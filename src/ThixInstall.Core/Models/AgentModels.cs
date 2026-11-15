using ThixInstall.Shared;

namespace ThixInstall.Core.Models;

public enum CloudState
{
    /// <summary>No cloud configured.</summary>
    Disconnected = 0,
    Connecting = 1,
    Online = 2,
    /// <summary>Configured, but not reachable right now.</summary>
    Offline = 3,
    /// <summary>The cloud refused the token: the device was removed there.</summary>
    SignedOut = 4,
}

/// <summary>
/// One job of the agent: what it is, where it came from, how it went. Kept in jobs.json (the newest
/// <see cref="Business.JobService.HistoryLength"/>), shown on the page "Aufträge".
/// </summary>
public sealed record JobRecord
{
    public required Guid Id { get; init; }

    public required ActionKind Kind { get; init; }

    public required ActionOrigin Origin { get; init; }

    /// <summary>German, e.g. "Firefox installieren".</summary>
    public required string Title { get; init; }

    public PackageDto? Package { get; init; }

    public ScriptDto? Script { get; init; }

    /// <summary>The action of the cloud this job carries out (null for jobs of the device itself).</summary>
    public long? CloudActionId { get; init; }

    /// <summary>
    /// The cloud the installer comes from when it is not the connected one (a downloaded bundle); null = the connected cloud.
    /// </summary>
    public string? CloudUrl { get; init; }

    public ActionState State { get; init; } = ActionState.Pending;

    /// <summary>What happens right now ("Lade herunter …", "Wartet auf den Benutzer"), for the UI.</summary>
    public string? Status { get; init; }

    /// <summary>0..1 while downloading; null when unknown.</summary>
    public double? Progress { get; init; }

    /// <summary>Who asked for it: the user name on the device, the cloud user, "Profil …".</summary>
    public string? RequestedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    public int? ExitCode { get; init; }

    /// <summary>The end of the output of the installer or script.</summary>
    public string? Output { get; init; }

    public string? InstalledVersion { get; init; }

    /// <summary>A script in the user's context: run by the app in the user's session, not by the service.</summary>
    public bool RunsAsUser => Script is { Context: ScriptContext.User };

    public bool IsFinished => State is ActionState.Succeeded or ActionState.Failed or ActionState.Cancelled;
}

/// <summary>A job the user (or the bundle runner) asks for. <see cref="PackageId"/> or <see cref="ScriptId"/> refer to the catalog.</summary>
public sealed record JobRequest(ActionKind Kind, long? PackageId = null, long? ScriptId = null);

/// <summary>A catalog app together with what this device has installed.</summary>
public sealed record CatalogApp(
    PackageDto Package,
    string? InstalledVersion,
    bool IsInstalled,
    bool IsUpdateAvailable,
    // A job for this app is queued or running.
    bool IsBusy);

/// <summary>The catalog as the app shows it.</summary>
public sealed record CatalogSnapshot(
    IReadOnlyList<CatalogApp> Apps,
    IReadOnlyList<ScriptDto> Scripts,
    DateTimeOffset? LoadedAt,
    // Why the list is empty or old (no cloud, offline), else null.
    string? Message)
{
    public static CatalogSnapshot Empty(string message) => new([], [], null, message);
}

/// <summary>Everything the main window shows about the agent; polled by the app.</summary>
public sealed record AgentStatus
{
    public required string AgentVersion { get; init; }

    public required int AgentBuildNumber { get; init; }

    /// <summary>The agent runs as the Windows service (the app is only its window).</summary>
    public bool IsService { get; init; }

    /// <summary>The agent has administrator rights (service, elevated, root).</summary>
    public bool IsElevated { get; init; }

    public CloudState Cloud { get; init; }

    public string CloudMessage { get; init; } = "";

    public string? ServerUrl { get; init; }

    public string? UserName { get; init; }

    public string? DeviceName { get; init; }

    public string? ProfileName { get; init; }

    public bool IsManaged { get; init; }

    public DateTimeOffset? LastSync { get; init; }

    public bool SyncEnabled { get; init; }

    public bool AutoUpdateApps { get; init; }

    public bool CheckForUpdates { get; init; }

    public bool InstallUpdatesAutomatically { get; init; }

    public bool AutostartEnabled { get; init; }

    /// <summary>A newer ThixInstall (version text) is available.</summary>
    public string? AvailableUpdate { get; init; }

    public string? UpdateMessage { get; init; }

    public DateTimeOffset? LastUpdateCheck { get; init; }

    public JobRecord? CurrentJob { get; init; }

    public int QueuedJobs { get; init; }

    public int UpdatesAvailable { get; init; }

    /// <summary>Grows with every change of the jobs: the app reloads the list when it changed.</summary>
    public long JobsRevision { get; init; }

    /// <summary>Grows when the catalog or the inventory changed.</summary>
    public long CatalogRevision { get; init; }

    public bool IsConfigurationLocked { get; init; }

    public bool IsCloudDisabled { get; init; }

    public bool AreUpdatesDisabled { get; init; }
}

/// <summary>Connect this device to a cloud: with an account, or with a profile's enrollment key.</summary>
public sealed record ConnectRequest(
    string ServerUrl,
    string? UserName,
    string? Password,
    string? EnrollmentKey,
    string? DeviceName,
    bool EnableSync = true);

/// <summary>A change of the agent's settings (null = unchanged).</summary>
public sealed record AgentSettingsChange(
    bool? SyncEnabled = null,
    bool? AutoUpdateApps = null,
    bool? CheckForUpdates = null,
    bool? InstallUpdatesAutomatically = null,
    bool? Autostart = null);

/// <summary>Who called the agent (the pipe tells the service; in the app's own process it is the user).</summary>
public sealed record AgentCaller(string UserName, bool IsAdministrator)
{
    public static AgentCaller CurrentProcess => new(Environment.UserName, Platform.Elevation.IsElevated);
}
