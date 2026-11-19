using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Business;

/// <summary>What the update manifest of a Thix app names: the version, the package of every platform and the install options.</summary>
public sealed record ThixAppManifest(string Version, IReadOnlyDictionary<string, (string File, string? Sha256)> Packages, InstallerConfig? Installer);

/// <summary>
/// A Thix app (<see cref="InstallMethod.ThixApp"/>): the update manifest its CI publishes in the rolling release of its channel,
/// <c>https://github.com/&lt;repository&gt;/releases/download/release-channel/&lt;manifest&gt;</c> (the same file ThixLauncher and the app itself
/// read). It names the version, the package of every platform ("win-x64": the setup, "osx-arm64"/"osx-x64": the zipped bundle) with its
/// SHA-256 and - since SETUP-08 - the block "installer": what the setup offers. Windows on Arm takes the x64 setup.
/// </summary>
public static partial class ThixAppResolver
{
    public const string Channel = "release";

    /// <summary>"owner/name" of a GitHub repository.</summary>
    public static bool IsValidRepository(string? repository) => repository is not null && RepositoryRegex().IsMatch(repository);

    /// <summary>A manifest file name: "thixfred-update.json".</summary>
    public static bool IsValidManifest(string? manifest) => manifest is not null && ManifestRegex().IsMatch(manifest);

    /// <summary>The manifest of a package: its own name, or "&lt;last part of the repository in lower case&gt;-update.json".</summary>
    public static string ManifestOf(string repository, string? manifest) =>
        string.IsNullOrWhiteSpace(manifest) ? repository[(repository.IndexOf('/') + 1)..].ToLowerInvariant() + "-update.json" : manifest.Trim();

    public static string ManifestUrl(string repository, string manifest) => FileUrl(repository, manifest);

    public static string FileUrl(string repository, string file) =>
        $"https://github.com/{repository}/releases/download/{Channel}-channel/{Uri.EscapeDataString(file)}";

    /// <summary>Reads the manifest of the release channel; throws <see cref="ResolveException"/> when there is none or it is not usable.</summary>
    public static async Task<ThixAppManifest> ReadAsync(string repository, string? manifest, CancellationToken ct)
    {
        var url = ManifestUrl(repository, ManifestOf(repository, manifest));
        using var response = await Net.GetAsync(url, TimeSpan.FromSeconds(30), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new ResolveException($"Im Release-Kanal von {repository} gibt es kein Update-Manifest ({ManifestOf(repository, manifest)}).");
        }

        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>The parts of a manifest ThixInstall needs; throws <see cref="ResolveException"/> without a version.</summary>
    public static ThixAppManifest Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ResolveException("Das Update-Manifest nennt keine Version.");
        }

        var packages = new Dictionary<string, (string File, string? Sha256)>(StringComparer.Ordinal);
        if (root.TryGetProperty("packages", out var list) && list.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in list.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.Object && StringOf(entry.Value, "file") is { } file && IsSafeFileName(file))
                {
                    packages[entry.Name] = (file, StringOf(entry.Value, "sha256"));
                }
            }
        }

        // older manifests name the Windows setup only at the top
        if (!packages.ContainsKey("win-x64") && StringOf(root, "setup") is { } setup && IsSafeFileName(setup))
        {
            packages["win-x64"] = (setup, StringOf(root, "sha256"));
        }

        InstallerConfig? installer = null;
        if (root.TryGetProperty("installer", out var block) && block.ValueKind == JsonValueKind.Object)
        {
            installer = ThixAppSetup.Sanitize(block.Deserialize<InstallerConfig>(ApiContract.Json));
        }

        return new ThixAppManifest(version.Trim(), packages, installer);
    }

    /// <summary>The releases of one system: Windows the setup (x64 and Arm), macOS the zipped bundle per processor.</summary>
    public static IReadOnlyDictionary<CpuArchitecture, ResolvedRelease> ReleasesFor(ThixAppManifest manifest, string repository, string appBundleName, TargetOs os)
    {
        var releases = new Dictionary<CpuArchitecture, ResolvedRelease>();
        if (os == TargetOs.Windows)
        {
            if (manifest.Packages.TryGetValue("win-x64", out var setup))
            {
                var release = Release(setup, InstallerKind.Exe, ThixAppSetup.QuietArgument, null);
                releases[CpuArchitecture.X64] = release;
                releases[CpuArchitecture.Arm64] = manifest.Packages.TryGetValue("win-arm64", out var arm) ? Release(arm, InstallerKind.Exe, ThixAppSetup.QuietArgument, null) : release;
            }
        }
        else
        {
            foreach (var (key, architecture) in new[] { ("osx-arm64", CpuArchitecture.Arm64), ("osx-x64", CpuArchitecture.X64) })
            {
                if (manifest.Packages.TryGetValue(key, out var package))
                {
                    var kind = package.File.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase) ? InstallerKind.Dmg : InstallerKind.Zip;
                    releases[architecture] = Release(package, kind, null, appBundleName);
                }
            }
        }

        if (releases.Count == 0)
        {
            throw new ResolveException($"Das Update-Manifest von {repository} nennt kein Paket für {(os == TargetOs.Windows ? "Windows" : "macOS")}.");
        }

        return releases;

        ResolvedRelease Release((string File, string? Sha256) package, InstallerKind kind, string? arguments, string? bundle) =>
            new(InstallMethod.ThixApp, manifest.Version, FileUrl(repository, package.File), package.Sha256, kind, arguments, AppBundleName: bundle, FileName: package.File);
    }

    private static string? StringOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;

    private static bool IsSafeFileName(string file) =>
        file.Length is > 0 and < 200 && !file.Contains('/') && !file.Contains('\\') && !file.Contains("..", StringComparison.Ordinal) &&
        (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepositoryRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}\.json$")]
    private static partial Regex ManifestRegex();
}
