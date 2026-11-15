using Avalonia.Controls;
using Avalonia.Platform;
using ThixInstall.Core.Agent;
using ThixInstall.Core.Models;
using ThixInstall.Shared;
using AvaloniaTrayIcon = Avalonia.Controls.TrayIcon;

namespace ThixInstall.Tray;

/// <summary>
/// The icon in the notification area (Windows) or the menu bar (macOS) and its menu: open ThixInstall, the running job,
/// the updates (all at once), refresh, settings, the update of ThixInstall, quit. Windows: a click opens the window, the
/// right button the menu. macOS: the glyph is a template image (the menu bar colours it), every click opens the menu.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly AvaloniaTrayIcon _trayIcon;
    private readonly NativeMenu _menu = new();
    private readonly App _app;

    public TrayIcon(App app)
    {
        _app = app;
        var asset = new Uri(OperatingSystem.IsMacOS() ? "avares://ThixInstall/Assets/tray-macos.png" : "avares://ThixInstall/Assets/tray-windows.ico");
        _trayIcon = new AvaloniaTrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(asset)),
            ToolTipText = "ThixInstall",
            Menu = _menu,
            IsVisible = true,
        };
        if (OperatingSystem.IsMacOS())
        {
            MacOSProperties.SetIsTemplateIcon(_trayIcon, true);
        }

        _trayIcon.Clicked += (_, _) => _app.ShowMainWindow();
        _menu.Opening += (_, _) => BuildMenu();
        AvaloniaTrayIcon.SetIcons(app, [_trayIcon]);
        AgentMonitor.StatusChanged += OnStatusChanged;
        BuildMenu();
    }

    public void ShowNotification(string title, string text, bool isWarning = false) =>
        NotificationToast.Show(title, text, isWarning, () => _app.ShowMainWindow(window => window.ShowJobs()));

    public void Dispose()
    {
        AgentMonitor.StatusChanged -= OnStatusChanged;
        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
    }

    private void OnStatusChanged(object? sender, AgentStatus status)
    {
        _trayIcon.ToolTipText = status.CurrentJob is { } job
            ? $"ThixInstall – {job.Title}{(job.Progress is { } progress ? $" ({progress:P0})" : "")}"
            : status.UpdatesAvailable > 0 ? $"ThixInstall – {status.UpdatesAvailable} Updates verfügbar" : "ThixInstall";
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var status = AgentMonitor.Status;
        _menu.Items.Add(Item("ThixInstall öffnen", () => _app.ShowMainWindow()));
        _menu.Items.Add(new NativeMenuItemSeparator());

        if (status?.CurrentJob is { } job)
        {
            _menu.Items.Add(new NativeMenuItem($"Läuft: {Shorten(job.Title)}") { IsEnabled = false });
            _menu.Items.Add(Item("Aufträge ansehen …", () => _app.ShowMainWindow(window => window.ShowJobs(job.Id))));
            _menu.Items.Add(new NativeMenuItemSeparator());
        }

        var updates = status?.UpdatesAvailable ?? 0;
        _menu.Items.Add(Item(updates > 0 ? $"Updates ({updates}) …" : "Updates …", () => _app.ShowMainWindow(window => window.ShowUpdates())));
        if (updates > 0)
        {
            _menu.Items.Add(Item("Alle Apps aktualisieren", UpdateAll));
        }

        _menu.Items.Add(Item("Katalog …", () => _app.ShowMainWindow(window => window.ShowCatalog())));
        if (status?.ServerUrl is not null)
        {
            _menu.Items.Add(Item("Jetzt aktualisieren", () => _ = AgentClient.RefreshAsync().ContinueWith(task => _ = task.Exception, TaskScheduler.Default)));
        }

        _menu.Items.Add(Item("Einstellungen …", () => _app.ShowMainWindow(window => window.ShowSettings())));
        if (status?.AvailableUpdate is { } version)
        {
            _menu.Items.Add(Item($"ThixInstall {version} installieren …", () => _app.ShowMainWindow(window => window.ShowSettings(Pages.SettingsPage.UpdatesTab))));
        }

        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(Item("Beenden", () => _app.ExitApplication()));
    }

    private async void UpdateAll()
    {
        try
        {
            await AgentClient.EnqueueAsync(new JobRequest(ActionKind.UpdateAll));
            ShowNotification("Updates eingeplant", "ThixInstall aktualisiert alle Apps mit einer neueren Version.");
        }
        catch (BusinessException exception)
        {
            ShowNotification("Updates nicht möglich", exception.Message, isWarning: true);
        }
    }

    private static string Shorten(string text) => text.Length <= 48 ? text : text[..47] + "…";

    private static NativeMenuItem Item(string header, Action action) => new(header) { Command = new RelayCommand(action) };

    /// <summary>Menu items run their action through a command (works on Windows and in the macOS menu bar).</summary>
    private sealed class RelayCommand(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => Ui.Post(action);
    }
}
