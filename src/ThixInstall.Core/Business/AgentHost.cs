using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Models;
using ThixInstall.Shared;

namespace ThixInstall.Core.Business;

/// <summary>
/// The agent: everything ThixInstall does on a device besides showing it. It runs in the Windows service (installed
/// Windows) or inside the app (macOS, development). The app talks to it through <see cref="Agent.AgentClient"/>; every
/// method here is what one request of the app does, with the permission checks of the business layer
/// (<see cref="AccessPolicy"/>, the caller).
/// </summary>
public static class AgentHost
{
    private static readonly object Gate = new();
    private static Timer? _timer;
    private static bool _started;
    private static DateTimeOffset _lastInventory = DateTimeOffset.MinValue;
    private static long _catalogRevision;

    public static bool IsService { get; private set; }

    /// <summary>Raised after anything the app shows changed (any thread).</summary>
    public static event EventHandler? Changed;

    /// <summary>Starts the agent: jobs, cloud, the timers (inventory, auto updates, self update).</summary>
    public static void Start(bool asService)
    {
        lock (Gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            IsService = asService;
            AppPaths.IsMachineAgent = asService;
        }

        Log.Info($"Agent {AppInfo.Version} starting ({(asService ? "service" : "in the app")}, {AppInfo.PlatformKey}, elevated: {Platform.Elevation.IsElevated})");
        JobService.Changed += (_, _) => Notify();
        CatalogService.Changed += (_, _) => { Interlocked.Increment(ref _catalogRevision); Notify(); };
        InventoryService.Changed += (_, _) => { Interlocked.Increment(ref _catalogRevision); Notify(); };
        CloudService.StatusChanged += (_, _) => Notify();
        UpdateService.Checked += (_, _) => Notify();

        JobService.Start();
        _ = Task.Run(() =>
        {
            InventoryService.Refresh();
            _lastInventory = DateTimeOffset.Now;
        });
        CloudService.Start();
        _timer = new Timer(_ => OnTimer(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        CloudService.Stop();
        JobService.Stop();
        Log.Info("Agent stopped");
    }

    public static AgentStatus GetStatus()
    {
        var settings = SettingsStore.ReadAgent();
        var connection = settings.Cloud;
        var update = UpdateService.LastResult;
        return new AgentStatus
        {
            AgentVersion = AppInfo.Version,
            AgentBuildNumber = AppInfo.BuildNumber,
            IsService = IsService,
            IsElevated = Platform.Elevation.IsElevated,
            Cloud = CloudService.State,
            CloudMessage = CloudService.Message,
            ServerUrl = connection.IsConnected ? connection.ServerUrl : null,
            UserName = connection.IsConnected ? connection.UserName : null,
            DeviceName = connection.IsConnected ? connection.DeviceName : null,
            ProfileName = connection.ProfileName,
            IsManaged = connection.IsManaged,
            LastSync = CloudService.LastSync,
            SyncEnabled = settings.SyncEnabled,
            AutoUpdateApps = settings.AutoUpdateApps,
            CheckForUpdates = settings.CheckForUpdates,
            InstallUpdatesAutomatically = settings.InstallUpdatesAutomatically,
            AutostartEnabled = AutostartService.IsEnabled(),
            AvailableUpdate = update is { IsUpdateAvailable: true, Manifest: { } manifest } ? manifest.Version : null,
            UpdateMessage = update?.Message,
            LastUpdateCheck = UpdateService.LastCheck,
            CurrentJob = JobService.Current,
            QueuedJobs = JobService.QueuedCount,
            UpdatesAvailable = CatalogService.AppsWithUpdates().Count,
            JobsRevision = JobService.Revision,
            CatalogRevision = Interlocked.Read(ref _catalogRevision),
            IsConfigurationLocked = AccessPolicy.IsConfigurationLocked,
            IsCloudDisabled = AccessPolicy.IsCloudDisabled,
            AreUpdatesDisabled = AccessPolicy.AreUpdatesDisabled,
        };
    }

    public static CatalogSnapshot GetCatalog() => CatalogService.Snapshot(JobService.BusyPackages());

    public static IReadOnlyList<JobRecord> GetJobs() => JobService.All();

    /// <summary>"Aktualisieren": catalog from the cloud, inventory of the device.</summary>
    public static async Task RefreshAsync()
    {
        await Task.Run(InventoryService.Refresh);
        _lastInventory = DateTimeOffset.Now;
        try
        {
            await CloudService.RefreshCatalogAsync();
            await CloudService.ReportInventoryAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is CloudException or HttpRequestException or TaskCanceledException)
        {
            throw new BusinessException($"Die Cloud ist nicht erreichbar: {exception.Message}");
        }

        CloudService.Kick();
    }

    /// <summary>A job asked for in the app: install, update, remove an app of the catalog, run a script, update all.</summary>
    public static JobRecord Enqueue(JobRequest request, AgentCaller caller)
    {
        var requestedBy = caller.UserName;
        switch (request.Kind)
        {
            case ActionKind.Install:
            case ActionKind.Update:
            case ActionKind.Uninstall:
            {
                AccessPolicy.EnsureUserMayInstall(caller);
                var package = (request.PackageId is { } id ? CatalogService.FindPackage(id) : null)
                              ?? throw new BusinessException("Diese App gibt es im Katalog nicht (mehr).");
                if (package.Install is null)
                {
                    throw new BusinessException($"{package.Name} gibt es nicht für dieses System.");
                }

                return JobService.Enqueue(JobService.NewJob(request.Kind, ActionOrigin.Device, package: package, requestedBy: requestedBy));
            }

            case ActionKind.RunScript:
            {
                AccessPolicy.EnsureScriptsAllowed();
                var script = (request.ScriptId is { } id ? CatalogService.FindScript(id) : null)
                             ?? throw new BusinessException("Dieses Skript gibt es im Katalog nicht (mehr).");
                if (script.Context == ScriptContext.System)
                {
                    AccessPolicy.EnsureUserMayInstall(caller);
                }

                return JobService.Enqueue(JobService.NewJob(ActionKind.RunScript, ActionOrigin.Device, script: script, requestedBy: requestedBy));
            }

            case ActionKind.UpdateAll:
                AccessPolicy.EnsureUserMayInstall(caller);
                return JobService.Enqueue(JobService.NewJob(ActionKind.UpdateAll, ActionOrigin.Device, requestedBy: requestedBy));

            case ActionKind.Refresh:
                return JobService.Enqueue(JobService.NewJob(ActionKind.Refresh, ActionOrigin.Device, requestedBy: requestedBy));

            default:
                throw new BusinessException("Diese Art von Auftrag gibt es nicht.");
        }
    }

    public static void Cancel(Guid jobId, AgentCaller caller)
    {
        var job = JobService.All().FirstOrDefault(candidate => candidate.Id == jobId) ?? throw new BusinessException("Diesen Auftrag gibt es nicht mehr.");
        if (job.Origin != ActionOrigin.Device && !caller.IsAdministrator && AccessPolicy.IsConfigurationLocked)
        {
            throw new BusinessException("Aufträge der Cloud darf hier nur ein Administrator abbrechen.");
        }

        if (!JobService.Cancel(jobId))
        {
            throw new BusinessException("Der Auftrag ist schon beendet.");
        }
    }

    public static async Task<DeviceInfo> ConnectAsync(ConnectRequest request, AgentCaller caller)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var connection = CloudService.Connection;
        if (connection is { IsConnected: true, IsManaged: true } && !caller.IsAdministrator)
        {
            throw new BusinessException("Dieses Gerät wird über ein Profil verwaltet: nur ein Administrator kann die Verbindung ändern.");
        }

        var info = await CloudService.ConnectAsync(request);
        Notify();
        return info;
    }

    public static async Task DisconnectAsync(AgentCaller caller)
    {
        AccessPolicy.EnsureConfigurationEditable();
        if (CloudService.Connection.IsManaged && !caller.IsAdministrator)
        {
            throw new BusinessException("Dieses Gerät wird über ein Profil verwaltet: nur ein Administrator kann es von der Cloud trennen.");
        }

        await CloudService.DisconnectAsync();
        Notify();
    }

    public static async Task ChangeSettingsAsync(AgentSettingsChange change, AgentCaller caller)
    {
        AccessPolicy.EnsureConfigurationEditable();
        if (change.SyncEnabled is { } sync)
        {
            if (!sync && CloudService.Connection.IsManaged && !caller.IsAdministrator)
            {
                throw new BusinessException("Dieses Gerät wird über ein Profil verwaltet: Sync kann nur ein Administrator ausschalten.");
            }

            await CloudService.SetSyncAsync(sync);
        }

        if (change.CheckForUpdates is not null || change.InstallUpdatesAutomatically is not null)
        {
            AccessPolicy.EnsureUpdatesAllowed();
        }

        SettingsStore.UpdateAgent(settings => settings with
        {
            AutoUpdateApps = change.AutoUpdateApps ?? settings.AutoUpdateApps,
            CheckForUpdates = change.CheckForUpdates ?? settings.CheckForUpdates,
            InstallUpdatesAutomatically = change.InstallUpdatesAutomatically ?? settings.InstallUpdatesAutomatically,
        });

        if (change.Autostart is { } autostart)
        {
            if (autostart)
            {
                AutostartService.Enable();
            }
            else
            {
                AutostartService.Disable();
            }
        }

        Notify();
    }

    public static async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        var result = await UpdateService.CheckAsync();
        Notify();
        return result;
    }

    /// <summary>Downloads and starts the update of ThixInstall itself (the agent ends right after).</summary>
    public static async Task InstallUpdateAsync(AgentCaller caller)
    {
        AccessPolicy.EnsureUpdatesAllowed();
        var result = UpdateService.LastResult is { IsUpdateAvailable: true } known ? known : await UpdateService.CheckAsync();
        if (result is not { IsUpdateAvailable: true, Manifest: { } manifest })
        {
            throw new BusinessException(result.Message);
        }

        Log.Info($"Update to {manifest.Version} requested by {caller.UserName}");
        await UpdateService.DownloadAndStartInstallerAsync(manifest);
    }

    public static JobRecord? ClaimUserJob() => JobService.ClaimUserJob();

    public static void CompleteUserJob(Guid id, bool succeeded, int? exitCode, string? output) => JobService.CompleteUserJob(id, succeeded, exitCode, output);

    private static void OnTimer()
    {
        try
        {
            var settings = SettingsStore.ReadAgent();
            if (DateTimeOffset.Now - _lastInventory > CloudService.CatalogInterval)
            {
                _lastInventory = DateTimeOffset.Now;
                InventoryService.Refresh();
                _ = CloudService.ReportInventoryAsync(CancellationToken.None).ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
            }

            // Auto update of the apps: once a day, when nothing else runs.
            if (settings.AutoUpdateApps && JobService.Current is null && JobService.QueuedCount == 0 &&
                (settings.LastAutoUpdate is not { } last || DateTimeOffset.Now - last > TimeSpan.FromHours(24)))
            {
                SettingsStore.UpdateAgent(current => current with { LastAutoUpdate = DateTimeOffset.Now });
                foreach (var package in CatalogService.AppsWithUpdates())
                {
                    JobService.Enqueue(JobService.NewJob(ActionKind.Update, ActionOrigin.Device, package: package, requestedBy: "Automatische Updates"));
                }
            }

            // Self update (the app does it itself when it hosts the agent; the service does it for an installed Windows).
            if (settings.CheckForUpdates && UpdateService.CanUpdate && (UpdateService.LastCheck is not { } checkedAt || DateTimeOffset.Now - checkedAt > UpdateService.CheckInterval))
            {
                _ = Task.Run(async () =>
                {
                    var result = await UpdateService.CheckAsync();
                    if (result is { IsUpdateAvailable: true, Manifest: { } manifest } && SettingsStore.ReadAgent().InstallUpdatesAutomatically &&
                        JobService.Current is null)
                    {
                        Log.Info($"Installing update {manifest.Version} automatically");
                        await UpdateService.DownloadAndStartInstallerAsync(manifest);
                        UpdateService.RaiseRestartRequested();
                    }
                });
            }
        }
        catch (Exception exception) when (exception is BusinessException or IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Agent timer: {exception.Message}");
        }
    }

    private static void Notify() => Changed?.Invoke(null, EventArgs.Empty);
}
