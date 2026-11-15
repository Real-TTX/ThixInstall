using Avalonia.Threading;
using ThixInstall.Core.Agent;
using ThixInstall.Core.Business;
using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Installers;
using ThixInstall.Core.Models;
using ThixInstall.Shared;

namespace ThixInstall;

/// <summary>
/// The app's view of the agent: asks for its status (every 1.5 s while a window is open, every 10 s otherwise) and tells
/// the pages what changed, on the UI thread. It also does the two things only the app in the user's session can do when
/// the agent is the Windows service: run scripts in the user's context, and restart itself after the service was updated.
/// </summary>
internal static class AgentMonitor
{
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private static bool _busy;
    private static bool _runningUserJob;
    private static long _jobsRevision = -1;
    private static long _catalogRevision = -1;
    private static DateTime _lastSlowPoll;
    private static readonly Dictionary<Guid, ActionState> KnownStates = [];

    /// <summary>The newest status (null until the first answer).</summary>
    public static AgentStatus? Status { get; private set; }

    /// <summary>The agent could not be reached (the service is not running): the message, else null.</summary>
    public static string? Problem { get; private set; }

    public static event EventHandler<AgentStatus>? StatusChanged;

    public static event EventHandler? JobsChanged;

    public static event EventHandler? CatalogChanged;

    /// <summary>A job ended (for a notification).</summary>
    public static event EventHandler<JobRecord>? JobFinished;

    /// <summary>The agent is newer than this app (the service was updated): the app restarts itself.</summary>
    public static event EventHandler? AgentUpdated;

    /// <summary>True while a window of the app is open: poll quickly.</summary>
    public static Func<bool> IsWindowVisible { get; set; } = () => false;

    public static void Start()
    {
        Timer.Tick += async (_, _) => await PollAsync();
        Timer.Start();
        if (!AgentClient.UsesService)
        {
            // The agent runs in this process: its events are enough to refresh at once.
            AgentHost.Changed += (_, _) => Dispatcher.UIThread.Post(() => _ = PollAsync());
        }

        _ = PollAsync();
    }

    public static void Stop() => Timer.Stop();

    /// <summary>Ask again now (after the user did something).</summary>
    public static Task RefreshNowAsync() => PollAsync(force: true);

    private static async Task PollAsync(bool force = false)
    {
        if (_busy)
        {
            return;
        }

        if (!force && !IsWindowVisible() && DateTime.UtcNow - _lastSlowPoll < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _busy = true;
        _lastSlowPoll = DateTime.UtcNow;
        try
        {
            var status = await AgentClient.GetStatusAsync();
            Problem = null;
            Status = status;
            StatusChanged?.Invoke(null, status);

            if (status.JobsRevision != _jobsRevision)
            {
                _jobsRevision = status.JobsRevision;
                await NoticeFinishedJobsAsync();
                JobsChanged?.Invoke(null, EventArgs.Empty);
            }

            if (status.CatalogRevision != _catalogRevision)
            {
                _catalogRevision = status.CatalogRevision;
                CatalogChanged?.Invoke(null, EventArgs.Empty);
            }

            if (AgentClient.UsesService)
            {
                if (status.AgentBuildNumber > AppInfo.BuildNumber && status.AgentVersion != AppInfo.Version)
                {
                    Log.Info($"The service runs {status.AgentVersion}, the app {AppInfo.Version}: restarting the app");
                    AgentUpdated?.Invoke(null, EventArgs.Empty);
                    return;
                }

                await RunUserJobsAsync();
            }
        }
        catch (BusinessException exception)
        {
            if (Problem != exception.Message)
            {
                Problem = exception.Message;
                StatusChanged?.Invoke(null, Status ?? new AgentStatus { AgentVersion = AppInfo.Version, AgentBuildNumber = AppInfo.BuildNumber });
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private static async Task NoticeFinishedJobsAsync()
    {
        var jobs = await AgentClient.GetJobsAsync();
        var first = KnownStates.Count == 0;
        foreach (var job in jobs.Take(50))
        {
            var known = KnownStates.TryGetValue(job.Id, out var state);
            KnownStates[job.Id] = job.State;
            if (!first && job.IsFinished && (!known || state != job.State))
            {
                JobFinished?.Invoke(null, job);
            }
        }
    }

    /// <summary>The service keeps scripts of the user's context for the app: run them here, in the user's session.</summary>
    private static async Task RunUserJobsAsync()
    {
        if (_runningUserJob)
        {
            return;
        }

        _runningUserJob = true;
        try
        {
            while (await AgentClient.ClaimUserJobAsync() is { Script: { } script } job)
            {
                Log.Info($"Running the user script \"{script.Name}\" for the service");
                var context = new JobContext(null, null, (_, _) => { }, CancellationToken.None);
                JobOutcome outcome;
                try
                {
                    outcome = await ScriptRunner.RunAsync(script, context);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BusinessException)
                {
                    outcome = new JobOutcome(false, null, exception.Message, exception.Message);
                }

                await AgentClient.CompleteUserJobAsync(job.Id, outcome.Succeeded, outcome.ExitCode, outcome.Message is null ? outcome.Output : $"{outcome.Output}\n{outcome.Message}".Trim());
            }
        }
        catch (BusinessException exception)
        {
            Log.Warning($"User scripts: {exception.Message}");
        }
        finally
        {
            _runningUserJob = false;
        }
    }

    /// <summary>"Installiert 133.0" / "Fehlgeschlagen" … for lists and notifications.</summary>
    public static string StateText(JobRecord job) => job.State switch
    {
        ActionState.Pending => job.Status ?? "Wartet",
        ActionState.Running => job.Status ?? "Läuft …",
        ActionState.Succeeded => job.InstalledVersion is { } version && job.Kind is ActionKind.Install or ActionKind.Update ? $"Erledigt · {version}" : job.Status ?? "Erledigt",
        ActionState.Failed => job.Status is { } reason ? $"Fehlgeschlagen · {reason}" : "Fehlgeschlagen",
        ActionState.Cancelled => "Abgebrochen",
        _ => "",
    };
}
