using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ThixInstall.Controls;
using ThixInstall.Core.Business;
using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Platform;

namespace ThixInstall.Setup;

/// <summary>
/// The Windows setup assistant. ThixInstall installs software for the whole computer, so the work needs administrator
/// rights: the assistant (as the user) starts this program once more with "runas" ("--install --quiet --log …"), shows its
/// steps from the log file and, at the end, starts the installed app as the user. Already elevated, it works itself.
/// It cannot be closed halfway through.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class SetupWindow : ThemedWindow
{
    private readonly bool _isUninstall;
    private readonly bool _isUpdate;
    private readonly ObservableCollection<string> _steps = [];
    private bool _isWorking;
    private bool _isFinished;
    private bool _succeeded;

    /// <summary>For the XAML previewer only.</summary>
    public SetupWindow()
        : this(isUninstall: false)
    {
    }

    private SetupWindow(bool isUninstall)
    {
        InitializeComponent();
        var workArea = WorkArea();
        Width = Math.Min(Width, workArea.Width - 16);
        Height = Math.Min(Height, workArea.Height - 16);

        _isUninstall = isUninstall;
        _isUpdate = !isUninstall && InstallService.IsInstalled;
        StepList.ItemsSource = _steps;
        VersionText.Text = $"Version {(isUninstall ? InstallService.InstalledVersion ?? AppInfo.Version : AppInfo.Version)}";

        if (isUninstall)
        {
            ShowConfirmPage();
        }
        else
        {
            ShowWelcomePage();
        }
    }

    /// <summary>Shows the assistant; <paramref name="finished"/> is called when it was closed.</summary>
    public static void Run(bool isUninstall, Action<bool> finished)
    {
        var window = new SetupWindow(isUninstall);
        window.Closed += (_, _) => finished(window._succeeded);
        window.Show();
        window.Activate();
    }

    protected override void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        // Half an installation is worse than a slow one.
        e.Cancel = _isWorking;
        base.OnClosing(e);
    }

    private void ShowWelcomePage()
    {
        Title = _isUpdate ? "ThixInstall aktualisieren" : $"ThixInstall {AppInfo.Version} Setup";
        ModeText.Text = _isUpdate ? "Update" : "Setup";
        if (_isUpdate)
        {
            // An update keeps autostart, shortcuts and the cloud connection as they are: nothing to ask.
            WelcomeTitle.Text = "Update";
            WelcomeText.Text = $"Installiert ist {InstallService.InstalledVersion ?? "eine ältere Version"}, dieses Setup bringt {AppInfo.Version}. " +
                               "Der Dienst wird kurz angehalten und danach wieder gestartet. Verbindung, Aufträge und installierte Apps bleiben erhalten.";
            OptionsCard.IsVisible = false;
            CloudCard.IsVisible = false;
            PrimaryLabel.Text = "Aktualisieren";
        }
        else
        {
            AutostartToggle.IsChecked = true;
            DesktopToggle.IsChecked = InstallService.HasDesktopShortcut;
            AutoUpdateToggle.IsChecked = true;
            PrimaryLabel.Text = "Installieren";
        }

        PlaceInfo.Show($"Wird für alle Benutzer installiert nach {InstallService.ProgramFolder}, mit dem Dienst „{InstallService.ServiceDisplayName}“. " +
                       "Windows fragt dafür einmal nach Administratorrechten.", InfoSeverity.Info);
    }

    private void ShowConfirmPage()
    {
        Title = "ThixInstall deinstallieren";
        ModeText.Text = "Deinstallation";
        WelcomePage.IsVisible = false;
        ConfirmPage.IsVisible = true;
        PrimaryButton.Theme = Ui.Theme("DangerButton");
        PrimaryButton.IsDefault = false;
        PrimaryLabel.Icon = Ui.Icon("Icon.Delete");
        PrimaryLabel.Text = "Deinstallieren";
    }

    private async void OnPrimaryClick(object? sender, RoutedEventArgs e)
    {
        if (_isFinished)
        {
            if (_succeeded && !_isUninstall && StartToggle.IsChecked == true)
            {
                InstallService.StartInstalled();
            }

            Close();
            return;
        }

        await RunAsync();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private async Task RunAsync()
    {
        _isWorking = true;
        WelcomePage.IsVisible = ConfirmPage.IsVisible = false;
        ProgressPage.IsVisible = true;
        ProgressTitle.Text = _isUninstall ? "Entferne …" : _isUpdate ? "Aktualisiere …" : "Installiere …";
        PrimaryButton.IsEnabled = CancelButton.IsEnabled = false;

        var arguments = new List<string>();
        if (_isUninstall)
        {
            arguments.AddRange([InstallService.UninstallArgument, InstallService.QuietArgument]);
            if (DeleteDataToggle.IsChecked == true)
            {
                arguments.Add("--delete-data");
            }
        }
        else if (_isUpdate)
        {
            arguments.AddRange([InstallService.UpdateArgument, InstallService.QuietArgument]);
        }
        else
        {
            arguments.AddRange([InstallService.InstallArgument, InstallService.QuietArgument]);
            arguments.Add(AutostartToggle.IsChecked == true ? "--autostart" : "--no-autostart");
            if (DesktopToggle.IsChecked == true)
            {
                arguments.Add("--desktop");
            }

            if (AutoUpdateToggle.IsChecked != true)
            {
                arguments.Add("--no-auto-update");
            }

            if (!string.IsNullOrWhiteSpace(CloudBox.Text))
            {
                arguments.AddRange(["--cloud", CloudBox.Text.Trim()]);
                if (!string.IsNullOrWhiteSpace(KeyBox.Text))
                {
                    arguments.AddRange(["--enroll-key", KeyBox.Text.Trim()]);
                }
            }
        }

        var logFile = Path.Combine(Path.GetTempPath(), $"thixinstall-setup-{Guid.NewGuid():N}.log");
        arguments.AddRange(["--log", logFile]);
        string? error = null;
        var tail = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        tail.Tick += (_, _) => ShowLog(logFile);
        tail.Start();
        try
        {
            // The program in the program folder cannot uninstall itself while it runs from there: a copy in the temp folder does.
            var program = App.ExecutablePath;
            if (_isUninstall && InstallService.IsInstalledExecutable(program))
            {
                var copy = Path.Combine(Path.GetTempPath(), $"thixinstall-uninstall-{Guid.NewGuid():N}.exe");
                InstallService.CopyProgram(program, copy);
                program = copy;
            }

            var exitCode = await Elevation.RunElevatedAsync(program, string.Join(' ', arguments.Select(Elevation.Quote)));
            ShowLog(logFile);
            error = exitCode switch
            {
                null => "Ohne Administratorrechte kann ThixInstall nicht für diesen Computer eingerichtet werden.",
                0 => null,
                2 => null,
                _ => _steps.LastOrDefault(step => step.StartsWith("Fehler", StringComparison.Ordinal)) ?? $"Das Setup endete mit Code {exitCode}.",
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Error(_isUninstall ? "Uninstall failed" : "Setup failed", exception);
            error = exception.Message;
        }
        finally
        {
            tail.Stop();
            TryDelete(logFile);
        }

        _isWorking = false;
        ShowFinishPage(error);
    }

    private void ShowLog(string logFile)
    {
        try
        {
            if (!File.Exists(logFile))
            {
                return;
            }

            var lines = File.ReadAllLines(logFile);
            for (var index = _steps.Count; index < lines.Length; index++)
            {
                _steps.Add(lines[index]);
            }
        }
        catch (IOException)
        {
            // written right now: next time
        }
    }

    private void ShowFinishPage(string? error)
    {
        _isFinished = true;
        _succeeded = error is null;
        ProgressPage.IsVisible = false;
        FinishPage.IsVisible = true;
        CancelButton.IsVisible = false;
        PrimaryButton.IsEnabled = true;
        PrimaryButton.IsDefault = true;
        PrimaryButton.Theme = Ui.Theme("PrimaryButton");
        PrimaryLabel.Icon = Ui.Icon("Icon.Check");
        PrimaryLabel.Text = "Fertig";
        StartCard.IsVisible = _succeeded && !_isUninstall;

        if (error is not null)
        {
            FinishTitle.Text = _isUninstall ? "Deinstallation fehlgeschlagen" : "Installation fehlgeschlagen";
            ResultBar.Show($"{error}\n\nDetails stehen im Protokoll ({AppPaths.LogDirectory}).", InfoSeverity.Error);
            return;
        }

        FinishTitle.Text = _isUninstall ? "ThixInstall wurde entfernt" : "ThixInstall ist bereit";
        var cloudProblem = _steps.LastOrDefault(step => step.StartsWith("Die Verbindung zur Cloud", StringComparison.Ordinal));
        ResultBar.Show(_isUninstall
            ? DeleteDataToggle.IsChecked == true ? "Programm, Dienst und Cloud-Verbindung wurden entfernt." : "Programm und Dienst wurden entfernt. Die Cloud-Verbindung bleibt für eine spätere Installation erhalten."
            : cloudProblem ?? $"ThixInstall {AppInfo.Version} ist installiert. Es läuft im Infobereich der Taskleiste; der Dienst hält die Apps aktuell.",
            cloudProblem is null ? InfoSeverity.Success : InfoSeverity.Warning);
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
    }
}
