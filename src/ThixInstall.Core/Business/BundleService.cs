using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Models;
using ThixInstall.Core.Platform;
using ThixInstall.Shared;

namespace ThixInstall.Core.Business;

/// <summary>
/// The bundle runner: a ThixInstall that carries a <see cref="BundleTicket"/> installs what somebody picked on the start
/// page of the cloud, one item after the other, like Ninite: apps that are current are skipped, older ones updated, then
/// the scripts run. A bundle "with ThixInstall" then installs ThixInstall itself and connects it to the cloud with the
/// bundle's token (the device belongs to whoever made the bundle and gets its profile).
/// </summary>
public static class BundleService
{
    /// <summary>The argument that names a ticket: "--bundle &lt;cloud&gt; &lt;token&gt;".</summary>
    public const string BundleArgument = "--bundle";

    /// <summary>
    /// The ticket of this start: appended to the program (Windows), a .thixinstall file as argument or next to the app
    /// (macOS: the zip of the cloud has it beside ThixInstall.app), or "--bundle &lt;cloud&gt; &lt;token&gt;".
    /// </summary>
    public static BundleTicket? FindTicket(string[] args)
    {
        var index = Array.IndexOf(args, BundleArgument);
        if (index >= 0 && index + 2 < args.Length)
        {
            var ticket = new BundleTicket(args[index + 1], args[index + 2]);
            return BundleTicket.IsValid(ticket) ? ticket : null;
        }

        foreach (var argument in args.Where(argument => argument.EndsWith(BundleTicket.FileExtension, StringComparison.OrdinalIgnoreCase)))
        {
            if (ReadTicketFile(argument) is { } fromFile)
            {
                return fromFile;
            }
        }

        if (OperatingSystem.IsWindows() && Environment.ProcessPath is { } program && BundleTicket.ReadTrailer(program) is { } appended)
        {
            return appended;
        }

        if (OperatingSystem.IsMacOS() && MacAppBundle.Current is { } bundle && !bundle.StartsWith("/Applications/", StringComparison.Ordinal))
        {
            var folder = Path.GetDirectoryName(bundle);
            if (folder is not null)
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*" + BundleTicket.FileExtension))
                {
                    if (ReadTicketFile(file) is { } beside)
                    {
                        return beside;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The configuration of the bundle: the one packed into the program (normal case), else the cloud's (a ticket of an
    /// older cloud, or "--bundle url token").
    /// </summary>
    public static async Task<BundleDto> LoadAsync(BundleTicket ticket, CancellationToken cancellationToken = default)
    {
        if (ticket.Bundle is { } packed)
        {
            return packed.Os == AppInfo.Os
                ? packed
                : throw new BusinessException($"Dieses Paket ist für {(packed.Os == TargetOs.MacOS ? "macOS" : "Windows")} gedacht.");
        }

        try
        {
            using var client = CloudClient.Create(ticket.CloudUrl);
            var bundle = await client.GetBundleAsync(ticket.Token, AppInfo.Architecture, cancellationToken);
            if (bundle.Os != AppInfo.Os)
            {
                throw new BusinessException($"Dieses Paket ist für {(bundle.Os == TargetOs.MacOS ? "macOS" : "Windows")} gedacht.");
            }

            return bundle;
        }
        catch (CloudException exception)
        {
            throw new BusinessException(exception.StatusCode == System.Net.HttpStatusCode.NotFound
                ? "Dieses Paket gibt es in der Cloud nicht (mehr)."
                : exception.Message);
        }
        catch (HttpRequestException exception)
        {
            throw new BusinessException($"Die Cloud {ticket.CloudUrl} ist nicht erreichbar: {exception.Message}");
        }
    }

    /// <summary>The steps of a bundle, before anything ran (the window shows them as a list).</summary>
    public static IReadOnlyList<JobRecord> PlanSteps(BundleTicket ticket, BundleDto bundle)
    {
        var steps = new List<JobRecord>();
        foreach (var package in bundle.Packages)
        {
            steps.Add(JobService.NewJob(ActionKind.Install, ActionOrigin.Bundle, package: package, cloudUrl: ticket.CloudUrl));
        }

        foreach (var script in bundle.Scripts)
        {
            steps.Add(JobService.NewJob(ActionKind.RunScript, ActionOrigin.Bundle, script: script, cloudUrl: ticket.CloudUrl));
        }

        if (bundle.IncludesAgent && bundle.CanEnroll)
        {
            steps.Add(JobService.NewJob(ActionKind.Install, ActionOrigin.Bundle, cloudUrl: ticket.CloudUrl,
                title: bundle.ProfileName is null ? "ThixInstall installieren und verbinden" : $"ThixInstall installieren (Profil „{bundle.ProfileName}“)"));
        }

        return steps;
    }

    /// <summary>
    /// Runs the steps one by one; <paramref name="progress"/> gets every change of a step (index, step). An app that is
    /// installed and current is skipped, an older one updated. Returns the steps as they ended.
    /// </summary>
    public static async Task<IReadOnlyList<JobRecord>> RunAsync(BundleTicket ticket, BundleDto bundle, IReadOnlyList<JobRecord> steps,
        Action<int, JobRecord> progress, CancellationToken cancellationToken)
    {
        var results = steps.ToList();
        await Task.Run(InventoryService.Refresh, cancellationToken);
        for (var index = 0; index < results.Count; index++)
        {
            var step = results[index];
            if (cancellationToken.IsCancellationRequested)
            {
                results[index] = step with { State = ActionState.Cancelled, Status = "Abgebrochen" };
                progress(index, results[index]);
                continue;
            }

            var position = index;
            void Report(JobRecord update)
            {
                results[position] = update;
                progress(position, update);
            }

            try
            {
                if (step.Package is { } package)
                {
                    var installed = InventoryService.FindInstalled(package);
                    if (installed is not null && !AppVersion.IsNewer(package.LatestVersion, installed.Version))
                    {
                        Report(step with { State = ActionState.Succeeded, Status = $"Bereits installiert ({installed.Version ?? "aktuell"})", InstalledVersion = installed.Version });
                        continue;
                    }

                    if (installed is not null)
                    {
                        step = step with { Kind = ActionKind.Update, Title = JobService.TitleFor(ActionKind.Update, package.Name) };
                    }

                    Report(await JobService.RunNowAsync(step, Report, cancellationToken));
                }
                else if (step.Script is not null)
                {
                    Report(await JobService.RunNowAsync(step, Report, cancellationToken));
                }
                else
                {
                    Report(step with { State = ActionState.Running, Status = "Installiere ThixInstall …", StartedAt = DateTimeOffset.Now });
                    var message = await InstallAgentAsync(ticket, bundle, cancellationToken);
                    Report(step with { State = ActionState.Succeeded, Status = message, FinishedAt = DateTimeOffset.Now });
                }
            }
            catch (Exception exception) when (exception is BusinessException or IOException or UnauthorizedAccessException or HttpRequestException or CloudException)
            {
                Log.Error($"Bundle step {step.Title} failed", exception);
                Report(step with { State = ActionState.Failed, Status = exception.Message, Output = exception.Message, FinishedAt = DateTimeOffset.Now });
            }
        }

        return results;
    }

    /// <summary>
    /// Installs ThixInstall for good and connects it with the bundle's token, before the agent starts: Windows into the
    /// program folder with the service (this process runs elevated), macOS into /Applications with the autostart.
    /// </summary>
    private static async Task<string> InstallAgentAsync(BundleTicket ticket, BundleDto bundle, CancellationToken cancellationToken)
    {
        var program = Environment.ProcessPath ?? throw new BusinessException("Das Programm von ThixInstall wurde nicht gefunden.");
        if (OperatingSystem.IsWindows())
        {
            InstallService.Install(program, new InstallOptions(Autostart: true, DesktopShortcut: false, InstallUpdatesAutomatically: true), Log.Info, startService: false);
            AppPaths.IsMachineAgent = true;
            SettingsStore.Reset();
            var info = await CloudService.ConnectAsync(new ConnectRequest(ticket.CloudUrl, null, null, null, null), ticket.Token, startLoop: false, cancellationToken);
            InstallService.StartAgentService(Log.Info);
            return $"Installiert und verbunden mit {info.UserDisplayName}";
        }

        if (OperatingSystem.IsMacOS())
        {
            var installed = MacAppBundle.InstallIntoApplications();
            var info = await CloudService.ConnectAsync(new ConnectRequest(ticket.CloudUrl, null, null, null, null), ticket.Token, startLoop: false, cancellationToken);
            AutostartService.WriteEntry(installed);
            return $"Installiert und verbunden mit {info.UserDisplayName}";
        }

        throw new BusinessException("ThixInstall gibt es für dieses System nicht.");
    }

    /// <summary>After the bundle: start the installed app (macOS; on Windows the not elevated starter does it).</summary>
    public static void OpenInstalledApp()
    {
        if (OperatingSystem.IsMacOS())
        {
            ProcessRunner.ReadOutput("/usr/bin/open", "-a", "/Applications/ThixInstall.app");
        }
    }

    private static BundleTicket? ReadTicketFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length < 4 * 1024 * 1024 ? BundleTicket.FromJson(File.ReadAllText(path)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
