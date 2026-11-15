using ThixInstall.Core.Infrastructure;
using ThixInstall.Shared;

namespace ThixInstall.Core.Installers;

/// <summary>Downloads the installer of a plan from the cloud's cache into the agent's download folder.</summary>
internal static class Downloads
{
    /// <summary>Returns the local file; the caller deletes it with <see cref="Remove"/> after the installation.</summary>
    public static async Task<string> FetchAsync(InstallPlan plan, JobContext context)
    {
        if (plan.DownloadUrl is null)
        {
            throw new InvalidOperationException("The plan has no download.");
        }

        if (context.CloudUrl is null && !Uri.IsWellFormedUriString(plan.DownloadUrl, UriKind.Absolute))
        {
            throw new Models.BusinessException("Keine Cloud verbunden – der Installer kann nicht geladen werden.");
        }

        var name = SafeFileName(plan.FileName ?? Path.GetFileName(plan.DownloadUrl.Split('?')[0]), plan.InstallerKind);
        var folder = Path.Combine(AppPaths.DownloadDirectory, Guid.NewGuid().ToString("N")[..12]);
        var target = Path.Combine(folder, name);
        context.Report("Lade herunter …", 0);
        context.Step($"Lade {name} aus der Cloud{(plan.Size is { } size ? $" ({size / 1024.0 / 1024.0:0.0} MB)" : "")}");
        using var client = CloudClient.Create(context.CloudUrl ?? plan.DownloadUrl, context.Token);
        await client.DownloadAsync(plan.DownloadUrl, target, plan.Sha256, new Progress<double>(fraction => context.Report("Lade herunter …", fraction)), context.Cancellation);
        context.Step(string.IsNullOrWhiteSpace(plan.Sha256) ? "Heruntergeladen (keine Prüfsumme bekannt)" : "Heruntergeladen, Prüfsumme stimmt");
        return target;
    }

    public static void Remove(string? file)
    {
        if (file is null)
        {
            return;
        }

        try
        {
            var folder = Path.GetDirectoryName(file);
            if (folder is not null && Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not remove the download {file}: {exception.Message}");
        }
    }

    /// <summary>A file name without paths or odd characters, with the extension its kind needs.</summary>
    internal static string SafeFileName(string? name, InstallerKind? kind)
    {
        var clean = new string((name ?? "").Where(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_').ToArray()).Trim('.');
        if (clean.Length == 0)
        {
            clean = "installer";
        }

        var extension = kind switch
        {
            InstallerKind.Msi => ".msi",
            InstallerKind.Msix => clean.EndsWith(".appx", StringComparison.OrdinalIgnoreCase) || clean.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase) || clean.EndsWith(".appxbundle", StringComparison.OrdinalIgnoreCase) ? "" : ".msix",
            InstallerKind.Exe => ".exe",
            InstallerKind.Dmg => ".dmg",
            InstallerKind.Pkg => ".pkg",
            InstallerKind.Zip => ".zip",
            _ => "",
        };
        return extension.Length > 0 && !clean.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? clean + extension : clean;
    }
}
