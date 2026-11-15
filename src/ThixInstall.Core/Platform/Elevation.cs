using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace ThixInstall.Core.Platform;

/// <summary>
/// Administrator rights. Windows: an elevated token (or LocalSystem); macOS: root. Installers of whole computers need them:
/// the Windows bundle runner and the setup start themselves once more with "runas" (the UAC prompt).
/// </summary>
public static partial class Elevation
{
    public static bool IsElevated => OperatingSystem.IsWindows() ? IsWindowsAdministrator() : IsRoot();

    /// <summary>Windows: this process is the service account LocalSystem.</summary>
    public static bool IsLocalSystem => OperatingSystem.IsWindows() && IsWindowsSystem();

    /// <summary>
    /// Starts this program again with administrator rights and waits for it. Returns its exit code, or null when the user
    /// declined the UAC prompt.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static async Task<int?> RunElevatedAsync(string executable, string arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
            });
            if (process is null)
            {
                return null;
            }

            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return null; // ERROR_CANCELLED: the user said no
        }
    }

    /// <summary>Quotes one argument for a Windows command line.</summary>
    public static string Quote(string argument) =>
        argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0 ? argument : "\"" + argument.Replace("\"", "\\\"") + "\"";

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem;
    }

    private static bool IsRoot()
    {
        try
        {
            return geteuid() == 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("libc")]
    private static partial uint geteuid();
}
