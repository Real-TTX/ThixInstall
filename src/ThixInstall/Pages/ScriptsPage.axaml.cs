using Avalonia.Controls;
using Avalonia.Interactivity;
using ThixInstall.Controls;
using ThixInstall.Core.Agent;
using ThixInstall.Core.Models;
using ThixInstall.Dialogs;
using ThixInstall.Shared;
using ThixInstall.ViewModels;

namespace ThixInstall.Pages;

/// <summary>Scripts of the catalog: run one on this device (it becomes a job).</summary>
public partial class ScriptsPage : UserControl, ISearchablePage
{
    private const string AllCategories = "Alle Kategorien";

    private List<ScriptRowViewModel> _all = [];
    private string _search = "";
    private string? _message;

    public ScriptsPage()
    {
        InitializeComponent();
        CategoryChoice.ItemsSource = new[] { AllCategories };
        CategoryChoice.SelectedIndex = 0;
        CategoryChoice.SelectionChanged += (_, _) => Apply();
        ContextChoice.SelectionChanged += (_, _) => Apply();
        AgentMonitor.CatalogChanged += (_, _) => _ = LoadAsync();
        AttachedToVisualTree += (_, _) => _ = LoadAsync();
    }

    public string SearchHint => "Skripte suchen";

    public void SetSearch(string text)
    {
        _search = text.Trim();
        Apply();
    }

    private async Task LoadAsync()
    {
        try
        {
            var snapshot = await AgentClient.GetCatalogAsync();
            _message = snapshot.Message;
            _all = snapshot.Scripts.Select(script => new ScriptRowViewModel(script)).OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var categories = _all.Select(row => row.Category).Distinct().OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var current = CategoryChoice.SelectedItem as string ?? AllCategories;
            CategoryChoice.ItemsSource = new[] { AllCategories }.Concat(categories).ToList();
            CategoryChoice.SelectedItem = categories.Contains(current) ? current : AllCategories;
            Apply();
        }
        catch (BusinessException exception)
        {
            Message.Show(exception.Message, InfoSeverity.Error);
        }
    }

    private void Apply()
    {
        IEnumerable<ScriptRowViewModel> rows = _all;
        if (CategoryChoice.SelectedItem is string category && category != AllCategories)
        {
            rows = rows.Where(row => row.Category == category);
        }

        rows = ContextChoice.SelectedIndex switch
        {
            1 => rows.Where(row => row.Script.Context == ScriptContext.User),
            2 => rows.Where(row => row.Script.Context == ScriptContext.System),
            _ => rows,
        };
        if (_search.Length > 0)
        {
            rows = rows.Where(row => row.Name.Contains(_search, StringComparison.CurrentCultureIgnoreCase) ||
                                     (row.Description?.Contains(_search, StringComparison.CurrentCultureIgnoreCase) ?? false));
        }

        var list = rows.ToList();
        Rows.ItemsSource = list;
        Summary.Text = _all.Count == 0 ? "Skripte und Makros aus dem Katalog deiner Cloud." : $"{_all.Count} Skripte für {Core.Infrastructure.AppInfo.PlatformName}";
        EmptyState.IsVisible = list.Count == 0;
        Scroller.IsVisible = list.Count > 0;
        ToolbarRow.IsVisible = _all.Count > 0;
        EmptyText.Text = _all.Count == 0 ? _message ?? "Der Katalog hat keine Skripte für dieses System." : "Kein Skript passt zu Suche und Filter.";
    }

    private async void OnRunClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ScriptRowViewModel row)
        {
            return;
        }

        var rights = row.Script.Context == ScriptContext.System ? "mit Administratorrechten" : "als angemeldeter Benutzer";
        if (!await ConfirmDialog.AskAsync(Ui.WindowOf(this), $"„{row.Name}“ ausführen?",
                $"Das Skript läuft auf diesem Gerät {rights} ({row.ShellText}).{(row.HasDescription ? "\n\n" + row.Description : "")}", "Ausführen"))
        {
            return;
        }

        try
        {
            var job = await AgentClient.EnqueueAsync(new JobRequest(ActionKind.RunScript, ScriptId: row.Id));
            Message.Show($"„{row.Name}“ ist eingeplant.", InfoSeverity.Success);
            await AgentMonitor.RefreshNowAsync();
            App.Instance.MainWindow?.ShowJobs(job.Id);
        }
        catch (BusinessException exception)
        {
            Message.Show(exception.Message, InfoSeverity.Error);
        }
    }

    private void OnJobsClick(object? sender, RoutedEventArgs e) => App.Instance.MainWindow?.ShowJobs();
}
