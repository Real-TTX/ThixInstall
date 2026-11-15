using System.Net.Http;
using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Models;
using ThixInstall.Core.Platform;
using ThixInstall.Shared;

namespace ThixInstall.Core.Business;

/// <summary>
/// The agent's connection to ThixInstall.Cloud: enrolling and leaving, and the loop that keeps the device managed while
/// it is connected:
///   at start and every <see cref="CatalogInterval"/>: device facts, catalog, (Sync) inventory;
///   with Sync: the open actions become jobs, then a long poll waits for new ones.
/// Errors make the device "offline" and it tries again (30 s, growing to 5 min); a refused token signs it out.
/// </summary>
public static class CloudService
{
    public static readonly TimeSpan CatalogInterval = TimeSpan.FromHours(6);

    private static readonly object Gate = new();
    private static readonly SemaphoreSlim Wake = new(0);
    private static CancellationTokenSource? _loopStop;
    private static Task? _loop;
    private static string? _token;
    private static CloudState _state;
    private static string _message = "Nicht verbunden";
    private static DateTimeOffset? _lastSync;
    private static DateTimeOffset _lastCatalog = DateTimeOffset.MinValue;

    /// <summary>Raised when the state changed (any thread).</summary>
    public static event EventHandler? StatusChanged;

    public static CloudConnection Connection => SettingsStore.ReadAgent().Cloud;

    public static CloudState State
    {
        get
        {
            lock (Gate)
            {
                return Connection.IsConnected ? _state : CloudState.Disconnected;
            }
        }
    }

    public static string Message
    {
        get
        {
            lock (Gate)
            {
                return Connection.IsConnected ? _message : "Nicht verbunden";
            }
        }
    }

    public static DateTimeOffset? LastSync
    {
        get
        {
            lock (Gate)
            {
                return _lastSync;
            }
        }
    }

    /// <summary>The device token (read once from DPAPI / Keychain).</summary>
    internal static string? Token
    {
        get
        {
            lock (Gate)
            {
                var connection = Connection;
                return connection.IsConnected ? _token ??= TokenStore.Read(TokenAccount(connection), connection.ProtectedToken) : null;
            }
        }
    }

    public static void Start()
    {
        lock (Gate)
        {
            if (_loop is not null || !Connection.IsConnected || AccessPolicy.IsCloudDisabled)
            {
                return;
            }

            _loopStop = new CancellationTokenSource();
            var stop = _loopStop.Token;
            _state = CloudState.Connecting;
            _message = "Verbinde …";
            _loop = Task.Run(() => LoopAsync(stop));
        }

        Notify();
    }

    public static void Stop()
    {
        Task? loop;
        lock (Gate)
        {
            _loopStop?.Cancel();
            loop = _loop;
            _loop = null;
        }

        Wake.Release();
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }
    }

    /// <summary>Asks the cloud at once (network back, the user pressed "Aktualisieren").</summary>
    public static void Kick() => Wake.Release();

    /// <summary>
    /// Joins a cloud with an account or a profile's enrollment key (or a bundle's token). The token is stored protected;
    /// the loop starts (not for the setup and the bundle runner: the service does that). Permission checks: the policies here,
    /// the caller in <see cref="AgentHost"/>.
    /// </summary>
    public static async Task<DeviceInfo> ConnectAsync(ConnectRequest request, string? bundleToken = null, bool startLoop = true, CancellationToken cancellationToken = default)
    {
        AccessPolicy.EnsureCloudAllowed();
        var serverUrl = ValidateUrl(request.ServerUrl);
        var byKey = !string.IsNullOrWhiteSpace(request.EnrollmentKey);
        var byBundle = !string.IsNullOrWhiteSpace(bundleToken);
        if (!byKey && !byBundle)
        {
            if (string.IsNullOrWhiteSpace(request.UserName))
            {
                throw new BusinessException("Bitte den Benutzernamen angeben.", "UserName");
            }

            if (string.IsNullOrEmpty(request.Password))
            {
                throw new BusinessException("Bitte das Passwort angeben.", "Password");
            }
        }

        var enroll = new EnrollRequest
        {
            UserName = byKey || byBundle ? null : request.UserName!.Trim(),
            Password = byKey || byBundle ? null : request.Password,
            EnrollmentKey = byKey ? request.EnrollmentKey!.Trim() : null,
            BundleToken = byBundle ? bundleToken : null,
            Device = MachineInfo.Facts(request.DeviceName),
            EnableSync = request.EnableSync || byKey || byBundle,
        };

        EnrollResponse response;
        try
        {
            using var client = CloudClient.Create(serverUrl);
            response = await client.EnrollAsync(enroll, cancellationToken);
        }
        catch (CloudException exception)
        {
            throw new BusinessException(exception.Message, exception.Field ?? (exception.IsUnauthorized ? "Password" : null));
        }
        catch (HttpRequestException exception)
        {
            throw new BusinessException($"Die Cloud ist nicht erreichbar: {exception.Message}", "ServerUrl");
        }
        catch (TaskCanceledException)
        {
            throw new BusinessException("Die Cloud antwortet nicht.", "ServerUrl");
        }

        Stop();
        var info = response.Device;
        var account = $"{serverUrl}|{info.Id}";
        var protectedToken = TokenStore.Save(account, response.Token);
        SettingsStore.UpdateAgent(settings => settings with
        {
            SyncEnabled = info.SyncEnabled,
            Cloud = new CloudConnection
            {
                ServerUrl = serverUrl,
                UserName = info.UserName,
                DisplayName = info.UserDisplayName,
                DeviceName = info.Name,
                DeviceId = info.Id,
                ProfileName = info.ProfileName,
                IsManaged = byKey || byBundle,
                ProtectedToken = protectedToken,
            },
        });
        lock (Gate)
        {
            _token = response.Token;
            _lastCatalog = DateTimeOffset.MinValue;
        }

        Log.Info($"Connected to {serverUrl} as device {info.Id} ({info.Name}) of {info.UserName}");
        if (startLoop)
        {
            Start();
        }

        return info;
    }

    /// <summary>Leaves the cloud: the token is revoked there (if reachable) and forgotten here.</summary>
    public static async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var connection = Connection;
        if (!connection.IsConnected)
        {
            return;
        }

        Stop();
        try
        {
            using var client = CloudClient.Create(connection.ServerUrl, Token);
            await client.LogoutAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is CloudException or HttpRequestException or TaskCanceledException)
        {
            Log.Warning($"The cloud could not be told about the logout: {exception.Message}");
        }

        TokenStore.Delete(TokenAccount(connection));
        SettingsStore.UpdateAgent(settings => settings with { Cloud = new CloudConnection() });
        CatalogService.Clear();
        lock (Gate)
        {
            _token = null;
            _state = CloudState.Disconnected;
            _message = "Nicht verbunden";
        }

        Log.Info("Disconnected from the cloud");
        Notify();
    }

    /// <summary>Switches "Sync" and tells the cloud.</summary>
    public static async Task SetSyncAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        SettingsStore.UpdateAgent(settings => settings with { SyncEnabled = enabled });
        var connection = Connection;
        if (connection.IsConnected)
        {
            try
            {
                using var client = CloudClient.Create(connection.ServerUrl, Token);
                await client.UpdateDeviceAsync(new DeviceUpdate(SyncEnabled: enabled), cancellationToken);
            }
            catch (Exception exception) when (exception is CloudException or HttpRequestException or TaskCanceledException)
            {
                Log.Warning($"Sync could not be changed in the cloud: {exception.Message}");
            }
        }

        Kick();
        Notify();
    }

    /// <summary>Loads the catalog now (also without Sync).</summary>
    public static async Task RefreshCatalogAsync(CancellationToken cancellationToken = default)
    {
        var connection = Connection;
        if (!connection.IsConnected)
        {
            return;
        }

        await CatalogService.RefreshAsync(connection.ServerUrl, Token, cancellationToken);
        lock (Gate)
        {
            _lastCatalog = DateTimeOffset.Now;
        }
    }

    internal static async Task ReportInventoryAsync(CancellationToken cancellationToken)
    {
        var connection = Connection;
        if (!connection.IsConnected || !SettingsStore.ReadAgent().SyncEnabled || CatalogService.Current is not { } catalog)
        {
            return;
        }

        using var client = CloudClient.Create(connection.ServerUrl, Token);
        await client.PutInventoryAsync(InventoryService.BuildReport(catalog.Packages), cancellationToken);
    }

    /// <summary>Tells the cloud that one of its actions starts. False when it was cancelled there meanwhile.</summary>
    internal static async Task<bool> StartActionAsync(long actionId)
    {
        var connection = Connection;
        if (!connection.IsConnected)
        {
            return true;
        }

        try
        {
            using var client = CloudClient.Create(connection.ServerUrl, Token);
            await client.StartActionAsync(actionId, CancellationToken.None);
            return true;
        }
        catch (CloudException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return false;
        }
        catch (Exception exception) when (exception is CloudException or HttpRequestException or TaskCanceledException)
        {
            Log.Warning($"Action {actionId} could not be started in the cloud: {exception.Message}");
            return true;
        }
    }

    /// <summary>The result of a job for the cloud: the end of its action, or a report of a job of the device itself.</summary>
    internal static async Task ReportJobAsync(JobRecord job)
    {
        var connection = Connection;
        if (!connection.IsConnected || job.CloudUrl is not null)
        {
            return;
        }

        var result = new ActionResult(
            job.State == ActionState.Succeeded,
            job.ExitCode,
            job.Output,
            job.InstalledVersion,
            job.StartedAt ?? job.CreatedAt,
            job.FinishedAt ?? DateTimeOffset.Now,
            job.State == ActionState.Cancelled);
        try
        {
            using var client = CloudClient.Create(connection.ServerUrl, Token);
            if (job.CloudActionId is { } actionId)
            {
                await client.FinishActionAsync(actionId, result, CancellationToken.None);
            }
            else if (SettingsStore.ReadAgent().SyncEnabled)
            {
                await client.ReportAsync(new LocalRunReport(job.Kind, job.Origin, job.Title, job.Package?.Id, job.Script?.Id, result), CancellationToken.None);
            }

            if (SettingsStore.ReadAgent().SyncEnabled && job.Kind is ActionKind.Install or ActionKind.Update or ActionKind.Uninstall)
            {
                await ReportInventoryAsync(CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is CloudException or HttpRequestException or TaskCanceledException)
        {
            Log.Warning($"The result of \"{job.Title}\" could not be reported: {exception.Message}");
        }
    }

    private static async Task LoopAsync(CancellationToken stop)
    {
        var delay = TimeSpan.FromSeconds(30);
        var factsSent = false;
        while (!stop.IsCancellationRequested)
        {
            var connection = Connection;
            if (!connection.IsConnected)
            {
                return;
            }

            try
            {
                using var client = CloudClient.Create(connection.ServerUrl, Token);
                var settings = SettingsStore.ReadAgent();
                if (!factsSent)
                {
                    var info = await client.UpdateDeviceAsync(new DeviceUpdate(settings.SyncEnabled, MachineInfo.Facts(connection.DeviceName)), stop);
                    SettingsStore.UpdateAgent(current => current with
                    {
                        Cloud = current.Cloud with { DeviceName = info.Name, ProfileName = info.ProfileName, UserName = info.UserName, DisplayName = info.UserDisplayName },
                    });
                    factsSent = true;
                }

                var catalogDue = DateTimeOffset.Now - _lastCatalog > CatalogInterval;
                if (catalogDue)
                {
                    await CatalogService.RefreshAsync(connection.ServerUrl, Token, stop);
                    lock (Gate)
                    {
                        _lastCatalog = DateTimeOffset.Now;
                    }

                    await Task.Run(InventoryService.Refresh, stop);
                    await ReportInventoryAsync(stop);
                }

                SetState(CloudState.Online, settings.SyncEnabled ? "Verbunden · Sync an" : "Verbunden · Sync aus");
                delay = TimeSpan.FromSeconds(30);

                if (!settings.SyncEnabled)
                {
                    await WaitAsync(TimeSpan.FromMinutes(15), stop);
                    continue;
                }

                await FetchActionsAsync(client, stop);
                var since = SettingsStore.ReadAgent().Cloud.LastActionRevision;
                var waited = await client.WaitForActionsAsync(since, ApiContract.MaxWaitSeconds, stop);
                if (waited.Latest > since)
                {
                    await FetchActionsAsync(client, stop);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (CloudException exception) when (exception.IsUnauthorized)
            {
                Log.Warning("The cloud refused the device token: signed out");
                SetState(CloudState.SignedOut, "Von der Cloud abgemeldet – das Gerät wurde dort entfernt. Bitte neu verbinden.");
                await WaitAsync(TimeSpan.FromMinutes(10), stop);
            }
            catch (Exception exception) when (exception is CloudException or HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
            {
                SetState(CloudState.Offline, $"Cloud nicht erreichbar ({exception.Message})");
                await WaitAsync(delay, stop);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
            }
        }
    }

    private static async Task FetchActionsAsync(CloudClient client, CancellationToken stop)
    {
        var response = await client.GetActionsAsync(stop);
        foreach (var action in response.Actions.Where(action => action.State == ActionState.Pending))
        {
            if (JobService.IsKnownAction(action.Id))
            {
                continue;
            }

            Log.Info($"Action {action.Id} from the cloud: {action.Title}");
            JobService.Enqueue(JobService.NewJob(action.Kind, action.Origin, action.Package, action.Script, action.Id, action.RequestedBy, title: action.Title));
        }

        SettingsStore.UpdateAgent(settings => settings with { Cloud = settings.Cloud with { LastActionRevision = Math.Max(settings.Cloud.LastActionRevision, response.Latest) } });
        lock (Gate)
        {
            _lastSync = DateTimeOffset.Now;
        }

        Notify();
    }

    private static async Task WaitAsync(TimeSpan delay, CancellationToken stop)
    {
        try
        {
            await Wake.WaitAsync(delay, stop);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void SetState(CloudState state, string message)
    {
        bool changed;
        lock (Gate)
        {
            changed = _state != state || _message != message;
            _state = state;
            _message = message;
            if (state == CloudState.Online)
            {
                _lastSync = DateTimeOffset.Now;
            }
        }

        if (changed)
        {
            Notify();
        }
    }

    private static void Notify() => StatusChanged?.Invoke(null, EventArgs.Empty);

    private static string TokenAccount(CloudConnection connection) => $"{connection.ServerUrl}|{connection.DeviceId}";

    internal static string ValidateUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new BusinessException("Bitte die Adresse der ThixInstall.Cloud angeben.", "ServerUrl");
        }

        var normalized = CloudClient.NormalizeUrl(url);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || uri.Host.Length == 0)
        {
            throw new BusinessException("Das ist keine gültige Adresse (z. B. https://install.example.com).", "ServerUrl");
        }

        return normalized;
    }

    /// <summary>Tests start disconnected.</summary>
    internal static void ResetForTests()
    {
        Stop();
        lock (Gate)
        {
            _token = null;
            _state = CloudState.Disconnected;
            _lastCatalog = DateTimeOffset.MinValue;
        }
    }
}
