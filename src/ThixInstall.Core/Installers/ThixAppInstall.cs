using ThixInstall.Shared;

namespace ThixInstall.Core.Installers;

/// <summary>
/// What installing a Thix app (<see cref="InstallMethod.ThixApp"/>) needs besides the download: the arguments come from
/// <see cref="ThixAppSetup"/> (Windows: the setup with "--quiet" and the options; macOS: the app's own program with "--configure"
/// after the bundle was placed), and exit code 2 means the app refused the options and installed (or changed) nothing.
/// </summary>
internal static class ThixAppInstall
{
    /// <summary>The message of a refused setup: what the app wrote last (its reason, on stderr).</summary>
    public static string RefusedMessage(PackageDto package, string? output)
    {
        var message = (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return $"Das Setup von {package.Name} hat die Installationsoptionen abgelehnt und nichts geändert" + (string.IsNullOrEmpty(message) ? "." : $": {message}");
    }

    /// <summary>
    /// The program inside a macOS app bundle: Contents/MacOS/&lt;name of the bundle&gt; (every Thix app is built that way), else the only
    /// file there; null when there is none.
    /// </summary>
    public static string? MacExecutable(string bundle)
    {
        var folder = Path.Combine(bundle, "Contents", "MacOS");
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var named = Path.Combine(folder, Path.GetFileNameWithoutExtension(bundle));
        if (File.Exists(named))
        {
            return named;
        }

        var files = Directory.GetFiles(folder);
        return files.Length == 1 ? files[0] : null;
    }
}
