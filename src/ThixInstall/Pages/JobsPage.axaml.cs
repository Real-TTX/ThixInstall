using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ThixInstall.Controls;
using ThixInstall.Core.Agent;
using ThixInstall.Core.Models;
using ThixInstall.Shared;
using ThixInstall.ViewModels;

namespace ThixInstall.Pages;

/// <summary>The jobs of the agent with their output; cancel a waiting or running one, run a failed one again.</summary>
public partial class JobsPage : UserControl, ISearchablePage
{
    private readonly Dictionary<Guid, JobRowViewModel> _rows = [];
    private List<JobRowViewModel> _all = [];
    private Guid? _selected;
    private string _search = "";

    public JobsPage()
    {
        InitializeComponent();
        StateChoice.SelectionChanged += (_, _) => Apply();
        OriginChoice.SelectionChanged += (_, _) => Apply();
        AgentMonitor.JobsChanged += (_, _) => _ = LoadAsync();
        AgentMonitor.StatusChanged += (_, status) =>
        {
            // The running job changes its progress often: refresh its row without reloading the list.
            if (status.CurrentJob is { } current && _rows.TryGetValue(current.Id, out var row))
            {
                row.Update(current);
                if (_selected == current.Id)
                {
                    ShowDetail(row.Job);
                }
            }
        };
        AttachedToVisualTree += (_, _) => _ = LoadAsync();
        SizeChanged += (_, e) =>
        {
            // Narrow: the list alone (the detail needs room).
            var narrow = e.NewSize.Width < 820;
            Split.ColumnDefinitions = narrow ? new ColumnDefinitions("*,0,0") : new ColumnDefinitions("2*,16,3*");
        };
    }

    public string SearchHint => "Aufträge suchen";

    public void SetSearch(string text)
    {
        _search = text.Trim();
        Apply();
    }

    /// <summary>Shows one job (the sidebar's running job, a script that was just started).</summary>
    public void Select(Guid id)
    {
        _selected = id;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var jobs = await AgentClient.GetJobsAsync();
            _all = jobs.Select(job =>
            {
                if (_rows.TryGetValue(job.Id, out var row))
                {
                    row.Update(job);
                    return row;
                }

                row = new JobRowViewModel(job);
                _rows[job.Id] = row;
                return row;
            }).ToList();
            Apply();
        }
        catch (BusinessException exception)
        {
            Message.Show(exception.Message, InfoSeverity.Error);
        }
    }

    private void Apply()
    {
        IEnumerable<JobRowViewModel> rows = _all;
        rows = StateChoice.SelectedIndex switch
        {
            1 => rows.Where(row => !row.Job.IsFinished),
            2 => rows.Where(row => row.Job.State == ActionState.Failed),
            3 => rows.Where(row => row.Job.State == ActionState.Succeeded),
            _ => rows,
        };
        rows = OriginChoice.SelectedIndex switch
        {
            1 => rows.Where(row => row.Job.Origin is ActionOrigin.Device or ActionOrigin.Bundle),
            2 => rows.Where(row => row.Job.Origin == ActionOrigin.Push),
            3 => rows.Where(row => row.Job.Origin == ActionOrigin.Profile),
            _ => rows,
        };
        if (_search.Length > 0)
        {
            rows = rows.Where(row => row.Title.Contains(_search, StringComparison.CurrentCultureIgnoreCase));
        }

        var list = rows.ToList();
        Rows.ItemsSource = list;
        var open = _all.Count(row => !row.Job.IsFinished);
        var failed = _all.Count(row => row.Job.State == ActionState.Failed);
        Summary.Text = _all.Count == 0 ? "Was ThixInstall auf diesem Gerät installiert, aktualisiert und ausführt."
            : $"{open} offen · {failed} fehlgeschlagen · {_all.Count} insgesamt";
        EmptyState.IsVisible = list.Count == 0;
        ListScroller.IsVisible = list.Count > 0;

        var selected = list.FirstOrDefault(row => row.Id == _selected) ?? list.FirstOrDefault();
        _selected = selected?.Id;
        foreach (var row in _all)
        {
            row.IsSelected = row.Id == _selected;
        }

        DetailCard.IsVisible = selected is not null;
        if (selected is not null)
        {
            ShowDetail(selected.Job);
        }
    }

    private void ShowDetail(JobRecord job)
    {
        var row = _rows[job.Id];
        DetailTitle.Text = job.Title;
        DetailState.Text = row.ShortState;
        DetailState.Tone = row.StateTone;
        DetailStatus.Text = AgentMonitor.StateText(job);
        DetailProgress.IsVisible = job.State == ActionState.Running;
        DetailProgress.IsIndeterminate = job.Progress is null;
        DetailProgress.Value = (job.Progress ?? 0) * 100;
        DetailOrigin.Text = row.OriginText;
        DetailCreated.Text = Ui.FormatDateTime(job.CreatedAt);
        DetailDuration.Text = job.StartedAt is { } started ? Duration((job.FinishedAt ?? DateTimeOffset.Now) - started) : "–";
        DetailExit.Text = job.ExitCode?.ToString() ?? "–";
        DetailVersion.Text = job.InstalledVersion ?? job.Package?.LatestVersion ?? "–";
        DetailOutput.Text = string.IsNullOrWhiteSpace(job.Output) ? (job.IsFinished ? "(keine Ausgabe)" : "…") : job.Output;
        CancelButton.IsVisible = !job.IsFinished;
        RetryButton.IsVisible = job.IsFinished && job.State != ActionState.Succeeded && (job.Package is not null || job.Script is not null);
        CopyButton.IsEnabled = !string.IsNullOrWhiteSpace(job.Output);
    }

    private static string Duration(TimeSpan duration) => duration.TotalSeconds < 60
        ? $"{Math.Max(0, duration.TotalSeconds):0} s"
        : duration.TotalMinutes < 60 ? $"{(int)duration.TotalMinutes} min {duration.Seconds} s" : $"{(int)duration.TotalHours} h {duration.Minutes} min";

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is JobRowViewModel row)
        {
            _selected = row.Id;
            Apply();
        }
    }

    private async void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } id)
        {
            return;
        }

        try
        {
            await AgentClient.CancelAsync(id);
            await AgentMonitor.RefreshNowAsync();
        }
        catch (BusinessException exception)
        {
            Message.Show(exception.Message, InfoSeverity.Error);
        }
    }

    private async void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } id || !_rows.TryGetValue(id, out var row))
        {
            return;
        }

        var job = row.Job;
        try
        {
            var again = await AgentClient.EnqueueAsync(new JobRequest(job.Kind, job.Package?.Id, job.Script?.Id));
            _selected = again.Id;
            await AgentMonitor.RefreshNowAsync();
        }
        catch (BusinessException exception)
        {
            Message.Show(exception.Message, InfoSeverity.Error);
        }
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && DetailOutput.Text is { } text)
        {
            await clipboard.SetTextAsync(text);
            Message.Show("Die Ausgabe ist in der Zwischenablage.", InfoSeverity.Success);
        }
    }
}
