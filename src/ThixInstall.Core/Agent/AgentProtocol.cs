using System.Text.Json;
using ThixInstall.Core.Infrastructure;

namespace ThixInstall.Core.Agent;

/// <summary>
/// The requests between the app and the Windows service over the named pipe <see cref="PipeName"/>: one JSON line per
/// request and per answer. The methods are the ones of <see cref="Business.AgentHost"/>.
/// </summary>
internal static class AgentProtocol
{
    /// <summary>The pipe of the service (tests use one of their own).</summary>
    public static string PipeName { get; internal set; } = "ThixInstall.Agent";

    public const string Status = "status";
    public const string Catalog = "catalog";
    public const string Jobs = "jobs";
    public const string Refresh = "refresh";
    public const string Enqueue = "enqueue";
    public const string Cancel = "cancel";
    public const string Connect = "connect";
    public const string Disconnect = "disconnect";
    public const string Settings = "settings";
    public const string CheckUpdate = "check-update";
    public const string InstallUpdate = "install-update";
    public const string ClaimUserJob = "claim-user-job";
    public const string CompleteUserJob = "complete-user-job";

    /// <summary>One line per message: the JSON of the contract, never indented.</summary>
    public static readonly JsonSerializerOptions Json = Shared.ApiContract.Json;
}

internal sealed record AgentRequest(string Method, JsonElement? Payload);

internal sealed record AgentResponse(bool Ok, JsonElement? Payload, string? Error = null, string? Field = null);

internal sealed record CancelPayload(Guid JobId);

internal sealed record CompleteUserJobPayload(Guid JobId, bool Succeeded, int? ExitCode, string? Output);
