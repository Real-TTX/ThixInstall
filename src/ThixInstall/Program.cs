using Avalonia;
using Avalonia.Controls;
using ThixInstall.Core.Agent;
using ThixInstall.Core.Business;
using ThixInstall.Core.Infrastructure;

namespace ThixInstall;

internal static class Program
{
    /// <summary>
    /// One program for everything:
    ///   "--service"        the agent as the Windows service (no UI at all)
    ///   "--agent-console"  the same in a console (development)
    ///   everything else    Avalonia: the app, the bundle runner, the Windows setup (App decides)
    /// The app ends only through "Beenden" (tray / menu bar), not when its window closes.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (OperatingSystem.IsWindows() && args.Contains(InstallService.ServiceArgument))
        {
            AppPaths.IsMachineAgent = true;
            ProblemReports.Install(showMessageBox: false);
            AgentWindowsService.RunAsService();
            return 0;
        }

        if (OperatingSystem.IsWindows() && args.Contains("--agent-console"))
        {
            AppPaths.IsMachineAgent = !AppPaths.HasOverride;
            ProblemReports.Install(showMessageBox: false);
            AgentWindowsService.RunInConsole();
            return 0;
        }

        // First of all: no error ends ThixInstall without a report (and on Windows a message box).
        ProblemReports.Install(showMessageBox: !args.Contains(SelfTest.Argument));

        // The self test works in a fresh folder of its own (before anything reads the data folder).
        if (args.Contains(SelfTest.Argument))
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable,
                Path.Combine(Path.GetTempPath(), "thixinstall-self-test", Guid.NewGuid().ToString("N")));
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>Also used by the XAML previewer.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // ThixInstall lives in the menu bar; its window comes from there (and the Dock while it is open).
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .With(new Win32PlatformOptions { OverlayPopups = true })
            .LogToTrace();
}
