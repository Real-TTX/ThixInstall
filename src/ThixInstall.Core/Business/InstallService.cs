using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Win32;
using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Models;
using ThixInstall.Core.Platform;
using ThixInstall.Shared;

namespace ThixInstall.Core.Business;

/// <summary>
/// Windows only (macOS: the app bundle, <see cref="MacAppBundle"/>). ThixInstall installs itself, without an installer
/// tool, for the whole computer (it installs software for everybody, so it needs administrator rights once):
///   %ProgramFiles%\ThixInstall\ThixInstall.exe, the service "ThixInstall Agent" (LocalSystem, starts with Windows: the
///   agent), the Start menu entry and – as the install options say (<see cref="InstallerOptions"/>, SETUP-08) – a desktop
///   shortcut for all users, the autostart of the app (HKLM Run) and automatic updates; and the entry in "Installierte Apps".
///   The agent's data lives in %ProgramData%\ThixInstall (SYSTEM and administrators only).
/// The published ThixInstall.exe is the setup ("ThixInstallSetup.exe"); "--update" is the silent update the service starts,
/// "--uninstall" the uninstaller.
/// </summary>
[SupportedOSPlatform("windows")]
public static class InstallService
{
    public const string InstallArgument = InstallerOptions.InstallArgument;
    public const string UpdateArgument = InstallerOptions.UpdateArgument;
    public const string UninstallArgument = InstallerOptions.UninstallArgument;
    public const string QuietArgument = InstallerOptions.QuietArgument;
    public const string ServiceArgument = "--service";
    public const string ExecutableName = "ThixInstall.exe";
    public const string ServiceName = "ThixInstallAgent";
    public const string ServiceDisplayName = "ThixInstall Agent";

    /// <summary>A running app (in this session) listens for this signal and ends itself cleanly.</summary>
    public const string ExitSignalName = "ThixInstall.Exit";

    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ThixInstall";

    public static string ProgramFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppInfo.ProductName);

    public static string InstalledExecutable => Path.Combine(ProgramFolder, ExecutableName);

    public static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), $"{AppInfo.ProductName}.lnk");

    public static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), $"{AppInfo.ProductName}.lnk");

    /// <summary>Installed means: the entry in "Installierte Apps" and the program file exist.</summary>
    public static bool IsInstalled
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(UninstallKeyPath);
            return key is not null && File.Exists(InstalledExecutable);
        }
    }

    public static string? InstalledVersion
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(UninstallKeyPath);
            return key?.GetValue("DisplayVersion") as string;
        }
    }

    /// <summary>The agent service runs (the app then talks to it instead of hosting the agent itself).</summary>
    public static bool IsServiceRunning
    {
        get
        {
            try
            {
                using var service = new ServiceController(ServiceName);
                return service.Status == ServiceControllerStatus.Running;
            }
            catch (InvalidOperationException)
            {
                return false; // not installed
            }
        }
    }

    public static bool HasDesktopShortcut => File.Exists(DesktopShortcut);

    public static bool IsInstalledExecutable(string path) =>
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(InstalledExecutable), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Installs or updates (administrator rights needed) with the install options (SETUP-08): what <paramref name="choices"/>
    /// leaves open (null, or a value of null) an installed ThixInstall keeps as it is and a first install takes from
    /// installer.json. <paramref name="startService"/> false: the caller connects the device first and then starts the
    /// service with <see cref="StartAgentService"/>.
    /// </summary>
    public static void Install(string sourceExecutable, InstallChoices? choices, Action<string> log, bool startService = true)
    {
        if (!Elevation.IsElevated)
        {
            throw new BusinessException("Für die Installation werden Administratorrechte benötigt.");
        }

        var isUpdate = IsInstalled;
        log(isUpdate ? $"Aktualisiere ThixInstall in {ProgramFolder}" : $"Installiere ThixInstall nach {ProgramFolder}");
        StopService(log);
        SignalRunningApps();

        Directory.CreateDirectory(ProgramFolder);
        if (!IsInstalledExecutable(sourceExecutable))
        {
            ReplaceExecutable(sourceExecutable);
            log("Programmdatei kopiert");
        }

        CreateShortcut(StartMenuShortcut);
        log("Startmenü-Eintrag angelegt");

        // The agent's folder: machine-wide, only for SYSTEM and administrators (it holds the device token).
        AppPaths.IsMachineAgent = true;
        SettingsStore.Reset();
        Directory.CreateDirectory(AppPaths.AgentDirectory);
        Platform.Windows.MachineFolder.Secure(AppPaths.AgentDirectory);

        // also an update writes them again: the autostart points at the installed file
        var wanted = (choices ?? new InstallChoices()).Over(isUpdate ? InstallerOptions.Current() : InstallerOptions.Defaults);
        InstallerOptions.Apply(wanted, InstalledExecutable, log);

        WriteUninstallEntry();
        log("Eintrag unter „Installierte Apps“ geschrieben");

        RegisterService(log);
        if (startService)
        {
            StartService(log);
        }

        log(isUpdate ? "Update abgeschlossen" : "Installation abgeschlossen");
    }

    /// <summary>
    /// Removes service, program, shortcuts, autostart and the entry in "Installierte Apps"; the agent's data (cloud
    /// connection, jobs) only on request. Returns true when the program folder could not be deleted yet because this
    /// process runs from it - then call <see cref="ScheduleProgramFolderRemoval"/> right before the process ends.
    /// </summary>
    public static bool Uninstall(bool deleteData, Action<string> log)
    {
        if (!Elevation.IsElevated)
        {
            throw new BusinessException("Für die Deinstallation werden Administratorrechte benötigt.");
        }

        log("Entferne ThixInstall");
        StopService(log);
        RunSc($"delete {ServiceName}");
        log("Dienst entfernt");
        SignalRunningApps();

        AppPaths.IsMachineAgent = true;
        AutostartService.RemoveEntry();
        log("Autostart entfernt");
        DeleteFile(StartMenuShortcut);
        DeleteFile(DesktopShortcut);
        log("Verknüpfungen entfernt");
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
        log("Eintrag unter „Installierte Apps“ entfernt");

        if (deleteData)
        {
            DeleteFolder(AppPaths.AgentDirectory);
            log("Cloud-Verbindung, Aufträge und Protokolle gelöscht");
        }

        var runsFromProgramFolder = (Environment.ProcessPath ?? "").StartsWith(ProgramFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (runsFromProgramFolder)
        {
            log("Programmordner wird nach dem Schließen gelöscht");
            return true;
        }

        DeleteFolder(ProgramFolder);
        log("Programmordner gelöscht");
        return false;
    }

    /// <summary>The program folder cannot go while this exe runs from it: a short-lived cmd removes it afterwards.</summary>
    public static void ScheduleProgramFolderRemoval() =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{ProgramFolder}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });

    /// <summary>Starts the installed app (after setup or update), as the user who runs this process.</summary>
    public static void StartInstalled(string arguments = "") =>
        Process.Start(new ProcessStartInfo(InstalledExecutable, arguments) { UseShellExecute = false, WorkingDirectory = ProgramFolder });

    /// <summary>Program files of earlier versions that were still running during an update (removed at the next start).</summary>
    public static void RemoveOldExecutables()
    {
        if (!Directory.Exists(ProgramFolder))
        {
            return;
        }

        foreach (var old in Directory.EnumerateFiles(ProgramFolder, ExecutableName + ".old-*"))
        {
            DeleteFile(old, quiet: true);
        }
    }

    /// <summary>The program file is in use by the apps of signed-in users: it is renamed (Windows allows that) and replaced.</summary>
    private static void ReplaceExecutable(string source)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(InstalledExecutable))
                {
                    try
                    {
                        File.Delete(InstalledExecutable);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        File.Move(InstalledExecutable, $"{InstalledExecutable}.old-{DateTime.Now.Ticks}");
                    }
                }

                CopyProgram(source, InstalledExecutable);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>
    /// Copies the program without a bundle ticket: a bundle that installs ThixInstall carries one, the installed program
    /// must not (it would start as the bundle again).
    /// </summary>
    internal static void CopyProgram(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = Shared.BundleTicket.ProgramLength(input);
        input.Seek(0, SeekOrigin.Begin);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[1024 * 1024];
        var left = length;
        while (left > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (read <= 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            left -= read;
        }
    }

    /// <summary>Starts the agent service (after the setup connected the device to a cloud).</summary>
    public static void StartAgentService(Action<string> log) => StartService(log);

    private static void RegisterService(Action<string> log)
    {
        var binaryPath = $"\\\"{InstalledExecutable}\\\" {ServiceArgument}";
        var exists = ServiceExists();
        var result = exists
            ? RunSc($"config {ServiceName} binPath= \"{binaryPath}\" start= auto DisplayName= \"{ServiceDisplayName}\"")
            : RunSc($"create {ServiceName} binPath= \"{binaryPath}\" start= auto DisplayName= \"{ServiceDisplayName}\"");
        if (result != 0)
        {
            throw new BusinessException($"Der Dienst konnte nicht eingerichtet werden (sc {result}).");
        }

        RunSc($"description {ServiceName} \"Installiert und aktualisiert Apps und führt Aufträge von ThixInstall.Cloud aus.\"");
        RunSc($"failure {ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/300000");
        log(exists ? "Dienst „ThixInstall Agent“ aktualisiert" : "Dienst „ThixInstall Agent“ eingerichtet");
    }

    private static void StartService(Action<string> log)
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            if (service.Status != ServiceControllerStatus.Running)
            {
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }

            log("Dienst gestartet");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            log($"Der Dienst startet nicht: {exception.Message}");
            Log.Error("Service start failed", exception);
        }
    }

    private static void StopService(Action<string> log)
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            if (service.Status is ServiceControllerStatus.Stopped)
            {
                return;
            }

            log("Halte den Dienst an");
            if (service.CanStop)
            {
                service.Stop();
            }

            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
        }
        catch (InvalidOperationException)
        {
            // not installed
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            log("Der Dienst hält nicht an – er wird beendet");
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExecutableName)))
            {
                using (process)
                {
                    try
                    {
                        if (process.Id != Environment.ProcessId && process.SessionId == 0)
                        {
                            process.Kill();
                        }
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                    }
                }
            }
        }
    }

    private static bool ServiceExists()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            _ = service.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Asks the app of this session to end (setup, uninstall); apps of other users restart themselves later.</summary>
    private static void SignalRunningApps()
    {
        if (EventWaitHandle.TryOpenExisting(ExitSignalName, out var exitSignal))
        {
            exitSignal.Set();
            exitSignal.Dispose();
            Thread.Sleep(1500);
        }
    }

    private static int RunSc(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("sc.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            Log.Warning($"sc {arguments.Split(' ')[0]}: {process.ExitCode} {output.Trim()}");
        }

        return process.ExitCode;
    }

    /// <summary>The desktop shortcut for all users on or off (install options).</summary>
    internal static void SetDesktopShortcut(bool wanted)
    {
        if (wanted)
        {
            CreateShortcut(DesktopShortcut);
        }
        else
        {
            DeleteFile(DesktopShortcut, quiet: true);
        }
    }

    private static void CreateShortcut(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Platform.Windows.ShellLink.Write(path, InstalledExecutable, ProgramFolder, "Apps installieren und aktualisieren", InstalledExecutable);
    }

    private static void WriteUninstallEntry()
    {
        using var key = Registry.LocalMachine.CreateSubKey(UninstallKeyPath);
        key.SetValue("DisplayName", AppInfo.ProductName);
        key.SetValue("DisplayVersion", AppInfo.Version);
        key.SetValue("Publisher", "Matthias Schmoldt");
        key.SetValue("DisplayIcon", InstalledExecutable + ",0");
        key.SetValue("InstallLocation", ProgramFolder);
        key.SetValue("UninstallString", $"\"{InstalledExecutable}\" {UninstallArgument}");
        key.SetValue("QuietUninstallString", $"\"{InstalledExecutable}\" {UninstallArgument} {QuietArgument}");
        key.SetValue("URLInfoAbout", $"https://github.com/{AppInfo.Repository}");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExecutable).Length / 1024), RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
    }

    private static void DeleteFile(string path, bool quiet = false)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!quiet)
            {
                Log.Warning($"Could not delete {path}: {exception.Message}");
            }
        }
    }

    private static void DeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not delete {path}: {exception.Message}");
        }
    }
}
