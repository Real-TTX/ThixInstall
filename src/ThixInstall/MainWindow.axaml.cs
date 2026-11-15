using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using ThixInstall.Controls;
using ThixInstall.Core.Agent;
using ThixInstall.Core.Business;
using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Models;
using ThixInstall.Core.Platform;
using ThixInstall.Pages;

namespace ThixInstall;

/// <summary>A page of the main window; the header's search filters the page that is shown.</summary>
public interface ISearchablePage
{
    string SearchHint { get; }

    void SetSearch(string text);
}

/// <summary>
/// The main window. Built once, shown from the tray / menu bar and hidden by its X (ThixInstall keeps running).
/// Keys: Ctrl/Cmd+1..4 sections, +, settings, +F search, +B fold the menu, F5 / Ctrl/Cmd+R refresh, Esc clears the search.
/// </summary>
public partial class MainWindow : ThemedWindow
{
    /// <summary>The folded menu: the icons with their margins.</summary>
    private const double FoldedSidebarWidth = 68;

    private readonly CatalogPage _catalog = new();
    private readonly Lazy<UpdatesPage> _updates = new(() => new UpdatesPage());
    private readonly Lazy<ScriptsPage> _scripts = new(() => new ScriptsPage());
    private readonly Lazy<JobsPage> _jobs = new(() => new JobsPage());
    private readonly Lazy<SettingsPage> _settings = new(() => new SettingsPage());
    private readonly Lazy<AboutPage> _about = new(() => new AboutPage());
    private readonly DispatcherTimer _sizeTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    public MainWindow()
    {
        InitializeComponent();
        var settings = SettingsService.Get();
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        IsSidebarCollapsed = settings.IsSidebarCollapsed;

        if (OperatingSystem.IsMacOS())
        {
            // The traffic lights stay where macOS draws them; the logo moves right of them.
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
            ExtendClientAreaTitleBarHeightHint = 64;
            CaptionButtons.IsVisible = false;
            HeaderGrid.Margin = new Thickness(84, 0, 18, 0);
            HeaderGrid.ColumnDefinitions[0].Width = new GridLength(186);
        }
        else
        {
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
            ExtendClientAreaTitleBarHeightHint = 0;
        }

        ShowPage(_catalog, NavCatalog);
        SearchBox.TextChanged += (_, _) => (PageHost.Content as ISearchablePage)?.SetSearch(SearchBox.Text ?? "");

        _sizeTimer.Tick += (_, _) =>
        {
            _sizeTimer.Stop();
            if (WindowState == WindowState.Normal)
            {
                SettingsService.Remember(current => current with { WindowWidth = Width, WindowHeight = Height });
            }
        };
        Resized += (_, _) =>
        {
            _sizeTimer.Stop();
            _sizeTimer.Start();
            MaximizeIcon.Data = Ui.Icon(WindowState == WindowState.Maximized ? "Icon.Restore" : "Icon.Maximize");
        };
        SizeChanged += (_, e) => ApplySidebarWidth(e.NewSize.Width);

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AgentMonitor.StatusChanged += (_, status) => RefreshStatus(status);
        ApplySidebar();
        var command = Ui.CommandKey.Name;
        ToolTip.SetTip(NavCatalog, $"Katalog ({command}+1)");
        ToolTip.SetTip(NavUpdates, $"Updates ({command}+2)");
        ToolTip.SetTip(NavScripts, $"Skripte ({command}+3)");
        ToolTip.SetTip(NavJobs, $"Aufträge ({command}+4)");
        ToolTip.SetTip(NavSettings, $"Einstellungen ({command}+,)");
    }

    /// <summary>Set by App when the application ends: then the window really closes.</summary>
    public bool IsExiting { get; set; }

    /// <summary>The menu at the left is folded to its icons; remembered across starts.</summary>
    public bool IsSidebarCollapsed { get; private set; }

    public void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
        ApplySidebar();
        SettingsService.Remember(settings => settings with { IsSidebarCollapsed = IsSidebarCollapsed });
    }

    /// <summary>Tray, second start, Dock: show the window in front.</summary>
    public void ShowFromTray()
    {
        BringToFront();
        WindowActivation.BringToFront(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
        _ = AgentMonitor.RefreshNowAsync();
    }

    public void ShowCatalog() => ShowPage(_catalog, NavCatalog);

    public void ShowUpdates() => ShowPage(_updates.Value, NavUpdates);

    public void ShowScripts() => ShowPage(_scripts.Value, NavScripts);

    public void ShowJobs(Guid? select = null)
    {
        ShowPage(_jobs.Value, NavJobs);
        if (select is { } id)
        {
            _jobs.Value.Select(id);
        }
    }

    public void ShowSettings(string? tab = null)
    {
        ShowPage(_settings.Value, NavSettings);
        if (tab is not null)
        {
            _settings.Value.SelectTab(tab);
        }
    }

    public void ShowAbout() => ShowPage(_about.Value, null);

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // The X (and Alt+F4) only hides: ThixInstall keeps running in the tray / menu bar.
        if (!IsExiting)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void ShowPage(Control page, ToggleButton? nav)
    {
        foreach (var item in new[] { NavCatalog, NavUpdates, NavScripts, NavJobs, NavSettings })
        {
            item.IsChecked = item == nav;
        }

        PageHost.Content = page;
        var searchable = page as ISearchablePage;
        SearchBox.IsEnabled = searchable is not null;
        SearchBox.Watermark = searchable?.SearchHint ?? "Suchen";
        if (searchable is not null)
        {
            searchable.SetSearch(SearchBox.Text ?? "");
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var withCommand = e.KeyModifiers.HasFlag(Ui.CommandKey.Modifier);
        switch (e.Key)
        {
            case Key.Escape when OwnedWindows.Count == 0 && !string.IsNullOrEmpty(SearchBox.Text):
                e.Handled = true;
                SearchBox.Text = "";
                break;
            case Key.F when withCommand:
                e.Handled = true;
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case Key.D1 when withCommand:
                e.Handled = true;
                ShowCatalog();
                break;
            case Key.D2 when withCommand:
                e.Handled = true;
                ShowUpdates();
                break;
            case Key.D3 when withCommand:
                e.Handled = true;
                ShowScripts();
                break;
            case Key.D4 when withCommand:
                e.Handled = true;
                ShowJobs();
                break;
            case Key.OemComma when withCommand:
                e.Handled = true;
                ShowSettings();
                break;
            case Key.B when withCommand:
                e.Handled = true;
                ToggleSidebar();
                break;
            case Key.F5:
            case Key.R when withCommand:
                e.Handled = true;
                _ = RefreshAsync();
                break;
        }
    }

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        switch ((sender as Control)?.Tag as string)
        {
            case "Updates":
                ShowUpdates();
                break;
            case "Scripts":
                ShowScripts();
                break;
            case "Jobs":
                ShowJobs();
                break;
            case "Settings":
                ShowSettings();
                break;
            default:
                ShowCatalog();
                break;
        }
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e) => await RefreshAsync();

    /// <summary>Catalog from the cloud and the installed apps, read again.</summary>
    public async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;
        RefreshLabel.Text = "Aktualisiere …";
        try
        {
            await AgentClient.RefreshAsync();
            await AgentMonitor.RefreshNowAsync();
        }
        catch (BusinessException exception)
        {
            _catalog.ShowMessage(exception.Message, InfoSeverity.Warning);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            RefreshLabel.Text = "Aktualisieren";
        }
    }

    /// <summary>Counts, the running job, the cloud and the update in the sidebar.</summary>
    private void RefreshStatus(AgentStatus status)
    {
        UpdatesBadge.IsVisible = status.UpdatesAvailable > 0 && !IsSidebarCollapsed;
        UpdatesCount.Text = status.UpdatesAvailable.ToString();
        var open = status.QueuedJobs + (status.CurrentJob is null ? 0 : 1);
        JobsBadge.IsVisible = open > 0 && !IsSidebarCollapsed;
        JobsCount.Text = open.ToString();

        var job = status.CurrentJob;
        JobCard.IsVisible = job is not null && !IsSidebarCollapsed;
        if (job is not null)
        {
            JobTitle.Text = job.Title;
            JobStatus.Text = status.QueuedJobs > 0 ? $"{job.Status ?? "Läuft …"} · noch {status.QueuedJobs} weitere" : job.Status ?? "Läuft …";
            JobProgress.IsIndeterminate = job.Progress is null;
            JobProgress.Value = (job.Progress ?? 0) * 100;
        }

        RefreshCloudStatus(status);
        ApplyVersion(status);
    }

    /// <summary>
    /// Full menu: the sections with their names and counts, the running job, the cloud and the version. Folded: the icons
    /// alone (their tool tips name them), the cloud dot, an info icon for the about page.
    /// </summary>
    private void ApplySidebar()
    {
        var folded = IsSidebarCollapsed;
        foreach (var item in new[] { NavCatalog, NavUpdates, NavScripts, NavJobs, NavSettings })
        {
            ((IconLabel)((Grid)item.Content!).Children[0]).IsTextVisible = !folded;
        }

        CloudText.IsVisible = !folded;
        CloudStatus.HorizontalContentAlignment = folded ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        BrandName.IsVisible = !folded || OperatingSystem.IsMacOS();
        Grid.SetRow(SidebarToggle, folded ? 1 : 0);
        Grid.SetColumn(SidebarToggle, folded ? 0 : 1);
        Grid.SetColumnSpan(SidebarToggle, folded ? 2 : 1);
        SidebarToggle.HorizontalAlignment = folded ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        SidebarToggle.Margin = folded ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        var action = folded ? "Menü ausklappen" : "Menü einklappen";
        ToolTip.SetTip(SidebarToggle, $"{action} ({Ui.CommandKey.Name}+B)");
        AutomationProperties.SetName(SidebarToggle, action);
        ApplySidebarWidth(Bounds.Width > 0 ? Bounds.Width : Width);
        if (AgentMonitor.Status is { } status)
        {
            RefreshStatus(status);
        }
        else
        {
            ApplyVersion(null);
            RefreshCloudStatus(null);
        }
    }

    /// <summary>The version opens "Über ThixInstall"; an available update is named at it (a dot when folded).</summary>
    private void ApplyVersion(AgentStatus? status)
    {
        var folded = IsSidebarCollapsed;
        var update = status?.AvailableUpdate;
        VersionButton.Content = folded
            ? new Panel
            {
                Children =
                {
                    new IconView { Data = Ui.Icon(update is null ? "Icon.Info" : "Icon.Download"), Width = 18, Height = 18 },
                    new Avalonia.Controls.Shapes.Ellipse
                    {
                        Width = 7, Height = 7, Fill = Ui.Brush("AccentBrush"), IsVisible = update is not null,
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -3, 0),
                    },
                },
            }
            : update is null ? $"Version {AppInfo.Version}" : $"Version {AppInfo.Version}  ·  Update";
        ToolTip.SetTip(VersionButton, update is not null ? $"Update auf {update} verfügbar – Über ThixInstall" : $"Über ThixInstall – Version {AppInfo.Version}");
        VersionButton.Margin = folded ? new Thickness(0) : new Thickness(8, 0, 0, 0);
        VersionButton.HorizontalAlignment = folded ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        Grid.SetColumnSpan(VersionButton, folded ? 2 : 1);
    }

    private void ApplySidebarWidth(double windowWidth)
    {
        var width = IsSidebarCollapsed ? FoldedSidebarWidth : windowWidth < 900 ? 210 : 248;
        BodyGrid.ColumnDefinitions[0].Width = new GridLength(width);
        if (OperatingSystem.IsMacOS())
        {
            return; // the traffic lights keep the header as it is
        }

        HeaderGrid.ColumnDefinitions[0].Width = new GridLength(width);
        HeaderGrid.Margin = new Thickness(IsSidebarCollapsed ? 18 : 22, 0, 12, 0);
    }

    private void RefreshCloudStatus(AgentStatus? status)
    {
        string? dot;
        if (AgentMonitor.Problem is { } problem)
        {
            (CloudText.Text, dot) = ("Dienst nicht erreichbar", "DangerBrush");
            ToolTip.SetTip(CloudStatus, problem);
            CloudDot.IsVisible = true;
            CloudDot.Fill = Ui.Brush(dot);
            return;
        }

        (CloudText.Text, dot) = status?.Cloud switch
        {
            CloudState.Online => (status.SyncEnabled ? $"Verbunden · {status.UserName}" : $"Verbunden · Sync aus", "SuccessBrush"),
            CloudState.Connecting => ("Verbinde …", "AccentBrush"),
            CloudState.Offline => ("Cloud nicht erreichbar", "WarningBrush"),
            CloudState.SignedOut => ("Abgemeldet – neu verbinden", "DangerBrush"),
            _ => ("Mit Cloud verbinden", (string?)null),
        };
        CloudDot.IsVisible = dot is not null;
        if (dot is not null)
        {
            CloudDot.Fill = Ui.Brush(dot);
        }

        var tip = status is null ? "ThixInstall.Cloud" : status.LastSync is { } last ? $"{status.CloudMessage} (zuletzt {last.ToLocalTime():HH:mm:ss})" : status.CloudMessage;
        ToolTip.SetTip(CloudStatus, IsSidebarCollapsed ? $"{CloudText.Text}\n{tip}" : tip);
        AutomationProperties.SetName(CloudStatus, CloudText.Text);
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not TextBox && e.ClickCount == 1)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnHeaderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Border or Grid or StackPanel)
        {
            OnMaximizeClick(sender, e);
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Hide();

    private void OnJobCardPressed(object? sender, PointerPressedEventArgs e) => ShowJobs(AgentMonitor.Status?.CurrentJob?.Id);

    private void OnCloudStatusClick(object? sender, RoutedEventArgs e) => ShowSettings(SettingsPage.CloudTab);

    private void OnVersionClick(object? sender, RoutedEventArgs e) => ShowAbout();

    private void OnSidebarToggleClick(object? sender, RoutedEventArgs e) => ToggleSidebar();
}
