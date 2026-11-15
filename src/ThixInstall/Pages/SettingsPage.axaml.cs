using Avalonia.Controls;
using Avalonia.Interactivity;
using ThixInstall.Controls;
using ThixInstall.Core.Agent;
using ThixInstall.Core.Business;
using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Models;
using ThixInstall.Dialogs;

namespace ThixInstall.Pages;

/// <summary>
/// The settings: the app's own (look, notifications) through <see cref="SettingsService"/>, the agent's (cloud, sync,
/// updates, autostart) through the agent. A switch applies at once; the business layer says no with a message.
/// </summary>
public partial class SettingsPage : UserControl
{
    public const string GeneralTab = "general";
    public const string CloudTab = "cloud";
    public const string UpdatesTab = "updates";

    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        LoadAppSettings();
        DeviceNameBox.Text = AppInfo.MachineName;

        ThemeChoice.SelectionChanged += (_, _) => Save(settings => settings with { Theme = (Core.Models.ThemeChoice)Math.Max(0, ThemeChoice.SelectedIndex) });
        NotificationsSwitch.IsCheckedChanged += (_, _) => Save(settings => settings with { ShowNotifications = NotificationsSwitch.IsChecked == true });
        AutostartSwitch.IsCheckedChanged += async (_, _) => await ChangeAgentAsync(new AgentSettingsChange(Autostart: AutostartSwitch.IsChecked == true));
        SyncSwitch.IsCheckedChanged += async (_, _) => await ChangeAgentAsync(new AgentSettingsChange(SyncEnabled: SyncSwitch.IsChecked == true));
        CheckUpdatesSwitch.IsCheckedChanged += async (_, _) => await ChangeAgentAsync(new AgentSettingsChange(CheckForUpdates: CheckUpdatesSwitch.IsChecked == true));
        AutoInstallSwitch.IsCheckedChanged += async (_, _) => await ChangeAgentAsync(new AgentSettingsChange(InstallUpdatesAutomatically: AutoInstallSwitch.IsChecked == true));
        AutoAppsSwitch.IsCheckedChanged += async (_, _) => await ChangeAgentAsync(new AgentSettingsChange(AutoUpdateApps: AutoAppsSwitch.IsChecked == true));
        AgentMonitor.StatusChanged += (_, status) => ApplyStatus(status);
        if (AgentMonitor.Status is { } status)
        {
            ApplyStatus(status);
        }
    }

    public void SelectTab(string tab) => Tabs.SelectedItem = tab switch
    {
        CloudTab => CloudTabItem,
        UpdatesTab => UpdatesTabItem,
        _ => GeneralTabItem,
    };

    private void LoadAppSettings()
    {
        _loading = true;
        var settings = SettingsService.Get();
        ThemeChoice.SelectedIndex = (int)settings.Theme;
        NotificationsSwitch.IsChecked = settings.ShowNotifications;
        _loading = false;
    }

    private void ApplyStatus(AgentStatus status)
    {
        _loading = true;
        try
        {
            var locked = status.IsConfigurationLocked;
            PolicyNote.IsVisible = locked || status.IsCloudDisabled || status.AreUpdatesDisabled;
            AutostartSwitch.IsChecked = status.AutostartEnabled;
            AutostartSwitch.IsEnabled = !locked;
            ThemeChoice.IsEnabled = NotificationsSwitch.IsEnabled = !locked;

            var connected = status.ServerUrl is not null;
            ConnectedPanel.IsVisible = connected;
            ConnectPanel.IsVisible = !connected;
            ConnectButton.IsEnabled = !locked && !status.IsCloudDisabled;
            if (connected)
            {
                ServerText.Text = status.ServerUrl;
                AccountText.Text = status.UserName;
                DeviceText.Text = status.DeviceName;
                ProfileText.Text = status.ProfileName ?? "–";
                StateText.Text = status.CloudMessage + (status.LastSync is { } last ? $" · zuletzt {Ui.FormatTime(last)}" : "");
                SyncSwitch.IsChecked = status.SyncEnabled;
                SyncSwitch.IsEnabled = !locked;
                DisconnectButton.IsEnabled = !locked;
            }

            SelfUpdateTitle.Text = status.AvailableUpdate is { } update ? $"Version {update} ist verfügbar" : $"ThixInstall {status.AgentVersion}";
            SelfUpdateText.Text = status.AreUpdatesDisabled ? "Updates wurden von einem Administrator deaktiviert."
                : status.UpdateMessage is { } message ? message + (status.LastUpdateCheck is { } checkedAt ? $" ({Ui.FormatTime(checkedAt)})" : "")
                : "Noch nicht geprüft.";
            InstallUpdateButton.IsVisible = status.AvailableUpdate is not null && !status.AreUpdatesDisabled;
            CheckUpdateButton.IsEnabled = !status.AreUpdatesDisabled;
            CheckUpdatesSwitch.IsChecked = status.CheckForUpdates;
            AutoInstallSwitch.IsChecked = status.InstallUpdatesAutomatically;
            AutoAppsSwitch.IsChecked = status.AutoUpdateApps;
            CheckUpdatesSwitch.IsEnabled = AutoInstallSwitch.IsEnabled = !locked && !status.AreUpdatesDisabled;
            AutoAppsSwitch.IsEnabled = !locked;

            AgentText.Text = status.IsService
                ? $"Windows-Dienst „ThixInstall Agent“ · {status.AgentVersion}"
                : $"Im Programm · {status.AgentVersion}";
            RightsText.Text = status.IsElevated
                ? "Administrator – Apps werden für alle Benutzer installiert."
                : "Benutzer – Installationen für den ganzen Computer fragen nach einem Administrator.";
            ManagersText.Text = PackageManagerText();
            var policies = new List<string>();
            if (status.IsConfigurationLocked)
            {
                policies.Add("Einstellungen gesperrt");
            }

            if (status.IsCloudDisabled)
            {
                policies.Add("Cloud deaktiviert");
            }

            if (status.AreUpdatesDisabled)
            {
                policies.Add("Updates deaktiviert");
            }

            PoliciesText.Text = policies.Count == 0 ? "Keine" : string.Join(" · ", policies);
        }
        finally
        {
            _loading = false;
        }
    }

    private static string PackageManagerText()
    {
        if (OperatingSystem.IsMacOS())
        {
            return Core.Platform.PackageManagers.Brew is { } brew ? $"Homebrew ({brew})" : "Homebrew nicht installiert – Casks kommen aus dem Cloud-Cache.";
        }

        var parts = new List<string>
        {
            Core.Platform.PackageManagers.Winget is not null ? "winget" : "winget fehlt",
            Core.Platform.PackageManagers.Chocolatey is not null ? "Chocolatey" : "Chocolatey nicht installiert",
        };
        return string.Join(" · ", parts);
    }

    private void Save(Func<AppSettings, AppSettings> change)
    {
        if (_loading)
        {
            return;
        }

        try
        {
            SettingsService.Update(change);
            PageMessage.Hide();
        }
        catch (BusinessException exception)
        {
            PageMessage.Show(exception.Message, InfoSeverity.Error);
            LoadAppSettings();
        }
    }

    private async Task ChangeAgentAsync(AgentSettingsChange change)
    {
        if (_loading)
        {
            return;
        }

        try
        {
            await AgentClient.ChangeSettingsAsync(change);
            PageMessage.Hide();
        }
        catch (BusinessException exception)
        {
            PageMessage.Show(exception.Message, InfoSeverity.Error);
        }

        await AgentMonitor.RefreshNowAsync();
    }

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        var byKey = ConnectModeTabs.SelectedIndex == 1;
        var request = byKey
            ? new ConnectRequest(KeyServerBox.Text ?? "", null, null, KeyBox.Text, DeviceNameBox.Text, ConnectSyncSwitch.IsChecked == true)
            : new ConnectRequest(ServerBox.Text ?? "", UserBox.Text, PasswordBox.Text, null, DeviceNameBox.Text, ConnectSyncSwitch.IsChecked == true);
        ConnectButton.IsEnabled = false;
        ConnectLabel.Text = "Verbinde …";
        try
        {
            FormField.ShowError(this, null, "");
            var info = await AgentClient.ConnectAsync(request);
            PasswordBox.Text = "";
            PageMessage.Show($"Verbunden mit {info.UserDisplayName} als „{info.Name}“.", InfoSeverity.Success);
            await AgentMonitor.RefreshNowAsync();
        }
        catch (BusinessException exception)
        {
            if (!FormField.ShowError(this, exception.Field, exception.Message))
            {
                PageMessage.Show(exception.Message, InfoSeverity.Error);
            }
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            ConnectLabel.Text = "Verbinden";
        }
    }

    private async void OnDisconnectClick(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmDialog.AskAsync(Ui.WindowOf(this), "Von der Cloud trennen?",
                "Das Gerät verlässt die Cloud: der Katalog verschwindet hier, die Cloud kann keine Aufträge mehr senden. Installierte Apps bleiben.",
                "Trennen", isDestructive: true))
        {
            return;
        }

        try
        {
            await AgentClient.DisconnectAsync();
            PageMessage.Show("Die Verbindung zur Cloud ist getrennt.", InfoSeverity.Info);
        }
        catch (BusinessException exception)
        {
            PageMessage.Show(exception.Message, InfoSeverity.Error);
        }

        await AgentMonitor.RefreshNowAsync();
    }

    private void OnOpenCloudClick(object? sender, RoutedEventArgs e)
    {
        if (AgentMonitor.Status?.ServerUrl is { } url)
        {
            Ui.OpenExternal(url);
        }
    }

    private async void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        try
        {
            var result = await AgentClient.CheckForUpdateAsync();
            PageMessage.Show(result.Message, result.IsUpdateAvailable ? InfoSeverity.Success : InfoSeverity.Info);
        }
        catch (BusinessException exception)
        {
            PageMessage.Show(exception.Message, InfoSeverity.Error);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }

        await AgentMonitor.RefreshNowAsync();
    }

    private async void OnInstallUpdateClick(object? sender, RoutedEventArgs e)
    {
        InstallUpdateButton.IsEnabled = false;
        try
        {
            PageMessage.Show("Das Update wird geladen – ThixInstall startet danach neu.", InfoSeverity.Info);
            await AgentClient.InstallUpdateAsync();
            if (!AgentClient.UsesService)
            {
                App.Instance.ExitApplication();
            }
        }
        catch (BusinessException exception)
        {
            PageMessage.Show(exception.Message, InfoSeverity.Error);
            InstallUpdateButton.IsEnabled = true;
        }
    }

    private void OnLogsClick(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        Ui.OpenExternal(AppPaths.LogDirectory);
    }

    private void OnDataClick(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        Ui.OpenExternal(AppPaths.DataDirectory);
    }
}
