using System.IO.Pipes;
using System.Net.NetworkInformation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using ThixInstall.Core.Agent;
using ThixInstall.Core.Business;
using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Models;
using ThixInstall.Core.Platform;
using ThixInstall.Shared;
using TrayIcon = ThixInstall.Tray.TrayIcon;

namespace ThixInstall;

/// <summary>
/// Start-up and end of ThixInstall. What a start does:
///  - "--self-test": checks the published program.
///  - Windows "--install" / "--update" / "--uninstall": the setup without window (elevated; "--install" is also what the
///    setup assistant starts with administrator rights).
///  - a bundle ticket (appended to the program, a .thixinstall file): the bundle runner (Windows: elevated once).
///  - Windows, the published program outside the program folder: the setup assistant.
///  - everything else: the app (tray / menu bar, main window), one instance per user.
/// </summary>
public partial class App : Application
{
    public const string TrayArgument = "--tray";

    /// <summary>The elevated bundle runner tells the starter that ThixInstall itself was installed.</summary>
    private const int ExitAgentInstalled = 10;

    private const string ElevatedBundleArgument = "--bundle-elevated";

    // One instance per data folder: a development run with its own THIXINSTALL_DATA_DIR does not meet the installed one.
    private static readonly string InstanceSuffix = AppPaths.HasOverride
        ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppPaths.DataDirectory.ToUpperInvariant())))[..16]
        : "";

    private static readonly string InstanceName = "ThixInstall.SingleInstance" + InstanceSuffix;
    private static readonly string ShowWindowPipeName = "ThixInstall.ShowWindow" + InstanceSuffix + "." + Environment.UserName;

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private Mutex? _instanceMutex;
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private bool _isExiting;

    /// <summary>The program file; the autostart entry points to it.</summary>
    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ThixInstall");

    public static App Instance => (App)Current!;

    /// <summary>The main window (null in the setup and the bundle runner).</summary>
    public MainWindow? MainWindow => _mainWindow;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        _desktop = desktop;
        var args = desktop.Args ?? [];
        ApplyTheme(SettingsService.Get().Theme);

        var selfTest = Array.IndexOf(args, SelfTest.Argument);
        if (selfTest >= 0)
        {
            var report = selfTest + 1 < args.Length ? args[selfTest + 1] : Path.Combine(AppPaths.DataDirectory, "self-test.txt");
            Dispatcher.UIThread.Post(async () => EndProcess(await SelfTest.RunAsync(report)));
            return;
        }

        if (OperatingSystem.IsWindows() && RunWindowsSetupCommand(args))
        {
            return;
        }

        if (RunBundleIfRequested(args))
        {
            return;
        }

        if (OperatingSystem.IsWindows() && RunSetupAssistantIfNeeded())
        {
            return;
        }

        if (!TakeSingleInstance())
        {
            EndProcess(0);
            return;
        }

        Start(args);
    }

    /// <summary>
    /// Ends the process through the main loop. Shutting down straight from OnFrameworkInitializationCompleted throws
    /// "Dispatcher shut down" because the loop has not started yet.
    /// </summary>
    public void EndProcess(int exitCode) => Dispatcher.UIThread.Post(() => _desktop?.Shutdown(exitCode));

    private void Start(string[] args)
    {
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;
        UiWatchdog.Start();
        Log.Info($"ThixInstall {AppInfo.Version} starting on {AppInfo.PlatformKey} ({string.Join(' ', args)})");
        SettingsService.Changed += (_, settings) => Ui.Post(() => ApplyTheme(settings.Theme));

        // The agent: the Windows service when ThixInstall is installed there, otherwise in this process.
        AgentClient.Initialize(preferService: OperatingSystem.IsWindows() && InstallService.IsInstalled);
        AgentMonitor.IsWindowVisible = () => _mainWindow?.IsVisible == true;
        AgentMonitor.AgentUpdated += (_, _) => RestartAfterServiceUpdate();
        AgentMonitor.JobFinished += (_, job) => NotifyFinished(job);
        UpdateService.RestartRequested += (_, _) => Ui.Post(ExitApplication);

        _mainWindow = new MainWindow();
        _trayIcon = new TrayIcon(this);
        AgentMonitor.Start();

        var startHidden = args.Contains(TrayArgument) || args.Contains(AutostartService.StartupArgument);
        if (!startHidden)
        {
            _mainWindow.ShowFromTray();
        }

        Dispatcher.UIThread.Post(() => Dialogs.ProblemReportDialog.ShowUnseen(), DispatcherPriority.Background);

        // macOS: opening the app again (Finder, Spotlight) shows the window.
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, activation) =>
            {
                if (activation.Kind == ActivationKind.Reopen)
                {
                    ShowMainWindow();
                }
            };
        }

        // After sleep, a network change or a new Wi-Fi the cloud is asked at once (when the agent runs here).
        NetworkChange.NetworkAddressChanged += (_, _) =>
        {
            if (!AgentClient.UsesService)
            {
                CloudService.Kick();
            }
        };
    }

    /// <summary>Opens the main window (tray, second start, Dock) and optionally navigates.</summary>
    public void ShowMainWindow(Action<MainWindow>? navigate = null)
    {
        if (_mainWindow is null || _isExiting)
        {
            return;
        }

        _mainWindow.ShowFromTray();
        navigate?.Invoke(_mainWindow);
    }

    public void ApplyTheme(ThemeChoice theme) => RequestedThemeVariant = theme switch
    {
        ThemeChoice.Light => ThemeVariant.Light,
        ThemeChoice.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    /// <summary>"Beenden": ends the app (the Windows service keeps working; on macOS the agent ends with the app).</summary>
    public void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        UiWatchdog.Stop();
        AgentMonitor.Stop();
        Log.Info("ThixInstall exiting");
        if (!AgentClient.UsesService)
        {
            AgentHost.Stop();
        }

        _trayIcon?.Dispose();
        if (_mainWindow is not null)
        {
            _mainWindow.IsExiting = true;
            _mainWindow.Close();
        }

        _instanceMutex?.Dispose();
        _desktop?.Shutdown(0);
    }

    private void NotifyFinished(JobRecord job)
    {
        if (!SettingsService.Get().ShowNotifications || _isExiting)
        {
            return;
        }

        var failed = job.State == ActionState.Failed;
        if (job.State == ActionState.Cancelled || (job.Origin == ActionOrigin.Device && _mainWindow?.IsActive == true && !failed))
        {
            return;
        }

        _trayIcon?.ShowNotification(job.Title, AgentMonitor.StateText(job), isWarning: failed);
    }

    /// <summary>The service runs a newer ThixInstall than this app: start the new program and end this one.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416", Justification = "Only the Windows service updates itself.")]
    private void RestartAfterServiceUpdate()
    {
        if (_isExiting || !OperatingSystem.IsWindows() || !File.Exists(InstallService.InstalledExecutable))
        {
            return;
        }

        _instanceMutex?.Dispose();
        _instanceMutex = null;
        InstallService.StartInstalled(TrayArgument);
        ExitApplication();
    }

    // ---- Windows setup ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The setup commands without a window (each needs administrator rights; without them the program asks for them once):
    ///  "--install [--quiet] [--autostart|--no-autostart] [--desktop] [--no-auto-update] [--cloud url --enroll-key key] [--log file]"
    ///  "--update" (started by the service), "--uninstall [--quiet] [--delete-data]".
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private bool RunWindowsSetupCommand(string[] args)
    {
        var install = args.Contains(InstallService.InstallArgument);
        var update = args.Contains(InstallService.UpdateArgument);
        var uninstall = args.Contains(InstallService.UninstallArgument);
        if (!install && !update && !uninstall)
        {
            return false;
        }

        if (uninstall && !args.Contains(InstallService.QuietArgument))
        {
            // "Installierte Apps" › Deinstallieren: the assistant asks, then runs "--uninstall --quiet" elevated.
            Setup.SetupWindow.Run(isUninstall: true, _ => EndProcess(0));
            return true;
        }

        if (!Elevation.IsElevated)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                var code = await Elevation.RunElevatedAsync(ExecutablePath, string.Join(' ', args.Select(Elevation.Quote)));
                EndProcess(code ?? 1);
            });
            return true;
        }

        var logFile = ArgumentAfter(args, "--log");
        void Report(string line)
        {
            Log.Info(line);
            if (logFile is not null)
            {
                try
                {
                    File.AppendAllText(logFile, line + Environment.NewLine);
                }
                catch (IOException)
                {
                }
            }
        }

        Dispatcher.UIThread.Post(async () =>
        {
            var exitCode = 0;
            try
            {
                if (uninstall)
                {
                    if (InstallService.Uninstall(args.Contains("--delete-data"), Report))
                    {
                        InstallService.ScheduleProgramFolderRemoval();
                    }
                }
                else if (update)
                {
                    InstallService.Install(ExecutablePath, options: null, Report);
                }
                else
                {
                    var options = new InstallOptions(
                        Autostart: !args.Contains("--no-autostart"),
                        DesktopShortcut: args.Contains("--desktop"),
                        InstallUpdatesAutomatically: !args.Contains("--no-auto-update"));
                    var cloud = ArgumentAfter(args, "--cloud");
                    var key = ArgumentAfter(args, "--enroll-key");
                    InstallService.Install(ExecutablePath, options, Report, startService: cloud is null);
                    if (cloud is not null)
                    {
                        try
                        {
                            var info = await CloudService.ConnectAsync(new ConnectRequest(cloud, null, null, key, null), startLoop: false);
                            Report($"Mit {cloud} verbunden ({info.UserDisplayName}{(info.ProfileName is null ? "" : $", Profil {info.ProfileName}")})");
                        }
                        catch (BusinessException exception)
                        {
                            Report($"Die Verbindung zur Cloud ist fehlgeschlagen: {exception.Message}");
                            exitCode = 2;
                        }

                        InstallService.StartAgentService(Report);
                    }
                }
            }
            catch (Exception exception) when (exception is BusinessException or IOException or UnauthorizedAccessException or InvalidOperationException
                                                  or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
            {
                Report($"Fehler: {exception.Message}");
                Log.Error("Setup failed", exception);
                exitCode = 1;
            }

            EndProcess(exitCode);
        });
        return true;
    }

    /// <summary>The published program started from anywhere but the program folder: the setup assistant (install or update).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private bool RunSetupAssistantIfNeeded()
    {
#if SINGLE_FILE
        const bool isPublishedBuild = true;
#else
        const bool isPublishedBuild = false;
#endif
        if (!isPublishedBuild || InstallService.IsInstalledExecutable(ExecutablePath))
        {
            return false;
        }

        Setup.SetupWindow.Run(isUninstall: false, _ => EndProcess(0));
        return true;
    }

    // ---- Bundle -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A program with a bundle ticket installs the bundle. Windows: the window runs elevated (installers of the whole
    /// computer); the starter waits for it and starts the installed ThixInstall as the user when the bundle installed it.
    /// </summary>
    private bool RunBundleIfRequested(string[] args)
    {
        if (OperatingSystem.IsWindows() && InstallService.IsInstalledExecutable(ExecutablePath))
        {
            return false;
        }

        var ticket = BundleService.FindTicket(args);
        if (ticket is null)
        {
            return false;
        }

        AgentClient.UseInProcess();
        if (OperatingSystem.IsWindows() && !Elevation.IsElevated && !args.Contains(ElevatedBundleArgument))
        {
            RunBundleElevated(ticket);
            return true;
        }

        Bundle.BundleWindow.Run(ticket, agentInstalled =>
        {
            if (agentInstalled && OperatingSystem.IsMacOS())
            {
                BundleService.OpenInstalledApp();
            }

            EndProcess(agentInstalled ? ExitAgentInstalled : 0);
        });
        return true;
    }

    /// <summary>Windows: the bundle window runs elevated; this starter waits and opens the installed ThixInstall as the user.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void RunBundleElevated(BundleTicket ticket) => Dispatcher.UIThread.Post(async () =>
    {
        var forward = new List<string> { ElevatedBundleArgument };
        if (BundleTicket.ReadTrailer(ExecutablePath) is null)
        {
            forward.AddRange([BundleService.BundleArgument, ticket.CloudUrl, ticket.Token]);
        }

        var code = await Elevation.RunElevatedAsync(ExecutablePath, string.Join(' ', forward.Select(Elevation.Quote)));
        if (code is null)
        {
            Bundle.BundleWindow.ShowElevationDeclined(() => EndProcess(1));
            return;
        }

        if (code == ExitAgentInstalled && File.Exists(InstallService.InstalledExecutable))
        {
            InstallService.StartInstalled();
        }

        EndProcess(code.Value == ExitAgentInstalled ? 0 : code.Value);
    });

    // ---- Single instance ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The first instance owns the mutex and listens on a pipe; a second start knocks on the pipe (the first one shows
    /// its window) and ends. On Windows the setup may also ask the running instance to end.
    /// </summary>
    private bool TakeSingleInstance()
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceName, out var isFirst);
        if (!isFirst)
        {
            KnockOnRunningInstance();
            return false;
        }

        var listener = new Thread(ListenForSecondStarts) { IsBackground = true, Name = "ThixInstall second start listener" };
        listener.Start();
        if (OperatingSystem.IsWindows())
        {
            ListenForExitSignal();
        }

        return true;
    }

    private static void KnockOnRunningInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", ShowWindowPipeName, PipeDirection.Out);
            client.Connect(2000);
            client.WriteByte(1);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not reach the running instance: {exception.Message}");
        }
    }

    private void ListenForSecondStarts()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(ShowWindowPipeName, PipeDirection.In, 1);
                server.WaitForConnection();
                server.ReadByte();
                Ui.Post(() => ShowMainWindow());
            }
            catch (IOException exception)
            {
                Log.Warning($"Second start listener: {exception.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    /// <summary>The setup asks a running ThixInstall to end (update, uninstall).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ListenForExitSignal()
    {
        if (InstanceSuffix.Length > 0)
        {
            return; // a development run with its own data folder is none of the setup's business
        }

        var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstallService.ExitSignalName);
        var listener = new Thread(() =>
        {
            exitSignal.WaitOne();
            Ui.Post(() =>
            {
                Log.Info("Setup asked ThixInstall to end");
                ExitApplication();
            });
        })
        {
            IsBackground = true,
            Name = "ThixInstall exit signal listener",
        };
        listener.Start();
    }

    private static string? ArgumentAfter(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled error", e.Exception);
        e.Handled = true;
        if (e.Exception is BusinessException business)
        {
            _trayIcon?.ShowNotification("ThixInstall", business.Message, isWarning: true);
            return;
        }

        _trayIcon?.ShowNotification("Unerwarteter Fehler", $"{e.Exception.Message} – Details im Protokoll ({AppPaths.LogDirectory}).", isWarning: true);
    }
}
