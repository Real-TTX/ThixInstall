using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ThixInstall.Controls;
using ThixInstall.Core.Agent;
using ThixInstall.Core.Models;
using ThixInstall.Dialogs;
using ThixInstall.Shared;
using ThixInstall.ViewModels;

namespace ThixInstall.Pages;

/// <summary>A heading of the tiles and the apps under it.</summary>
public sealed record CatalogGroup(string Name, IReadOnlyList<AppTileViewModel> Items)
{
    public bool HasName => Name.Length > 0;
}

/// <summary>
/// The catalog: every app of the cloud for this system with its state here. Choose apps (click, check) and install,
/// update or remove them; each becomes a job of the agent (page "Aufträge").
/// </summary>
public partial class CatalogPage : UserControl, ISearchablePage
{
    private const string AllCategories = "Alle Kategorien";

    private readonly Dictionary<long, AppTileViewModel> _tiles = [];
    private CatalogSnapshot _snapshot = CatalogSnapshot.Empty("");
    private string _search = "";
    private bool _loading;

    public CatalogPage()
    {
        InitializeComponent();
        CategoryChoice.ItemsSource = new[] { AllCategories };
        CategoryChoice.SelectedIndex = 0;
        CategoryChoice.SelectionChanged += (_, _) => Apply();
        FilterChoice.SelectionChanged += (_, _) => Apply();
        SortChoice.SelectionChanged += (_, _) => Apply();
        ApplyView(Core.Business.SettingsService.Get().CatalogAsTiles);
        AgentMonitor.CatalogChanged += (_, _) => _ = LoadAsync();
        AgentMonitor.JobsChanged += (_, _) => _ = LoadAsync();
        AttachedToVisualTree += (_, _) => _ = LoadAsync();
        UpdateActions();
    }

    public string SearchHint => "Apps suchen";

    public void SetSearch(string text)
    {
        _search = text.Trim();
        Apply();
    }

    public void ShowMessage(string text, InfoSeverity severity) => Message.Show(text, severity);

    private async Task LoadAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        try
        {
            _snapshot = await AgentClient.GetCatalogAsync();
            var serverUrl = AgentMonitor.Status?.ServerUrl;
            foreach (var app in _snapshot.Apps)
            {
                if (_tiles.TryGetValue(app.Package.Id, out var tile))
                {
                    tile.Update(app);
                }
                else
                {
                    tile = new AppTileViewModel(app);
                    tile.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(AppTileViewModel.IsSelected))
                        {
                            UpdateActions();
                        }
                    };
                    _tiles[app.Package.Id] = tile;
                    if (app.Package.HasIcon)
                    {
                        _ = LoadIconAsync(tile, serverUrl);
                    }
                }
            }

            foreach (var gone in _tiles.Keys.Except(_snapshot.Apps.Select(app => app.Package.Id)).ToList())
            {
                _tiles.Remove(gone);
            }

            var categories = _snapshot.Apps.Select(app => app.Package.Category).Distinct().OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var current = CategoryChoice.SelectedItem as string ?? AllCategories;
            CategoryChoice.ItemsSource = new[] { AllCategories }.Concat(categories).ToList();
            CategoryChoice.SelectedItem = categories.Contains(current) ? current : AllCategories;
            Apply();
        }
        catch (Core.Models.BusinessException exception)
        {
            _snapshot = CatalogSnapshot.Empty(exception.Message);
            Apply();
        }
        finally
        {
            _loading = false;
        }
    }

    private static async Task LoadIconAsync(AppTileViewModel tile, string? serverUrl)
    {
        var icon = await PackageIcons.LoadAsync(serverUrl, tile.Id);
        if (icon is not null)
        {
            Ui.Post(() => tile.Icon = icon);
        }
    }

    /// <summary>Search, category, filter and sort over the apps in memory.</summary>
    private void Apply()
    {
        var category = CategoryChoice.SelectedItem as string ?? AllCategories;
        IEnumerable<AppTileViewModel> items = _snapshot.Apps.Select(app => _tiles[app.Package.Id]);
        if (category != AllCategories)
        {
            items = items.Where(tile => tile.Category == category);
        }

        items = FilterChoice.SelectedIndex switch
        {
            1 => items.Where(tile => tile.IsInstalled),
            2 => items.Where(tile => !tile.IsInstalled),
            3 => items.Where(tile => tile.IsUpdateAvailable),
            _ => items,
        };

        if (_search.Length > 0)
        {
            items = items.Where(tile => Matches(tile.Package, _search));
        }

        var list = SortChoice.SelectedIndex switch
        {
            2 => items.OrderBy(tile => tile.Subtitle, StringComparer.CurrentCultureIgnoreCase).ThenBy(tile => tile.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            1 => items.OrderBy(tile => tile.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            _ => items.OrderBy(tile => tile.Category, StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(tile => tile.Package.IsFeatured).ThenBy(tile => tile.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
        };

        Groups.ItemsSource = SortChoice.SelectedIndex == 0
            ? list.GroupBy(tile => tile.Category).Select(group => new CatalogGroup(group.Key, group.ToList())).ToList()
            : [new CatalogGroup("", list)];
        Rows.ItemsSource = list;

        var installed = _snapshot.Apps.Count(app => app.IsInstalled);
        var updates = _snapshot.Apps.Count(app => app.IsUpdateAvailable);
        Summary.Text = _snapshot.Apps.Count == 0
            ? "Apps aus dem Katalog deiner ThixInstall.Cloud – still installiert, aktuell gehalten."
            : $"{_snapshot.Apps.Count} Apps · {installed} installiert · {updates} {(updates == 1 ? "Update" : "Updates")}" +
              (_snapshot.LoadedAt is { } loaded ? $" · Stand {Ui.FormatTime(loaded)}" : "");

        var empty = list.Count == 0;
        EmptyState.IsVisible = empty;
        Scroller.IsVisible = !empty;
        ToolbarRow.IsVisible = _snapshot.Apps.Count > 0;
        var notConnected = _snapshot.Apps.Count == 0 && AgentMonitor.Status?.ServerUrl is null;
        ConnectButton.IsVisible = notConnected;
        EmptyText.Text = _snapshot.Apps.Count == 0
            ? _snapshot.Message ?? "Der Katalog ist leer."
            : "Keine App passt zu Suche und Filter.";
        UpdateActions();
    }

    private static bool Matches(PackageDto package, string search) =>
        package.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
        (package.Publisher?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
        package.Category.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
        (package.Description?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false);

    private List<AppTileViewModel> Selected() => _tiles.Values.Where(tile => tile.IsSelected).ToList();

    /// <summary>The buttons name how many apps they act on; a button without one is hidden.</summary>
    private void UpdateActions()
    {
        var selected = Selected();
        var toInstall = selected.Count(tile => !tile.IsInstalled && !tile.IsBusy);
        var toUpdate = selected.Count(tile => tile.IsUpdateAvailable && !tile.IsBusy);
        var toRemove = selected.Count(tile => tile.IsInstalled && !tile.IsBusy);
        InstallLabel.Text = toInstall > 1 ? $"{toInstall} Apps installieren" : "Installieren";
        UpdateLabel.Text = toUpdate > 1 ? $"{toUpdate} Apps aktualisieren" : "Aktualisieren";
        UninstallLabel.Text = toRemove > 1 ? $"{toRemove} Apps deinstallieren" : "Deinstallieren";
        InstallButton.IsEnabled = toInstall > 0;
        UpdateButton.IsVisible = toUpdate > 0;
        UninstallButton.IsVisible = toRemove > 0;
        ClearButton.IsVisible = selected.Count > 0;
    }

    private void OnItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is CheckBox || (sender as Control)?.DataContext is not AppTileViewModel tile)
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            tile.IsSelected = !tile.IsSelected;
            e.Handled = true;
        }
    }

    private async void OnInstallClick(object? sender, RoutedEventArgs e) =>
        await EnqueueAsync(Selected().Where(tile => !tile.IsInstalled && !tile.IsBusy).ToList(), ActionKind.Install);

    private async void OnUpdateClick(object? sender, RoutedEventArgs e) =>
        await EnqueueAsync(Selected().Where(tile => tile.IsUpdateAvailable && !tile.IsBusy).ToList(), ActionKind.Update);

    private async void OnUninstallClick(object? sender, RoutedEventArgs e)
    {
        var apps = Selected().Where(tile => tile.IsInstalled && !tile.IsBusy).ToList();
        if (apps.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", apps.Select(tile => tile.Name));
        if (!await ConfirmDialog.AskAsync(Ui.WindowOf(this), apps.Count == 1 ? $"{apps[0].Name} deinstallieren?" : $"{apps.Count} Apps deinstallieren?",
                $"{names} wird von diesem Gerät entfernt.", "Deinstallieren", isDestructive: true))
        {
            return;
        }

        await EnqueueAsync(apps, ActionKind.Uninstall);
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        foreach (var tile in _tiles.Values)
        {
            tile.IsSelected = false;
        }
    }

    private void OnViewToggleClick(object? sender, RoutedEventArgs e)
    {
        var tiles = !Groups.IsVisible;
        ApplyView(tiles);
        Core.Business.SettingsService.Remember(settings => settings with { CatalogAsTiles = tiles });
    }

    private void ApplyView(bool tiles)
    {
        Groups.IsVisible = tiles;
        Rows.IsVisible = !tiles;
        ViewLabel.Icon = Ui.Icon(tiles ? "Icon.List" : "Icon.Grid");
        ViewLabel.Text = tiles ? "Liste" : "Kacheln";
    }

    private void OnConnectClick(object? sender, RoutedEventArgs e) => App.Instance.MainWindow?.ShowSettings(SettingsPage.CloudTab);

    private async Task EnqueueAsync(IReadOnlyList<AppTileViewModel> apps, ActionKind kind)
    {
        if (apps.Count == 0)
        {
            return;
        }

        var queued = 0;
        try
        {
            foreach (var tile in apps)
            {
                await AgentClient.EnqueueAsync(new JobRequest(kind, PackageId: tile.Id));
                tile.IsSelected = false;
                queued++;
            }

            Message.Show(queued == 1 ? $"„{apps[0].Name}“ ist eingeplant – den Fortschritt zeigt „Aufträge“." : $"{queued} Aufträge sind eingeplant – den Fortschritt zeigt „Aufträge“.", InfoSeverity.Success);
        }
        catch (Core.Models.BusinessException exception)
        {
            Message.Show(exception.Message, InfoSeverity.Error);
        }

        await AgentMonitor.RefreshNowAsync();
        await LoadAsync();
    }
}
