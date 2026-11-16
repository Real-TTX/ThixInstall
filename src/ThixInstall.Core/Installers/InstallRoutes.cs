using ThixInstall.Shared;

namespace ThixInstall.Core.Installers;

/// <summary>One way to install an app.</summary>
internal enum InstallRouteStep
{
    /// <summary>The installer from the cloud's cache.</summary>
    Cache,
    Winget,
    Chocolatey,
    /// <summary>Homebrew (formula or cask), macOS.</summary>
    Homebrew,
}

/// <summary>
/// The order in which a device tries the ways of a plan: the cached installer and the package managers, the cache before or after them as
/// <see cref="InstallPlan.Route"/> says. When a way fails the next one follows, so both ways work and each is the other's fallback.
/// </summary>
internal static class InstallRoutes
{
    /// <summary>
    /// Windows: the package manager of the plan first, then the other one it knows (winget is left out when the device has none; Chocolatey is installed
    /// when it is needed).
    /// </summary>
    public static IReadOnlyList<InstallRouteStep> ForWindows(InstallPlan plan, bool hasWinget)
    {
        var managers = new List<InstallRouteStep>();
        if (WingetIdOf(plan) is not null && hasWinget)
        {
            managers.Add(InstallRouteStep.Winget);
        }

        if (ChocoIdOf(plan) is not null)
        {
            managers.Insert(plan.Method == InstallMethod.Chocolatey ? 0 : managers.Count, InstallRouteStep.Chocolatey);
        }

        return WithCache(plan, managers, InstallerKind.Exe, InstallerKind.Msi, InstallerKind.Msix);
    }

    /// <summary>macOS: Homebrew (when the plan has a formula or cask and the device has brew) and the cached dmg, zip or pkg.</summary>
    public static IReadOnlyList<InstallRouteStep> ForMac(InstallPlan plan, bool hasBrew)
    {
        var managers = new List<InstallRouteStep>();
        if (plan.Method is InstallMethod.Homebrew or InstallMethod.HomebrewCask && !string.IsNullOrWhiteSpace(plan.PackageId) && hasBrew)
        {
            managers.Add(InstallRouteStep.Homebrew);
        }

        return WithCache(plan, managers, InstallerKind.Dmg, InstallerKind.Zip, InstallerKind.Pkg);
    }

    public static string? WingetIdOf(InstallPlan plan) =>
        NullIfEmpty(plan.WingetId) ?? (plan.Method == InstallMethod.Winget ? NullIfEmpty(plan.PackageId) : null);

    public static string? ChocoIdOf(InstallPlan plan) =>
        NullIfEmpty(plan.ChocoId) ?? (plan.Method == InstallMethod.Chocolatey ? NullIfEmpty(plan.PackageId) : null);

    public static string Name(InstallRouteStep route) => route switch
    {
        InstallRouteStep.Cache => "Der Installer aus der Cloud",
        InstallRouteStep.Winget => "winget",
        InstallRouteStep.Chocolatey => "Chocolatey",
        _ => "Homebrew",
    };

    /// <summary>Tries the ways one after another until one succeeds (or the job is cancelled); the steps of the job name every change of way.</summary>
    public static async Task<JobOutcome> RunAsync(IReadOnlyList<InstallRouteStep> routes, Func<InstallRouteStep, Task<JobOutcome>> run, JobContext context)
    {
        JobOutcome? outcome = null;
        for (var index = 0; index < routes.Count; index++)
        {
            if (outcome is not null)
            {
                context.Step($"{Name(routes[index - 1])} hat nicht geklappt ({outcome.Message ?? "ohne Meldung"}) – versuche {Name(routes[index])}");
            }

            outcome = await TryAsync(() => run(routes[index]), context);
            if (outcome.Succeeded || outcome.Cancelled)
            {
                return outcome;
            }
        }

        return outcome ?? throw new InvalidOperationException("No way to install.");
    }

    /// <summary>A way that may throw (the download from the cloud) as an outcome, so the next way can follow.</summary>
    public static async Task<JobOutcome> TryAsync(Func<Task<JobOutcome>> route, JobContext context)
    {
        try
        {
            return await route();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or Models.BusinessException
                                              || exception is OperationCanceledException && !context.Cancellation.IsCancellationRequested)
        {
            return JobOutcome.Fail(context, exception is OperationCanceledException ? "Zeitüberschreitung beim Herunterladen." : exception.Message);
        }
    }

    private static IReadOnlyList<InstallRouteStep> WithCache(InstallPlan plan, List<InstallRouteStep> managers, params InstallerKind[] kinds)
    {
        if (plan.DownloadUrl is null || plan.InstallerKind is not { } kind || !kinds.Contains(kind))
        {
            return managers;
        }

        return plan.Route == InstallRoute.PackageManager ? [.. managers, InstallRouteStep.Cache] : [InstallRouteStep.Cache, .. managers];
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
