using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace ThixInstall.Core.Business;

/// <summary>
/// The app starts at sign-in (tray / menu bar). The entry itself is the truth; the settings page shows what it finds.
///   Windows installed: a value in HKLM\...\Run for every user (the setup and the service write it, uninstalling removes it);
///                     not installed (development): HKCU\...\Run.
///   macOS:            a LaunchAgent ~/Library/LaunchAgents/info.schmoldt.thixinstall.plist (RunAtLoad).
/// The agent itself needs no autostart: on Windows it is a service that starts with the computer.
/// </summary>
public static class AutostartService
{
    public const string StartupArgument = "--autostart";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ThixInstall";
    private const string LaunchAgentLabel = "info.schmoldt.thixinstall";

    private static string? _launchAgentFolderForTests;

    /// <summary>The LaunchAgent file (macOS).</summary>
    public static string LaunchAgentFile => Path.Combine(
        _launchAgentFolderForTests ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents"),
        LaunchAgentLabel + ".plist");

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static bool IsEnabled()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = RunRoot().OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string command && command.Length > 0;
        }

        return OperatingSystem.IsMacOS() && File.Exists(LaunchAgentFile);
    }

    public static void Enable()
    {
        AccessPolicy.EnsureConfigurationEditable();
        WriteEntry(ProgramToStart());
    }

    public static void Disable()
    {
        AccessPolicy.EnsureConfigurationEditable();
        RemoveEntry();
    }

    /// <summary>For the setup: installing and uninstalling is not a configuration change.</summary>
    internal static void WriteEntry(string executablePath)
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = RunRoot().CreateSubKey(RunKeyPath);
            key.SetValue(ValueName, $"\"{executablePath}\" {StartupArgument}");
        }
        else if (OperatingSystem.IsMacOS())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LaunchAgentFile)!);
            File.WriteAllText(LaunchAgentFile, LaunchAgent(executablePath));
        }
    }

    internal static void RemoveEntry()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = RunRoot().OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        else if (File.Exists(LaunchAgentFile))
        {
            File.Delete(LaunchAgentFile);
        }
    }

    /// <summary>launchd starts the program at sign-in; it does not restart it when the user ends it (no KeepAlive).</summary>
    internal static string LaunchAgent(string executablePath) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key>
          <string>{LaunchAgentLabel}</string>
          <key>ProgramArguments</key>
          <array>
            <string>{SecurityElement.Escape(executablePath)}</string>
            <string>{StartupArgument}</string>
          </array>
          <key>RunAtLoad</key>
          <true/>
          <key>ProcessType</key>
          <string>Interactive</string>
        </dict>
        </plist>

        """;

    internal static void UseLaunchAgentFolderForTests(string? folder) => _launchAgentFolderForTests = folder;

    private static string ProgramToStart() =>
        OperatingSystem.IsWindows() && InstallService.IsInstalled
            ? InstallService.InstalledExecutable
            : Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ThixInstall");

    /// <summary>The installed program starts for every user (HKLM); a development build for this user (HKCU).</summary>
    [SupportedOSPlatform("windows")]
    private static RegistryKey RunRoot() =>
        InstallService.IsInstalled || Infrastructure.AppPaths.IsMachineAgent ? Registry.LocalMachine : Registry.CurrentUser;
}
