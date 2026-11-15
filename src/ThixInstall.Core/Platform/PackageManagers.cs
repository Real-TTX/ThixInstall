using System.Runtime.Versioning;

namespace ThixInstall.Core.Platform;

/// <summary>
/// Finds the package managers ThixInstall uses underneath: winget (also for the service: as LocalSystem it is not on the
/// path, its folder under WindowsApps is), Chocolatey and Homebrew.
/// </summary>
internal static class PackageManagers
{
    public static string? Winget => OperatingSystem.IsWindows() ? FindWinget() : null;

    public static string? Chocolatey => OperatingSystem.IsWindows() ? FindChocolatey() : null;

    public static string? Brew => OperatingSystem.IsMacOS()
        ? new[] { "/opt/homebrew/bin/brew", "/usr/local/bin/brew" }.FirstOrDefault(File.Exists)
        : null;

    [SupportedOSPlatform("windows")]
    private static string? FindWinget()
    {
        // As a user: the execution alias in %LocalAppData%\Microsoft\WindowsApps.
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (!Elevation.IsLocalSystem && File.Exists(alias))
        {
            return alias;
        }

        // As LocalSystem: the newest App Installer package of the machine.
        try
        {
            var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            var architecture = AppInfoArchitecture();
            return Directory.EnumerateDirectories(windowsApps, $"Microsoft.DesktopAppInstaller_*_{architecture}__8wekyb3d8bbwe")
                .Select(folder => Path.Combine(folder, "winget.exe"))
                .Where(File.Exists)
                .OrderByDescending(path => VersionOf(Path.GetFileName(Path.GetDirectoryName(path)!)))
                .FirstOrDefault() ?? (File.Exists(alias) ? alias : null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return File.Exists(alias) ? alias : null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? FindChocolatey()
    {
        var installation = Environment.GetEnvironmentVariable("ChocolateyInstall");
        var candidates = new[]
        {
            installation is null ? null : Path.Combine(installation, "bin", "choco.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin", "choco.exe"),
        };
        return candidates.FirstOrDefault(path => path is not null && File.Exists(path));
    }

    private static string AppInfoArchitecture() =>
        Infrastructure.AppInfo.Architecture == Shared.CpuArchitecture.Arm64 ? "arm64" : "x64";

    private static Version VersionOf(string folderName)
    {
        // Microsoft.DesktopAppInstaller_1.24.25200.0_x64__8wekyb3d8bbwe
        var parts = folderName.Split('_');
        return parts.Length > 1 && Version.TryParse(parts[1], out var version) ? version : new Version(0, 0);
    }
}
