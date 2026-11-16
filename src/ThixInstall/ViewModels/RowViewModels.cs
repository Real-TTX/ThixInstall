using Avalonia.Media.Imaging;
using ThixInstall.Controls;
using ThixInstall.Core.Installers;
using ThixInstall.Core.Models;
using ThixInstall.Shared;

namespace ThixInstall.ViewModels;

/// <summary>An app of the catalog as a tile or row: its state on this device, chosen or not, its icon.</summary>
public sealed class AppTileViewModel : ObservableObject
{
    private CatalogApp _app;
    private bool _isSelected;
    private Bitmap? _icon;

    public AppTileViewModel(CatalogApp app)
    {
        _app = app;
    }

    public CatalogApp App => _app;

    public PackageDto Package => _app.Package;

    public long Id => _app.Package.Id;

    public string Name => _app.Package.Name;

    public string Key => _app.Package.Key;

    public string Category => _app.Package.Category;

    public string Subtitle => string.IsNullOrWhiteSpace(_app.Package.Publisher) ? _app.Package.Category : _app.Package.Publisher!;

    public string? Description => _app.Package.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool IsInstalled => _app.IsInstalled;

    public bool IsUpdateAvailable => _app.IsUpdateAvailable;

    public bool IsBusy => _app.IsBusy;

    public string StateText => _app switch
    {
        { IsBusy: true } => "In Arbeit …",
        { IsUpdateAvailable: true } => $"Update auf {_app.Package.LatestVersion}",
        { IsInstalled: true } => _app.InstalledVersion is { } version ? $"Installiert · {version}" : "Installiert",
        _ => _app.Package.LatestVersion is { } latest ? $"Version {latest}" : "",
    };

    public ChipTone StateTone => _app switch
    {
        { IsBusy: true } => ChipTone.Accent,
        { IsUpdateAvailable: true } => ChipTone.Warning,
        { IsInstalled: true } => ChipTone.Success,
        _ => ChipTone.Neutral,
    };

    /// <summary>"133.0 → 134.0" on the updates page.</summary>
    public string VersionChange => $"{_app.InstalledVersion ?? "?"} → {_app.Package.LatestVersion ?? "?"}";

    public string SourceText => _app.Package.Install?.Method switch
    {
        InstallMethod.Winget => _app.Package.Install.DownloadUrl is null ? "winget" : "winget · Cloud-Cache",
        InstallMethod.Chocolatey => "Chocolatey",
        InstallMethod.Direct => "Direkter Download · Cloud-Cache",
        InstallMethod.HomebrewCask => _app.Package.Install.DownloadUrl is null ? "Homebrew" : "Homebrew · Cloud-Cache",
        InstallMethod.Homebrew => "Homebrew",
        InstallMethod.MicrosoftStore => "Microsoft Store",
        InstallMethod.Upload => "Eigene Datei · Cloud-Cache",
        _ => "",
    };

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public Bitmap? Icon
    {
        get => _icon;
        set => SetField(ref _icon, value);
    }

    public void Update(CatalogApp app)
    {
        _app = app;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>A script of the catalog as a row.</summary>
public sealed class ScriptRowViewModel(ScriptDto script) : ObservableObject
{
    public ScriptDto Script { get; } = script;

    public long Id => Script.Id;

    public string Name => Script.Name;

    public string? Description => Script.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Script.Description);

    public string Category => Script.Category;

    public string ShellText => ScriptRunner.ShellName(Script.Shell);

    public string ContextText => Script.Context == ScriptContext.User ? "Benutzer" : "System (Administrator)";

    public ChipTone ContextTone => Script.Context == ScriptContext.User ? ChipTone.Neutral : ChipTone.Warning;
}

/// <summary>A job as a row of "Aufträge".</summary>
public sealed class JobRowViewModel : ObservableObject
{
    private JobRecord _job;
    private bool _isSelected;

    public JobRowViewModel(JobRecord job)
    {
        _job = job;
    }

    public JobRecord Job => _job;

    public Guid Id => _job.Id;

    public string Title => _job.Title;

    public string StateText => AgentMonitor.StateText(_job);

    public ChipTone StateTone => _job.State switch
    {
        ActionState.Running => ChipTone.Accent,
        ActionState.Succeeded => ChipTone.Success,
        ActionState.Failed => ChipTone.Danger,
        ActionState.Cancelled => ChipTone.Neutral,
        _ => ChipTone.Neutral,
    };

    public string ShortState => _job.State switch
    {
        ActionState.Pending => "Wartet",
        ActionState.Running => "Läuft",
        ActionState.Succeeded => "Erledigt",
        ActionState.Failed => "Fehlgeschlagen",
        _ => "Abgebrochen",
    };

    public string OriginText => _job.Origin switch
    {
        ActionOrigin.Push => _job.RequestedBy is { } who ? $"Aus der Cloud · {who}" : "Aus der Cloud",
        ActionOrigin.Profile => _job.RequestedBy is { } profile ? $"Profil · {profile}" : "Profil",
        ActionOrigin.Bundle => "Paket",
        _ => _job.RequestedBy is { } user ? $"Auf diesem Gerät · {user}" : "Auf diesem Gerät",
    };

    public string TimeText => Ui.FormatDateTime(_job.FinishedAt ?? _job.StartedAt ?? _job.CreatedAt);

    public bool IsRunning => _job.State == ActionState.Running;

    public double ProgressValue => (_job.Progress ?? 0) * 100;

    public bool HasProgress => _job.State == ActionState.Running && _job.Progress is not null;

    public string Glyph => _job.Kind switch
    {
        ActionKind.RunScript => "Icon.Terminal",
        ActionKind.Uninstall => "Icon.Delete",
        ActionKind.Update or ActionKind.UpdateAll => "Icon.Sync",
        ActionKind.Refresh => "Icon.List",
        _ => "Icon.Download",
    };

    public Avalonia.Media.Geometry GlyphData => Ui.Icon(Glyph);

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public void Update(JobRecord job)
    {
        _job = job;
        OnPropertyChanged(string.Empty);
    }
}
