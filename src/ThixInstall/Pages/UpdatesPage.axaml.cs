using Avalonia.Controls;
using Avalonia.Interactivity;
using ThixInstall.Controls;
using ThixInstall.Core.Agent;
using ThixInstall.Core.Models;
using ThixInstall.Shared;
using ThixInstall.ViewModels;

namespace ThixInstall.Pages;

/// <summary>Updates of the catalog apps on this device.</summary>
public partial class UpdatesPage : UserControl, ISearchablePage
{
    private readonly Dictionary<long, AppTileViewModel> _rows = [];
    private List<AppTileViewModel> _all = [];
    private string _search = "";
    private bool _applyingStatus;

    public UpdatesPage()
    {
        InitializeComponent();
        AgentMonitor.CatalogChanged += (_, _) => _ = LoadAsync();
        AgentMonitor.JobsChanged += (_, _) => _ = LoadAsync();
        AgentMonitor.StatusChanged += (_, status) => ApplyStatus(status);
        AttachedToVisualTree += (_, _) => _ = LoadAsync();
        AutoUpdateSwitch.IsCheckedChanged += async (_, _) =>
        {
            if (_applyingStatus)
            {
                return;
            }

            try
            {
                await AgentClient.ChangeSettingsAsync(new AgentSettingsChange(AutoUpdateApps: AutoUpdateSwitch.IsChecked == true));
            }
            catch (BusinessException exception)
            {
                Message.Show(exception.Message, InfoSeverity.Error);
            }
        };
        if (AgentMonitor.Status is { } status)
        {
            ApplyStatus(status);
        }
    }

    public string SearchHint => "Updates suchen";

    public void SetSearch(string text)
    {
        _search = text.Trim();
        Apply();
    }

    private void ApplyStatus(AgentStatus status)
    {
        _applyingStatus = true;
        AutoUpdateSwitch.IsChecked = status.AutoUpdateApps;
        AutoUpdateSwitch.IsEnabled = !status.IsConfigurationLocked;
        _applyingStatus = false;
    }

    private async Task LoadAsync()
    {
        try
        {
            var snapshot = await AgentClient.GetCatalogAsync();
            var serverUrl = AgentMonitor.Status?.ServerUrl;
            _all = snapshot.Apps.Where(app => app.IsUpdateAvailable).Select(app =>
            {
                if (_rows.TryGetValue(app.Package.Id, out var row))
                {
                    row.Update(app);
                    return row;
                }

                row = new AppTileViewModel(app);
                _rows[app.Package.Id] = row;
                if (app.Package.HasIcon)
                {
                    _ = Task.Run(async () =>
                    {
                        var icon = await PackageIcons.LoadAsync(serverUrl, row.Id);
                        Ui.Post(() => row.Icon = icon);
                    });
                }

                return row;
            }).OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            Apply();
        }
        catch (BusinessException exception)
        {
            Message.Show(exception.Message, InfoSeverity.Error);
        }
    }

    private void Apply()
    {
        var list = _search.Length == 0 ? _all : _all.Where(row => row.Name.Contains(_search, StringComparison.CurrentCultureIgnoreCase)).ToList();
        Rows.ItemsSource = list;
        Summary.Text = _all.Count switch
        {
            0 => "Neue Versionen der installierten Katalog-Apps erscheinen hier.",
            1 => "1 App hat eine neuere Version.",
            _ => $"{_all.Count} Apps haben eine neuere Version.",
        };
        EmptyState.IsVisible = list.Count == 0;
        EmptyText.Text = _all.Count == 0 ? "Alle Apps aus dem Katalog sind aktuell." : "Keine Update passt zur Suche.";
        Scroller.IsVisible = list.Count > 0;
        UpdateAllButton.IsEnabled = _all.Any(row => !row.IsBusy);
        UpdateAllLabel.Text = _all.Count > 1 ? $"Alle {_all.Count} aktualisieren" : "Alle aktualisieren";
    }

    private async void OnUpdateOneClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AppTileViewModel row)
        {
            await EnqueueAsync(new JobRequest(ActionKind.Update, PackageId: row.Id), $"„{row.Name}“ wird aktualisiert.");
        }
    }

    private async void OnUpdateAllClick(object? sender, RoutedEventArgs e) =>
        await EnqueueAsync(new JobRequest(ActionKind.UpdateAll), "Alle Updates sind eingeplant – den Fortschritt zeigt „Aufträge“.");

    private void OnCatalogClick(object? sender, RoutedEventArgs e) => App.Instance.MainWindow?.ShowCatalog();

    private async Task EnqueueAsync(JobRequest request, string done)
    {
        try
        {
            await AgentClient.EnqueueAsync(request);
            Message.Show(done, InfoSeverity.Success);
            await AgentMonitor.RefreshNowAsync();
        }
        catch (BusinessException exception)
        {
            Message.Show(exception.Message, InfoSeverity.Error);
        }
    }
}
