using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Models;

namespace ThixInstall.Core.Agent;

/// <summary>
/// The service's side of the pipe: every signed-in user may talk to the agent (the app of each user), the agent learns
/// who it is (the user name, an elevated administrator or not) and the business layer decides what that caller may do.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AgentPipeServer
{
    private const int MaxRequestLength = 1024 * 1024;

    public static Task RunAsync(CancellationToken stop) => Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(AgentProtocol.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, Security());
                await pipe.WaitForConnectionAsync(stop);
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => ServeAsync(connected, stop), stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"Agent pipe: {exception.Message}");
                await Task.Delay(1000, stop).ContinueWith(_ => { }, TaskScheduler.Default);
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }, stop);

    private static async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        await using (pipe)
        {
            AgentResponse response;
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(stop);
                if (line is null || line.Length > MaxRequestLength)
                {
                    return;
                }

                // Windows lets the server take the client's identity only after it read from the pipe.
                var caller = Caller(pipe);
                var request = JsonSerializer.Deserialize<AgentRequest>(line, AgentProtocol.Json);
                if (request is null)
                {
                    return;
                }

                var result = await AgentClient.DispatchAsync(request.Method, request.Payload, caller);
                response = new AgentResponse(true, result is null ? null : JsonSerializer.SerializeToElement(result, result.GetType(), AgentProtocol.Json));
            }
            catch (BusinessException exception)
            {
                response = new AgentResponse(false, null, exception.Message, exception.Field);
            }
            catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                Log.Warning($"Agent pipe request failed: {exception.Message}");
                response = new AgentResponse(false, null, $"Der Dienst konnte die Anfrage nicht ausführen: {exception.Message}");
            }

            try
            {
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, AgentProtocol.Json));
                pipe.WaitForPipeDrain();
            }
            catch (IOException)
            {
                // the app went away
            }
        }
    }

    /// <summary>The user at the other end; an administrator only with an elevated token (UAC), like everywhere in Windows.</summary>
    private static AgentCaller Caller(NamedPipeServerStream pipe)
    {
        var name = "Benutzer";
        var isAdministrator = false;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                name = identity.Name.Contains('\\') ? identity.Name[(identity.Name.IndexOf('\\') + 1)..] : identity.Name;
                isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warning($"The caller of the pipe could not be identified: {exception.Message}");
        }

        return new AgentCaller(name, isAdministrator);
    }

    private static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        // The process itself (the service: LocalSystem) creates the next instances of the pipe.
        using (var self = WindowsIdentity.GetCurrent())
        {
            security.AddAccessRule(new PipeAccessRule(self.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
