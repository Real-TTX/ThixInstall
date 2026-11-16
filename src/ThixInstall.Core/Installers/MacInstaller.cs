using ThixInstall.Core.Platform;
using ThixInstall.Shared;

namespace ThixInstall.Core.Installers;

/// <summary>
/// Installs, updates and removes apps on macOS. A cask whose download the cloud resolved is installed without Homebrew:
/// the .app of the disk image or zip is copied to /Applications, a .pkg goes through Apple's installer (which asks for an
/// administrator). Without a download, and for formulae, Homebrew does it; with both, the plan's <see cref="InstallPlan.Route"/> says which comes first
/// and the other is the fallback (<see cref="InstallRoutes"/>).
/// </summary>
internal static class MacInstaller
{
    private const string Applications = "/Applications";
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(45);

    public static async Task<JobOutcome> InstallAsync(PackageDto package, bool isUpdate, JobContext context)
    {
        var plan = package.Install ?? throw new Models.BusinessException($"{package.Name} gibt es nicht für macOS.");

        // the cached dmg/zip/pkg and Homebrew: the order of the plan, each one the fallback of the other
        var routes = InstallRoutes.ForMac(plan, PackageManagers.Brew is not null);
        if (routes.Count == 0)
        {
            return plan.Method is InstallMethod.Homebrew or InstallMethod.HomebrewCask
                ? await BrewAsync(isUpdate ? "upgrade" : "install", plan.PackageId, plan.Method == InstallMethod.HomebrewCask, context)
                : JobOutcome.Fail(context, $"Für {package.Name} ist kein Installer bekannt.");
        }

        return await InstallRoutes.RunAsync(routes, route => route == InstallRouteStep.Cache
            ? InstallDownloadAsync(package, plan, context)
            : BrewAsync(isUpdate ? "upgrade" : "install", plan.PackageId, plan.Method == InstallMethod.HomebrewCask, context), context);
    }

    public static async Task<JobOutcome> UninstallAsync(PackageDto package, InstalledEntry? installed, JobContext context)
    {
        if (installed is { Source: "brew" })
        {
            return await BrewAsync("uninstall", installed.Key, installed.IsCask, context);
        }

        var bundle = installed?.Source == "app" ? installed.Key : AppBundlePath(package.Install?.AppBundleName ?? package.Detection?.AppBundleName);
        if (bundle is null || !Directory.Exists(bundle))
        {
            return JobOutcome.Fail(context, $"{package.Name} ist nicht installiert.");
        }

        context.Report("Lege die App in den Papierkorb …");
        var trash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");
        var target = Path.Combine(trash, Path.GetFileName(bundle));
        if (Directory.Exists(target))
        {
            target = Path.Combine(trash, $"{Path.GetFileNameWithoutExtension(bundle)} {DateTime.Now:HH-mm-ss}.app");
        }

        try
        {
            Directory.CreateDirectory(trash);
            Directory.Move(bundle, target);
            context.Step($"{bundle} in den Papierkorb gelegt");
            return new JobOutcome(true, 0, context.Combine(null));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return JobOutcome.Fail(context, $"Die App konnte nicht entfernt werden: {exception.Message}");
        }
    }

    private static async Task<JobOutcome> InstallDownloadAsync(PackageDto package, InstallPlan plan, JobContext context)
    {
        string? file = null;
        string? mount = null;
        try
        {
            file = await Downloads.FetchAsync(plan, context);
            context.Report($"Installiere {package.Name} …");
            switch (plan.InstallerKind)
            {
                case InstallerKind.Pkg:
                    return await InstallPackageAsync(file, context);

                case InstallerKind.Zip:
                {
                    var unpacked = Path.Combine(Path.GetDirectoryName(file)!, "unpacked");
                    var result = await ProcessRunner.RunAsync("/usr/bin/ditto", ["-x", "-k", file, unpacked], InstallTimeout, context.Cancellation);
                    if (!result.Succeeded())
                    {
                        return JobOutcome.Fail(context, "Das Archiv konnte nicht entpackt werden.", result.Output, result.ExitCode);
                    }

                    return await CopyAppAsync(FindApp(unpacked, plan.AppBundleName), context);
                }

                default:
                {
                    mount = Path.Combine(Path.GetDirectoryName(file)!, "mount");
                    Directory.CreateDirectory(mount);
                    // "Y" answers the licence agreement some disk images show.
                    var attach = await ProcessRunner.RunAsync("/usr/bin/hdiutil", ["attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount, file],
                        TimeSpan.FromMinutes(5), context.Cancellation, standardInput: "Y\n");
                    if (!attach.Succeeded())
                    {
                        mount = null;
                        return JobOutcome.Fail(context, "Das Disk-Image konnte nicht geöffnet werden.", attach.Output, attach.ExitCode);
                    }

                    context.Step("Disk-Image geöffnet");
                    var app = FindApp(mount, plan.AppBundleName);
                    if (app is null && Directory.EnumerateFiles(mount, "*.pkg").FirstOrDefault() is { } pkg)
                    {
                        return await InstallPackageAsync(pkg, context);
                    }

                    return await CopyAppAsync(app, context);
                }
            }
        }
        finally
        {
            if (mount is not null)
            {
                await ProcessRunner.RunAsync("/usr/bin/hdiutil", ["detach", mount, "-force"], TimeSpan.FromMinutes(1), CancellationToken.None);
            }

            Downloads.Remove(file);
        }
    }

    /// <summary>Copies an app bundle to /Applications, replacing an older one (which comes back if the copy fails).</summary>
    private static async Task<JobOutcome> CopyAppAsync(string? app, JobContext context)
    {
        if (app is null)
        {
            return JobOutcome.Fail(context, "Im Download ist keine App enthalten.");
        }

        var target = Path.Combine(Applications, Path.GetFileName(app));
        var backup = target + ".thixinstall-old";
        try
        {
            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }

            if (Directory.Exists(target))
            {
                Directory.Move(target, backup);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return JobOutcome.Fail(context, $"Die vorhandene App kann nicht ersetzt werden: {exception.Message}");
        }

        context.Step($"Kopiere {Path.GetFileName(app)} nach {Applications}");
        var copy = await ProcessRunner.RunAsync("/usr/bin/ditto", [app, target], InstallTimeout, context.Cancellation);
        if (!copy.Succeeded())
        {
            if (Directory.Exists(backup))
            {
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }

                Directory.Move(backup, target);
            }

            return JobOutcome.Fail(context, "Die App konnte nicht nach „Programme“ kopiert werden.", copy.Output, copy.ExitCode);
        }

        await ProcessRunner.RunAsync("/usr/bin/xattr", ["-dr", "com.apple.quarantine", target], TimeSpan.FromMinutes(1), CancellationToken.None);
        if (Directory.Exists(backup))
        {
            Directory.Delete(backup, recursive: true);
        }

        return new JobOutcome(true, 0, context.Combine(copy.Output));
    }

    /// <summary>A .pkg needs root: as root directly, else through an administrator prompt of macOS.</summary>
    private static async Task<JobOutcome> InstallPackageAsync(string pkg, JobContext context)
    {
        context.Step($"installer -pkg {Path.GetFileName(pkg)} -target /");
        var result = Elevation.IsElevated
            ? await ProcessRunner.RunAsync("/usr/sbin/installer", ["-pkg", pkg, "-target", "/"], InstallTimeout, context.Cancellation)
            : await RunAsAdministratorAsync($"/usr/sbin/installer -pkg {ShellQuote(pkg)} -target /", context);
        return result.Succeeded()
            ? new JobOutcome(true, 0, context.Combine(result.Output))
            : JobOutcome.Fail(context, result.Cancelled ? "Abgebrochen." : "Die Installation des Pakets ist fehlgeschlagen.", result.Output, result.ExitCode);
    }

    private static async Task<JobOutcome> BrewAsync(string verb, string? name, bool cask, JobContext context)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return JobOutcome.Fail(context, "Für dieses Paket ist kein Homebrew-Name hinterlegt.");
        }

        var brew = PackageManagers.Brew;
        if (brew is null)
        {
            return JobOutcome.Fail(context, "Homebrew ist nicht installiert (https://brew.sh).");
        }

        var arguments = new List<string> { verb };
        if (cask)
        {
            arguments.Add("--cask");
        }

        arguments.Add(name);
        context.Step($"brew {string.Join(' ', arguments)}");
        context.Report(verb switch { "uninstall" => "Deinstalliere mit Homebrew …", "upgrade" => "Aktualisiere mit Homebrew …", _ => "Installiere mit Homebrew …" });
        var result = await ProcessRunner.RunAsync(brew, arguments, InstallTimeout, context.Cancellation,
            environment: new Dictionary<string, string> { ["HOMEBREW_NO_AUTO_UPDATE"] = "1", ["NONINTERACTIVE"] = "1" });
        if (result.Cancelled)
        {
            return new JobOutcome(false, null, context.Combine(result.Output), "Abgebrochen.", Cancelled: true);
        }

        return result.Succeeded()
            ? new JobOutcome(true, 0, context.Combine(result.Output))
            : JobOutcome.Fail(context, $"Homebrew meldet Fehler {result.ExitCode}.", result.Output, result.ExitCode);
    }

    /// <summary>Runs a shell command as root after macOS asked for an administrator ("do shell script … with administrator privileges").</summary>
    internal static Task<ProcessResult> RunAsAdministratorAsync(string shellCommand, JobContext context, TimeSpan? timeout = null)
    {
        var script = $"do shell script \"{shellCommand.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" with administrator privileges";
        return ProcessRunner.RunAsync("/usr/bin/osascript", ["-e", script], timeout ?? InstallTimeout, context.Cancellation);
    }

    internal static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string? FindApp(string folder, string? appBundleName)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(appBundleName))
        {
            var named = Path.Combine(folder, Path.GetFileName(appBundleName));
            if (Directory.Exists(named))
            {
                return named;
            }
        }

        return Directory.EnumerateDirectories(folder, "*.app").FirstOrDefault()
               ?? Directory.EnumerateDirectories(folder).SelectMany(sub => Directory.EnumerateDirectories(sub, "*.app")).FirstOrDefault();
    }

    private static string? AppBundlePath(string? appBundleName) =>
        string.IsNullOrWhiteSpace(appBundleName) ? null : Path.Combine(Applications, Path.GetFileName(appBundleName));
}
