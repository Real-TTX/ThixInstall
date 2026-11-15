using System.Reflection;
using System.Runtime.InteropServices;
using ThixInstall.Shared;

namespace ThixInstall.Core.Infrastructure;

/// <summary>Version, channel and build number baked in at build time (Directory.Build.props), and facts of this machine.</summary>
public static class AppInfo
{
    private static readonly Assembly ThisAssembly = typeof(AppInfo).Assembly;

    public const string ProductName = "ThixInstall";

    /// <summary>e.g. 0.1.42-20261114, nightly-43-20261114 or local-20261114.</summary>
    public static string Version { get; } =
        ThisAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "local";

    /// <summary>release, nightly or local.</summary>
    public static string Channel { get; } = ReadMetadata("ThixInstallChannel") ?? "local";

    public static int BuildNumber { get; } = int.TryParse(ReadMetadata("ThixInstallBuildNumber"), out var number) ? number : 0;

    /// <summary>owner/name of the GitHub repository that publishes the builds.</summary>
    public static string Repository { get; } = ReadMetadata("ThixInstallRepository") ?? "Real-TTX/ThixInstall";

    public static bool IsLocalBuild => Channel == "local";

    /// <summary>"Windows" or "macOS".</summary>
    public static string PlatformName => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : RuntimeInformation.OSDescription;

    /// <summary>The OS of the catalog this device uses.</summary>
    public static TargetOs Os => OperatingSystem.IsMacOS() ? TargetOs.MacOS : TargetOs.Windows;

    public static CpuArchitecture Architecture =>
        RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? CpuArchitecture.Arm64 : CpuArchitecture.X64;

    /// <summary>win-x64, win-arm64, osx-arm64 or osx-x64.</summary>
    public static string PlatformKey => ApiContract.PlatformKey(Os, Architecture);

    /// <summary>The name of this computer, offered as the device name for the cloud.</summary>
    public static string MachineName
    {
        get
        {
            try
            {
                return Environment.MachineName;
            }
            catch (InvalidOperationException)
            {
                return "Computer";
            }
        }
    }

    private static string? ReadMetadata(string key) =>
        ThisAssembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == key)?.Value;
}
