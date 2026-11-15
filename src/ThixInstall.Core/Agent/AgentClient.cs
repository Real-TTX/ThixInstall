using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ThixInstall.Core.Business;
using ThixInstall.Core.Models;

namespace ThixInstall.Core.Agent;

/// <summary>
/// How the app reaches the agent. On an installed Windows the agent is the service and the app is only its window: every
/// call goes through the named pipe. Everywhere else (macOS, development) the agent runs in the app's own process and
/// the calls are direct. The app never touches the agent's business classes in another way.
/// </summary>
public static class AgentClient
{
    private static IAgentConnection _connection = new InProcessConnection();

    /// <summary>True when the agent is the Windows service.</summary>
    public static bool UsesService => _connection is PipeConnection;

    /// <summary>
    /// Chooses the agent: the running service when there is one (installed Windows), otherwise the app hosts it itself
    /// (<see cref="AgentHost.Start"/>).
    /// </summary>
    public static void Initialize(bool preferService)
    {
        if (OperatingSystem.IsWindows() && (Environment.GetEnvironmentVariable("THIXINSTALL_AGENT_PIPE") == "1" || (preferService && InstallService.IsServiceRunning)))
        {
            _connection = new PipeConnection();
            return;
        }

        _connection = new InProcessConnection();
        AgentHost.Start(asService: false);
    }

    /// <summary>Tests and the bundle runner use the agent in their own process without starting it.</summary>
    internal static void UseInProcess() => _connection = new InProcessConnection();

    /// <summary>Tests (and "THIXINSTALL_AGENT_PIPE=1" with "--agent-console") talk to the agent through the pipe.</summary>
    internal static void UsePipe() => _connection = new PipeConnection();

    public static Task<AgentStatus> GetStatusAsync() => _connection.CallAsync<AgentStatus>(AgentProtocol.Status, null);

    public static Task<CatalogSnapshot> GetCatalogAsync() => _connection.CallAsync<CatalogSnapshot>(AgentProtocol.Catalog, null);

    public static Task<List<JobRecord>> GetJobsAsync() => _connection.CallAsync<List<JobRecord>>(AgentProtocol.Jobs, null);

    public static Task RefreshAsync() => _connection.CallAsync<object>(AgentProtocol.Refresh, null);

    public static Task<JobRecord> EnqueueAsync(JobRequest request) => _connection.CallAsync<JobRecord>(AgentProtocol.Enqueue, request);

    public static Task CancelAsync(Guid jobId) => _connection.CallAsync<object>(AgentProtocol.Cancel, new CancelPayload(jobId));

    public static Task<Shared.DeviceInfo> ConnectAsync(ConnectRequest request) => _connection.CallAsync<Shared.DeviceInfo>(AgentProtocol.Connect, request);

    public static Task DisconnectAsync() => _connection.CallAsync<object>(AgentProtocol.Disconnect, null);

    public static Task ChangeSettingsAsync(AgentSettingsChange change) => _connection.CallAsync<object>(AgentProtocol.Settings, change);

    public static Task<UpdateCheckResult> CheckForUpdateAsync() => _connection.CallAsync<UpdateCheckResult>(AgentProtocol.CheckUpdate, null);

    public static Task InstallUpdateAsync() => _connection.CallAsync<object>(AgentProtocol.InstallUpdate, null);

    public static Task<JobRecord?> ClaimUserJobAsync() => _connection.CallAsync<JobRecord?>(AgentProtocol.ClaimUserJob, null);

    public static Task CompleteUserJobAsync(Guid jobId, bool succeeded, int? exitCode, string? output) =>
        _connection.CallAsync<object>(AgentProtocol.CompleteUserJob, new CompleteUserJobPayload(jobId, succeeded, exitCode, output));

    /// <summary>The dispatcher of both ways: what one request does (the service calls it for every pipe request).</summary>
    internal static async Task<object?> DispatchAsync(string method, JsonElement? payload, AgentCaller caller)
    {
        T Read<T>() => payload is { } element
            ? element.Deserialize<T>(AgentProtocol.Json) ?? throw new BusinessException("Die Anfrage ist leer.")
            : throw new BusinessException("Die Anfrage ist leer.");

        switch (method)
        {
            case AgentProtocol.Status:
                return AgentHost.GetStatus();
            case AgentProtocol.Catalog:
                return AgentHost.GetCatalog();
            case AgentProtocol.Jobs:
                return AgentHost.GetJobs().ToList();
            case AgentProtocol.Refresh:
                await AgentHost.RefreshAsync();
                return null;
            case AgentProtocol.Enqueue:
                return AgentHost.Enqueue(Read<JobRequest>(), caller);
            case AgentProtocol.Cancel:
                AgentHost.Cancel(Read<CancelPayload>().JobId, caller);
                return null;
            case AgentProtocol.Connect:
                return await AgentHost.ConnectAsync(Read<ConnectRequest>(), caller);
            case AgentProtocol.Disconnect:
                await AgentHost.DisconnectAsync(caller);
                return null;
            case AgentProtocol.Settings:
                await AgentHost.ChangeSettingsAsync(Read<AgentSettingsChange>(), caller);
                return null;
            case AgentProtocol.CheckUpdate:
                return await AgentHost.CheckForUpdateAsync();
            case AgentProtocol.InstallUpdate:
                await AgentHost.InstallUpdateAsync(caller);
                return null;
            case AgentProtocol.ClaimUserJob:
                return AgentHost.ClaimUserJob();
            case AgentProtocol.CompleteUserJob:
                var complete = Read<CompleteUserJobPayload>();
                AgentHost.CompleteUserJob(complete.JobId, complete.Succeeded, complete.ExitCode, complete.Output);
                return null;
            default:
                throw new BusinessException($"Unbekannte Anfrage „{method}“.");
        }
    }

    private interface IAgentConnection
    {
        Task<T> CallAsync<T>(string method, object? payload);
    }

    /// <summary>The agent in this process: the same dispatcher, without the pipe (the objects are passed as they are).</summary>
    private sealed class InProcessConnection : IAgentConnection
    {
        public async Task<T> CallAsync<T>(string method, object? payload)
        {
            JsonElement? element = payload is null ? null : JsonSerializer.SerializeToElement(payload, payload.GetType(), AgentProtocol.Json);
            var result = await Task.Run(() => DispatchAsync(method, element, AgentCaller.CurrentProcess));
            return result is T typed ? typed : default!;
        }
    }

    /// <summary>The service: one connection per request (requests are rare and small; nothing to keep alive).</summary>
    private sealed class PipeConnection : IAgentConnection
    {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

        public async Task<T> CallAsync<T>(string method, object? payload)
        {
            await using var pipe = new NamedPipeClientStream(".", AgentProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var timeout = new CancellationTokenSource(ConnectTimeout);
                await pipe.ConnectAsync(timeout.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException or UnauthorizedAccessException)
            {
                throw new BusinessException("Der ThixInstall-Dienst antwortet nicht. Läuft der Dienst „ThixInstall Agent“?");
            }

            var request = new AgentRequest(method, payload is null ? null : JsonSerializer.SerializeToElement(payload, payload.GetType(), AgentProtocol.Json));
            var line = JsonSerializer.Serialize(request, AgentProtocol.Json).Replace("\n", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(line);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var answer = await reader.ReadLineAsync() ?? throw new BusinessException("Der ThixInstall-Dienst hat die Verbindung beendet.");
            var response = JsonSerializer.Deserialize<AgentResponse>(answer, AgentProtocol.Json) ?? throw new BusinessException("Leere Antwort des Dienstes.");
            if (!response.Ok)
            {
                throw new BusinessException(response.Error ?? "Der Dienst meldet einen Fehler.", response.Field);
            }

            return response.Payload is { } element && element.ValueKind != JsonValueKind.Null ? element.Deserialize<T>(AgentProtocol.Json)! : default!;
        }
    }
}
