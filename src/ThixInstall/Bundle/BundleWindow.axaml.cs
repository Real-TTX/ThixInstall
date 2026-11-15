using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using ThixInstall.Controls;
using ThixInstall.Core.Business;
using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Models;
using ThixInstall.Shared;
using ThixInstall.ViewModels;

namespace ThixInstall.Bundle;

/// <summary>One step of a bundle in the window.</summary>
public sealed class BundleStepViewModel(JobRecord step) : ObservableObject
{
    private JobRecord _step = step;
    private Bitmap? _icon;

    public JobRecord Step => _step;

    public bool IsApp => _step.Package is not null;

    public string Name => _step.Package?.Name ?? _step.Script?.Name ?? "ThixInstall";

    public string Key => _step.Package?.Key ?? "";

    public string Title => _step.Title;

    public Avalonia.Media.Geometry GlyphData => Ui.Icon(_step.Script is not null ? "Icon.Terminal" : "Icon.Box");

    public string StatusText => _step.State switch
    {
        ActionState.Pending => _step.Package?.LatestVersion is { } version ? $"Version {version}" : "Wartet",
        _ => AgentMonitor.StateText(_step),
    };

    public string ShortState => _step.State switch
    {
        ActionState.Pending => "Wartet",
        ActionState.Running => "Läuft",
        ActionState.Succeeded => "Fertig",
        ActionState.Failed => "Fehler",
        _ => "Abgebrochen",
    };

    public ChipTone StateTone => _step.State switch
    {
        ActionState.Running => ChipTone.Accent,
        ActionState.Succeeded => ChipTone.Success,
        ActionState.Failed => ChipTone.Danger,
        _ => ChipTone.Neutral,
    };

    public bool IsRunning => _step.State == ActionState.Running;

    public bool HasProgress => _step.Progress is not null;

    public double ProgressValue => (_step.Progress ?? 0) * 100;

    public Bitmap? Icon
    {
        get => _icon;
        set => SetField(ref _icon, value);
    }

    public void Update(JobRecord step)
    {
        _step = step;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// The bundle runner's window: loads the bundle of the ticket, shows its steps and starts at once (like Ninite). The
/// callback gets true when ThixInstall itself was installed (the starter then opens it).
/// </summary>
public partial class BundleWindow : ThemedWindow
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly List<BundleStepViewModel> _steps = [];
    private BundleTicket? _ticket;
    private Action<bool>? _done;
    private bool _running;
    private bool _agentInstalled;

    public BundleWindow()
    {
        InitializeComponent();
        Closing += (_, e) =>
        {
            if (_running)
            {
                e.Cancel = true;
                OnCancelClick(this, new RoutedEventArgs());
            }
        };
        Closed += (_, _) => _done?.Invoke(_agentInstalled);
    }

    public static void Run(BundleTicket ticket, Action<bool> done)
    {
        var window = new BundleWindow { _ticket = ticket, _done = done };
        window.Show();
        _ = window.StartAsync();
    }

    /// <summary>Windows: the user said no to the administrator prompt.</summary>
    public static void ShowElevationDeclined(Action done)
    {
        var window = new BundleWindow { _done = _ => done() };
        window.BundleName.Text = "Keine Administratorrechte";
        window.Message.Show("Ohne Administratorrechte kann ThixInstall die Apps nicht für diesen Computer installieren. Starte das Paket erneut und bestätige die Abfrage.", InfoSeverity.Warning);
        window.Finish("Nichts installiert.");
        window.Show();
    }

    private async Task StartAsync()
    {
        if (_ticket is null)
        {
            return;
        }

        BundleDto bundle;
        try
        {
            bundle = await BundleService.LoadAsync(_ticket, _cancel.Token);
        }
        catch (BusinessException exception)
        {
            BundleName.Text = "Paket nicht verfügbar";
            Message.Show(exception.Message, InfoSeverity.Error);
            Finish("Nichts installiert.");
            return;
        }

        BundleName.Text = $"{bundle.Name} · {bundle.Packages.Count} Apps{(bundle.Scripts.Count > 0 ? $", {bundle.Scripts.Count} Skripte" : "")}";
        foreach (var step in BundleService.PlanSteps(_ticket, bundle))
        {
            var row = new BundleStepViewModel(step);
            _steps.Add(row);
            if (step.Package is { HasIcon: true } package)
            {
                _ = Task.Run(async () =>
                {
                    var icon = await PackageIcons.LoadAsync(_ticket.CloudUrl, package.Id);
                    Ui.Post(() => row.Icon = icon);
                });
            }
        }

        Steps.ItemsSource = _steps;
        if (_steps.Count == 0)
        {
            Message.Show("Dieses Paket ist leer.", InfoSeverity.Info);
            Finish("Nichts zu tun.");
            return;
        }

        _running = true;
        SummaryText.Text = "Installiere … du kannst ThixInstall einfach arbeiten lassen.";
        var results = await BundleService.RunAsync(_ticket, bundle, _steps.Select(row => row.Step).ToList(), (index, update) => Ui.Post(() =>
        {
            _steps[index].Update(update);
            UpdateTotal();
        }), _cancel.Token);
        _running = false;

        var succeeded = results.Count(step => step.State == ActionState.Succeeded);
        var failed = results.Count(step => step.State == ActionState.Failed);
        _agentInstalled = bundle.IncludesAgent && results.LastOrDefault() is { Package: null, Script: null, State: ActionState.Succeeded };
        if (failed > 0)
        {
            Message.Show($"{failed} von {results.Count} Schritten sind fehlgeschlagen. Details im Protokoll ({AppPaths.LogDirectory}).", InfoSeverity.Warning);
        }
        else if (!_cancel.IsCancellationRequested)
        {
            Message.Show(_agentInstalled ? "Alles installiert. ThixInstall ist jetzt mit deiner Cloud verbunden und hält die Apps aktuell." : "Alles installiert.", InfoSeverity.Success);
        }

        Finish(_cancel.IsCancellationRequested ? $"Abgebrochen – {succeeded} von {results.Count} erledigt." : $"{succeeded} von {results.Count} erledigt.");
    }

    private void UpdateTotal()
    {
        var finished = _steps.Count(row => row.Step.IsFinished);
        var running = _steps.FirstOrDefault(row => row.IsRunning);
        var part = running?.Step.Progress ?? 0;
        TotalProgress.Value = _steps.Count == 0 ? 0 : (finished + part * 0.8) * 100.0 / _steps.Count;
        SummaryText.Text = running is null ? $"{finished} von {_steps.Count} erledigt" : $"{running.Title} · {finished + 1} von {_steps.Count}";
    }

    private void Finish(string summary)
    {
        _running = false;
        TotalProgress.Value = 100;
        SummaryText.Text = summary;
        CancelButton.IsVisible = false;
        DoneButton.IsVisible = true;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (!_running)
        {
            Close();
            return;
        }

        _cancel.Cancel();
        CancelButton.IsEnabled = false;
        SummaryText.Text = "Breche ab … der laufende Schritt endet noch.";
    }

    private void OnDoneClick(object? sender, RoutedEventArgs e) => Close();
}
