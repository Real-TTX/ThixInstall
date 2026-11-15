using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ThixInstall.Core.Infrastructure;
using ThixInstall.Shared;

namespace ThixInstall.Core.Platform;

/// <summary>What the device tells the cloud about itself.</summary>
internal static partial class MachineInfo
{
    private static string? _hardwareId;

    public static DeviceFacts Facts(string? deviceName = null) => new(
        string.IsNullOrWhiteSpace(deviceName) ? AppInfo.MachineName : deviceName.Trim(),
        AppInfo.Os,
        OsVersion,
        AppInfo.Architecture,
        AppInfo.Version,
        HardwareId);

    /// <summary>"Windows 11 Pro 24H2 (26100)" or "macOS 15.1 (24B83)".</summary>
    public static string OsVersion
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsVersion();
            }

            if (OperatingSystem.IsMacOS())
            {
                var version = ProcessRunner.ReadOutput("/usr/bin/sw_vers", "-productVersion").Trim();
                var build = ProcessRunner.ReadOutput("/usr/bin/sw_vers", "-buildVersion").Trim();
                return version.Length > 0 ? $"macOS {version} ({build})" : RuntimeInformation.OSDescription;
            }

            return RuntimeInformation.OSDescription;
        }
    }

    /// <summary>Windows MachineGuid, macOS IOPlatformUUID: enrolling the same machine again finds its device.</summary>
    public static string? HardwareId => _hardwareId ??= ReadHardwareId();

    private static string? ReadHardwareId()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                return key?.GetValue("MachineGuid") as string;
            }

            if (OperatingSystem.IsMacOS())
            {
                var text = ProcessRunner.ReadOutput("/usr/sbin/ioreg", "-rd1", "-c", "IOPlatformExpertDevice");
                var match = PlatformUuid().Match(text);
                return match.Success ? match.Groups[1].Value : null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warning($"The hardware id could not be read: {exception.Message}");
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string WindowsVersion()
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = Environment.OSVersion.Version.Build;
        var product = key?.GetValue("ProductName") as string ?? "Windows";
        // Windows 11 still says "Windows 10" in ProductName.
        if (build >= 22000)
        {
            product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
        }

        var release = key?.GetValue("DisplayVersion") as string;
        return release is null ? $"{product} ({build})" : $"{product} {release} ({build})";
    }

    [GeneratedRegex("\"IOPlatformUUID\" = \"([^\"]+)\"")]
    private static partial Regex PlatformUuid();
}
