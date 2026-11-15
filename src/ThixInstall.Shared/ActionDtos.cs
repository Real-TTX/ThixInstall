namespace ThixInstall.Shared;

/// <summary>
/// One action for a device ("Auftrag"): install, update or uninstall a package, run a script, update everything.
/// The package or script travels with the action, so the device needs no catalog to run it.
/// </summary>
public sealed record ActionDto
{
    public required long Id { get; init; }

    public required ActionKind Kind { get; init; }

    public required ActionState State { get; init; }

    public required ActionOrigin Origin { get; init; }

    /// <summary>German, e.g. "Firefox installieren".</summary>
    public required string Title { get; init; }

    public PackageDto? Package { get; init; }

    public ScriptDto? Script { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The display name of who asked for it (a user, or the profile).</summary>
    public string? RequestedBy { get; init; }

    public long Revision { get; init; }
}

/// <summary>GET <see cref="ApiContract.Actions"/>: the open actions of the device, oldest first.</summary>
public sealed record ActionsResponse(IReadOnlyList<ActionDto> Actions, long Latest);

/// <summary>GET <see cref="ApiContract.ActionsWait"/>: the latest action revision of the device.</summary>
public sealed record WaitResult(long Latest);

/// <summary>POST <see cref="ApiContract.ActionFinish"/>: how an action ended on the device.</summary>
public sealed record ActionResult(
    bool Succeeded,
    int? ExitCode,
    // The end of the output (at most <see cref="ApiContract.MaxOutputLength"/> characters).
    string? Output,
    // The version found after an install or update.
    string? InstalledVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool WasCancelled = false);

/// <summary>POST <see cref="ApiContract.ActionReport"/>: a job the device ran on its own (user, bundle, auto update).</summary>
public sealed record LocalRunReport(
    ActionKind Kind,
    ActionOrigin Origin,
    string Title,
    long? PackageId,
    long? ScriptId,
    ActionResult Result);
