using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using ThixInstall.Core.Infrastructure;

namespace ThixInstall.Core.Platform;

/// <summary>One installed program as the system lists it.</summary>
internal sealed record InstalledEntry(
    string Name,
    string? Version,
    string? Publisher,
    string Source,
    // Windows: the key of the uninstall entry (an MSI product code for MSI packages); macOS: the bundle path or the brew name.
    string Key,
    string? UninstallCommand = null,
    string? QuietUninstallCommand = null,
    bool IsCask = false);

/// <summary>
/// Reads what is installed. Windows: the "Installierte Apps" entries in the registry (machine-wide 64 and 32 bit, the
/// current user, and - for the service - every signed-in user). macOS: the app bundles in /Applications and
/// ~/Applications, and Homebrew's formulae and casks.
/// </summary>
internal static class InstalledApps
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IReadOnlyList<InstalledEntry> Read()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return ReadWindows();
            }

            if (OperatingSystem.IsMacOS())
            {
                return ReadMac();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warning($"The installed apps could not be read: {exception.Message}");
        }

        return [];
    }

    [SupportedOSPlatform("windows")]
    private static List<InstalledEntry> ReadWindows()
    {
        var entries = new Dictionary<string, InstalledEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            ReadUninstallKey(machine, entries);
        }

        if (Elevation.IsLocalSystem)
        {
            // The service sees the users through HKEY_USERS (only the profiles that are loaded: the users signed in).
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
            foreach (var sid in users.GetSubKeyNames().Where(name => name.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !name.EndsWith("_Classes", StringComparison.Ordinal)))
            {
                using var user = users.OpenSubKey(sid);
                if (user is not null)
                {
                    ReadUninstallKey(user, entries);
                }
            }
        }
        else
        {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            ReadUninstallKey(user, entries);
        }

        return entries.Values.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    [SupportedOSPlatform("windows")]
    private static void ReadUninstallKey(RegistryKey root, Dictionary<string, InstalledEntry> entries)
    {
        using var uninstall = root.OpenSubKey(UninstallPath);
        if (uninstall is null)
        {
            return;
        }

        foreach (var keyName in uninstall.GetSubKeyNames())
        {
            using var key = uninstall.OpenSubKey(keyName);
            if (key?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            // Hidden parts, updates and hotfixes are not apps of their own.
            if (key.GetValue("SystemComponent") is int hidden && hidden == 1 ||
                key.GetValue("ParentKeyName") is string ||
                key.GetValue("ReleaseType") is string release && (release.Contains("Update", StringComparison.OrdinalIgnoreCase) || release.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var entry = new InstalledEntry(
                name.Trim(),
                (key.GetValue("DisplayVersion") as string)?.Trim(),
                (key.GetValue("Publisher") as string)?.Trim(),
                "registry",
                keyName,
                key.GetValue("UninstallString") as string,
                key.GetValue("QuietUninstallString") as string);
            entries.TryAdd(keyName + "|" + entry.Name, entry);
        }
    }

    private static List<InstalledEntry> ReadMac()
    {
        var entries = new List<InstalledEntry>();
        var folders = new[]
        {
            "/Applications",
            "/Applications/Utilities",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"),
        };
        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var bundle in Directory.EnumerateDirectories(folder, "*.app"))
            {
                var version = MacBundleVersion(bundle);
                entries.Add(new InstalledEntry(Path.GetFileNameWithoutExtension(bundle), version, null, "app", bundle));
            }
        }

        if (PackageManagers.Brew is { } brew)
        {
            entries.AddRange(ReadBrew(brew, cask: false));
            entries.AddRange(ReadBrew(brew, cask: true));
        }

        return entries;
    }

    /// <summary>CFBundleShortVersionString of an app bundle (plutil reads binary and XML property lists).</summary>
    public static string? MacBundleVersion(string bundle)
    {
        var plist = Path.Combine(bundle, "Contents", "Info.plist");
        if (!File.Exists(plist))
        {
            return null;
        }

        var version = ProcessRunner.ReadOutput("/usr/bin/plutil", "-extract", "CFBundleShortVersionString", "raw", "-o", "-", plist).Trim();
        return version.Length > 0 ? version : null;
    }

    private static IEnumerable<InstalledEntry> ReadBrew(string brew, bool cask)
    {
        // "brew info --json=v2 --installed" names every installed formula and cask with its version.
        var json = ProcessRunner.ReadOutput(brew, "info", "--json=v2", "--installed", cask ? "--cask" : "--formula");
        if (json.Length == 0)
        {
            yield break;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            var list = document.RootElement.TryGetProperty(cask ? "casks" : "formulae", out var items) ? items : default;
            if (list.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var item in list.EnumerateArray().ToList())
            {
                var name = item.TryGetProperty("token", out var token) ? token.GetString() : item.TryGetProperty("name", out var formula) ? formula.GetString() : null;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                string? version = null;
                if (cask && item.TryGetProperty("installed", out var installed) && installed.ValueKind == JsonValueKind.String)
                {
                    version = installed.GetString();
                }
                else if (!cask && item.TryGetProperty("installed", out var versions) && versions.ValueKind == JsonValueKind.Array && versions.GetArrayLength() > 0 &&
                         versions[versions.GetArrayLength() - 1].TryGetProperty("version", out var last))
                {
                    version = last.GetString();
                }

                yield return new InstalledEntry(name, version, null, "brew", name, IsCask: cask);
            }
        }
    }
}
