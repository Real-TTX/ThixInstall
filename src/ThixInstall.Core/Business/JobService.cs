using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Installers;
using ThixInstall.Core.Models;
using ThixInstall.Shared;

namespace ThixInstall.Core.Business;

/// <summary>
/// The jobs of the agent: one queue, worked off one after the other (installers do not like to run side by side), a
/// history of the last <see cref="HistoryLength"/> jobs in jobs.json. A job of the cloud is started and finished there; a job
/// of the device itself is reported afterwards while "Sync" is on.
///
/// Scripts in the user's context: the Windows service cannot run them in the user's session, so it keeps them for the
/// app (<see cref="ClaimUserJob"/>, <see cref="CompleteUserJob"/>). Everywhere else the agent runs in the user's process
/// and does them itself.
/// </summary>
public static class JobService
{
    public const int HistoryLength = 200;

    private static readonly object Gate = new();
    private static readonly List<JobRecord> Queue = [];
    private static readonly List<JobRecord> WaitingForUser = [];
    private static readonly SemaphoreSlim Signal = new(0);
    private static List<JobRecord>? _history;
    private static JobRecord? _current;
    private static CancellationTokenSource? _currentCancel;
    private static CancellationTokenSource? _stop;
    private static Task? _worker;
    private static long _revision;

    /// <summary>Raised after every change of a job (any thread).</summary>
    public static event EventHandler<JobRecord>? Changed;

    /// <summary>Raised when a job has ended (any thread).</summary>
    public static event EventHandler<JobRecord>? Finished;

    public static long Revision => Interlocked.Read(ref _revision);

    public static JobRecord? Current
    {
        get
        {
            lock (Gate)
            {
                return _current;
            }
        }
    }

    public static int QueuedCount
    {
        get
        {
            lock (Gate)
            {
                return Queue.Count + WaitingForUser.Count;
            }
        }
    }

    /// <summary>The running job, the queued ones and the history, newest first.</summary>
    public static IReadOnlyList<JobRecord> All()
    {
        lock (Gate)
        {
            var list = new List<JobRecord>();
            list.AddRange(WaitingForUser.AsEnumerable().Reverse());
            list.AddRange(Queue.AsEnumerable().Reverse());
            if (_current is not null)
            {
                list.Add(_current);
            }

            list.AddRange(History);
            return list;
        }
    }

    /// <summary>The packages with a queued or running job (the catalog shows them as busy).</summary>
    public static IReadOnlySet<long> BusyPackages()
    {
        lock (Gate)
        {
            return Queue.Concat(WaitingForUser).Append(_current)
                .Where(job => job?.Package is not null)
                .Select(job => job!.Package!.Id)
                .ToHashSet();
        }
    }

    public static void Start()
    {
        lock (Gate)
        {
            if (_worker is not null)
            {
                return;
            }

            _ = History;
            _stop = new CancellationTokenSource();
            var stop = _stop.Token;
            _worker = Task.Run(() => WorkAsync(stop));
        }
    }

    public static void Stop()
    {
        Task? worker;
        lock (Gate)
        {
            _stop?.Cancel();
            _currentCancel?.Cancel();
            worker = _worker;
            _worker = null;
        }

        Signal.Release();
        try
        {
            worker?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
    }

    /// <summary>Queues a job (the caller checked the permission); returns it as queued.</summary>
    public static JobRecord Enqueue(JobRecord job)
    {
        lock (Gate)
        {
            if (job.CloudActionId is { } actionId && IsKnownAction(actionId))
            {
                return job;
            }

            Queue.Add(job with { State = ActionState.Pending, Status = "Wartet …" });
        }

        Signal.Release();
        Notify(job);
        return job;
    }

    /// <summary>True when an action of the cloud is already queued, running or done here.</summary>
    internal static bool IsKnownAction(long actionId)
    {
        lock (Gate)
        {
            return Queue.Concat(WaitingForUser).Append(_current).Concat(History).Any(job => job?.CloudActionId == actionId);
        }
    }

    /// <summary>Cancels a queued job, or stops the running one.</summary>
    public static bool Cancel(Guid id)
    {
        JobRecord? removed = null;
        lock (Gate)
        {
            if (_current?.Id == id)
            {
                _currentCancel?.Cancel();
                return true;
            }

            removed = Queue.FirstOrDefault(job => job.Id == id) ?? WaitingForUser.FirstOrDefault(job => job.Id == id);
            if (removed is not null)
            {
                Queue.Remove(removed);
                WaitingForUser.Remove(removed);
            }
        }

        if (removed is null)
        {
            return false;
        }

        _ = CompleteAsync(removed with { State = ActionState.Cancelled, Status = null, FinishedAt = DateTimeOffset.Now, Output = "Abgebrochen, bevor er begann." }, null);
        return true;
    }

    /// <summary>The app in the user's session takes the next script that has to run as the user (Windows service).</summary>
    public static JobRecord? ClaimUserJob()
    {
        JobRecord? job;
        lock (Gate)
        {
            job = WaitingForUser.FirstOrDefault(waiting => waiting.State == ActionState.Pending);
            if (job is null)
            {
                return null;
            }

            WaitingForUser.Remove(job);
            job = job with { State = ActionState.Running, StartedAt = DateTimeOffset.Now, Status = "Läuft beim angemeldeten Benutzer …" };
            WaitingForUser.Add(job);
        }

        Notify(job);

        // the app in the user's session downloads from the cloud of the job (a Thix app from the cache; downloads are public). Only its copy
        // carries the address: the job kept here is still reported to the connected cloud.
        var connection = CloudService.Connection;
        return job with { CloudUrl = job.CloudUrl ?? (connection.IsConnected ? connection.ServerUrl : null) };
    }

    /// <summary>The app reports how a claimed user script ended.</summary>
    public static void CompleteUserJob(Guid id, bool succeeded, int? exitCode, string? output)
    {
        JobRecord? job;
        lock (Gate)
        {
            job = WaitingForUser.FirstOrDefault(waiting => waiting.Id == id);
            if (job is null)
            {
                return;
            }

            WaitingForUser.Remove(job);
        }

        // a Store app the user installed: the service reads the inventory again for the version (and the cloud's report)
        string? installedVersion = null;
        if (succeeded && job.Package is { } package && job.Kind is ActionKind.Install or ActionKind.Update)
        {
            InventoryService.Refresh();
            installedVersion = InventoryService.FindInstalled(package)?.Version;
        }

        _ = CompleteAsync(job with
        {
            State = succeeded ? ActionState.Succeeded : ActionState.Failed,
            ExitCode = exitCode,
            Output = Trim(output),
            InstalledVersion = installedVersion,
            FinishedAt = DateTimeOffset.Now,
            Status = null,
        }, job.Package);
    }

    /// <summary>
    /// The app runs a job it claimed (<see cref="ClaimUserJob"/>) in the user's session: a user script or a Store app. Returns how it
    /// ended; the app hands that back with <see cref="CompleteUserJob"/>.
    /// </summary>
    internal static async Task<JobOutcome> RunClaimedAsync(JobRecord job, CancellationToken cancellationToken)
    {
        if (!job.RunsAsUser)
        {
            throw new BusinessException("Dieser Auftrag läuft nicht beim Benutzer.");
        }

        var context = new JobContext(job.CloudUrl, null, (_, _) => { }, cancellationToken);
        try
        {
            return await ExecuteAsync(job, context);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BusinessException)
        {
            return new JobOutcome(false, null, exception.Message, exception.Message);
        }
    }

    /// <summary>Runs one job right here and returns how it ended (the bundle runner and user scripts of the app).</summary>
    public static async Task<JobRecord> RunNowAsync(JobRecord job, Action<JobRecord>? progress, CancellationToken cancellationToken)
    {
        var running = job with { State = ActionState.Running, StartedAt = DateTimeOffset.Now };
        progress?.Invoke(running);
        var context = CreateContext(running, update => progress?.Invoke(update), cancellationToken);
        var outcome = await ExecuteAsync(running, context);
        return Finish(running, outcome);
    }

    private static List<JobRecord> History
    {
        get
        {
            // called under the lock or before the worker starts
            return _history ??= (JsonFile.Read<List<JobRecord>>(AppPaths.JobsFile) ?? [])
                .Select(job => job.IsFinished ? job : job with { State = ActionState.Cancelled, Status = null, Output = "Beim Beenden von ThixInstall unterbrochen." })
                .ToList();
        }
    }

    private static async Task WorkAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            await Signal.WaitAsync(stop).ContinueWith(_ => { }, TaskScheduler.Default);
            while (!stop.IsCancellationRequested && TakeNext() is { } job)
            {
                await RunQueuedAsync(job, stop);
            }
        }
    }

    private static JobRecord? TakeNext()
    {
        lock (Gate)
        {
            if (Queue.Count == 0)
            {
                return null;
            }

            var job = Queue[0];
            Queue.RemoveAt(0);
            return job;
        }
    }

    private static async Task RunQueuedAsync(JobRecord job, CancellationToken stop)
    {
        // A script for the user, and the agent is the Windows service: the app in the user's session runs it.
        if (job.RunsAsUser && AppPaths.IsMachineAgent)
        {
            var waiting = job with { Status = "Wartet auf den angemeldeten Benutzer (ThixInstall-App)" };
            lock (Gate)
            {
                WaitingForUser.Add(waiting);
            }

            await StartInCloudAsync(waiting);
            Notify(waiting);
            return;
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var running = job with { State = ActionState.Running, StartedAt = DateTimeOffset.Now, Status = "Startet …" };
        lock (Gate)
        {
            _current = running;
            _currentCancel = cancel;
        }

        Notify(running);
        if (!await StartInCloudAsync(running))
        {
            lock (Gate)
            {
                _current = null;
                _currentCancel = null;
            }

            await CompleteAsync(running with { State = ActionState.Cancelled, FinishedAt = DateTimeOffset.Now, Status = null, Output = "In der Cloud abgebrochen." }, null, reportToCloud: false);
            return;
        }

        JobRecord finished;
        try
        {
            var context = CreateContext(running, update =>
            {
                lock (Gate)
                {
                    if (_current?.Id == update.Id)
                    {
                        _current = update;
                    }
                }

                Notify(update);
            }, cancel.Token);
            var outcome = await ExecuteAsync(running, context);
            finished = Finish(running, outcome);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            Log.Error($"Job {running.Title} failed", exception);
            finished = running with
            {
                State = cancel.IsCancellationRequested ? ActionState.Cancelled : ActionState.Failed,
                FinishedAt = DateTimeOffset.Now,
                Status = null,
                Output = exception is BusinessException or CloudException ? exception.Message : $"Unerwarteter Fehler: {exception.Message}",
            };
        }

        lock (Gate)
        {
            _current = null;
            _currentCancel = null;
        }

        await CompleteAsync(finished, finished.Package);
    }

    private static JobContext CreateContext(JobRecord job, Action<JobRecord> update, CancellationToken cancellationToken)
    {
        var connection = CloudService.Connection;
        var cloudUrl = job.CloudUrl ?? (connection.IsConnected ? connection.ServerUrl : null);
        var token = job.CloudUrl is null && connection.IsConnected ? CloudService.Token : null;
        var current = job;
        return new JobContext(cloudUrl, token, (status, progress) =>
        {
            current = current with { Status = status, Progress = progress };
            update(current);
        }, cancellationToken);
    }

    private static async Task<JobOutcome> ExecuteAsync(JobRecord job, JobContext context)
    {
        switch (job.Kind)
        {
            case ActionKind.Install:
            case ActionKind.Update:
            {
                var package = job.Package ?? throw new BusinessException("Zu diesem Auftrag fehlt die App.");
                return OperatingSystem.IsWindows()
                    ? await WindowsInstaller.InstallAsync(package, job.Kind == ActionKind.Update, context)
                    : await MacInstaller.InstallAsync(package, job.Kind == ActionKind.Update, context);
            }

            case ActionKind.Uninstall:
            {
                var package = job.Package ?? throw new BusinessException("Zu diesem Auftrag fehlt die App.");
                await Task.Run(InventoryService.Refresh, context.Cancellation);
                var installed = InventoryService.FindInstalled(package);
                return OperatingSystem.IsWindows()
                    ? await WindowsInstaller.UninstallAsync(package, installed, context)
                    : await MacInstaller.UninstallAsync(package, installed, context);
            }

            case ActionKind.RunScript:
            {
                var script = job.Script ?? throw new BusinessException("Zu diesem Auftrag fehlt das Skript.");
                AccessPolicy.EnsureScriptsAllowed();
                return await ScriptRunner.RunAsync(script, context);
            }

            case ActionKind.UpdateAll:
            {
                await Task.Run(InventoryService.Refresh, context.Cancellation);
                var updates = CatalogService.AppsWithUpdates();
                foreach (var package in updates)
                {
                    Enqueue(NewJob(ActionKind.Update, job.Origin, package: package, requestedBy: job.RequestedBy));
                }

                context.Step(updates.Count == 0 ? "Alle Apps sind aktuell." : $"{updates.Count} Updates eingeplant: {string.Join(", ", updates.Select(package => package.Name))}");
                return new JobOutcome(true, 0, context.Combine(null));
            }

            case ActionKind.Refresh:
                await Task.Run(InventoryService.Refresh, context.Cancellation);
                await CloudService.ReportInventoryAsync(context.Cancellation);
                context.Step($"{InventoryService.Entries.Count} installierte Programme gemeldet");
                return new JobOutcome(true, 0, context.Combine(null));

            default:
                return JobOutcome.Fail(context, "Diese Art von Auftrag kennt ThixInstall nicht.");
        }
    }

    private static JobRecord Finish(JobRecord job, JobOutcome outcome)
    {
        string? installedVersion = null;
        if (outcome.Succeeded && job.Package is { } package && job.Kind is ActionKind.Install or ActionKind.Update)
        {
            InventoryService.Refresh();
            installedVersion = InventoryService.FindInstalled(package)?.Version;
        }

        var output = outcome.Message is null || outcome.Output.Contains(outcome.Message, StringComparison.Ordinal)
            ? outcome.Output
            : (outcome.Output + Environment.NewLine + outcome.Message).Trim();
        return job with
        {
            State = outcome.Cancelled ? ActionState.Cancelled : outcome.Succeeded ? ActionState.Succeeded : ActionState.Failed,
            ExitCode = outcome.ExitCode,
            Output = Trim(output),
            InstalledVersion = installedVersion,
            FinishedAt = DateTimeOffset.Now,
            Status = outcome.Message,
            Progress = null,
        };
    }

    /// <summary>Records the end of a job: history, the cloud, the events.</summary>
    private static async Task CompleteAsync(JobRecord job, PackageDto? package, bool reportToCloud = true)
    {
        lock (Gate)
        {
            History.Insert(0, job);
            if (History.Count > HistoryLength)
            {
                History.RemoveRange(HistoryLength, History.Count - HistoryLength);
            }

            try
            {
                JsonFile.Write(AppPaths.JobsFile, History);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"The job history could not be stored: {exception.Message}");
            }
        }

        Log.Info($"Job \"{job.Title}\" ended: {job.State}{(job.ExitCode is { } code ? $" ({code})" : "")}");
        Notify(job);
        Finished?.Invoke(null, job);
        if (package is not null && job.Kind is ActionKind.Uninstall)
        {
            InventoryService.Refresh();
        }

        if (reportToCloud)
        {
            await CloudService.ReportJobAsync(job);
        }
    }

    /// <summary>Tells the cloud that its action starts; false when it was cancelled there meanwhile.</summary>
    private static Task<bool> StartInCloudAsync(JobRecord job) =>
        job.CloudActionId is null ? Task.FromResult(true) : CloudService.StartActionAsync(job.CloudActionId.Value);

    private static void Notify(JobRecord job)
    {
        Interlocked.Increment(ref _revision);
        Changed?.Invoke(null, job);
    }

    internal static string? Trim(string? output) =>
        output is null || output.Length <= ApiContract.MaxOutputLength ? output : "…" + output[^(ApiContract.MaxOutputLength - 1)..];

    /// <summary>A new job for a package or a script with a German title.</summary>
    public static JobRecord NewJob(ActionKind kind, ActionOrigin origin, PackageDto? package = null, ScriptDto? script = null,
        long? cloudActionId = null, string? requestedBy = null, string? cloudUrl = null, string? title = null) => new()
    {
        Id = Guid.NewGuid(),
        Kind = kind,
        Origin = origin,
        Title = title ?? TitleFor(kind, package?.Name ?? script?.Name),
        Package = package,
        Script = script,
        CloudActionId = cloudActionId,
        RequestedBy = requestedBy,
        CloudUrl = cloudUrl,
        CreatedAt = DateTimeOffset.Now,
    };

    public static string TitleFor(ActionKind kind, string? name) => kind switch
    {
        ActionKind.Install => $"{name} installieren",
        ActionKind.Update => $"{name} aktualisieren",
        ActionKind.Uninstall => $"{name} deinstallieren",
        ActionKind.RunScript => $"„{name}“ ausführen",
        ActionKind.UpdateAll => "Alle Apps aktualisieren",
        ActionKind.Refresh => "Inventar melden",
        _ => name ?? "Auftrag",
    };

    /// <summary>Tests start with an empty queue and history.</summary>
    internal static void ResetForTests()
    {
        Stop();
        lock (Gate)
        {
            Queue.Clear();
            WaitingForUser.Clear();
            _history = null;
            _current = null;
        }
    }
}
