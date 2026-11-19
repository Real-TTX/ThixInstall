using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using ThixInstall.Core.Platform;
using ThixInstall.Shared;

namespace ThixInstall.Core.Installers;

/// <summary>
/// Installs, updates and removes apps on Windows: the installer from the cloud's cache (MSI, EXE, MSIX) and winget / Chocolatey (installed first when
/// it is missing) in the order of the plan (<see cref="InstallRoutes"/>: when one way fails the next follows), or the Microsoft Store (as the user).
/// Updating is installing the newer version over the old one (what every one of these installers does).
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsInstaller
{
    /// <summary>0, and "restart needed" / "restart started" of the Windows Installer.</summary>
    private static readonly int[] SuccessCodes = [3010, 1641];

    /// <summary>winget: "no newer version" and "already installed" are no failure of an update or installation.</summary>
    private static readonly int[] WingetNothingToDo = [unchecked((int)0x8A15002B), unchecked((int)0x8A150061)];

    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(45);

    public static async Task<JobOutcome> InstallAsync(PackageDto package, bool isUpdate, JobContext context)
    {
        var plan = package.Install ?? throw new Models.BusinessException($"{package.Name} gibt es nicht für Windows.");
        if (plan.Method == InstallMethod.MicrosoftStore)
        {
            return await StoreAsync(isUpdate ? "upgrade" : "install", plan.PackageId, context);
        }

        // the cached installer and winget / Chocolatey: the order of the plan, each one the fallback of the other
        var routes = InstallRoutes.ForWindows(plan, PackageManagers.Winget is not null);
        if (routes.Count == 0)
        {
            return JobOutcome.Fail(context, PackageManagers.Winget is null && InstallRoutes.WingetIdOf(plan) is not null
                ? "winget (App-Installer) ist auf diesem Gerät nicht installiert."
                : $"Für {package.Name} ist kein Installer bekannt.");
        }

        var verb = isUpdate ? "upgrade" : "install";
        return await InstallRoutes.RunAsync(routes, route => route switch
        {
            InstallRouteStep.Cache => InstallDownloadAsync(package, plan, isUpdate, context),
            InstallRouteStep.Winget => WingetAsync(verb, InstallRoutes.WingetIdOf(plan), context),
            _ => ChocolateyAsync(verb, InstallRoutes.ChocoIdOf(plan), context),
        }, context);
    }

    public static async Task<JobOutcome> UninstallAsync(PackageDto package, InstalledEntry? installed, JobContext context)
    {
        var plan = package.Install;
        if (plan?.Method == InstallMethod.MicrosoftStore && plan.PackageId is not null)
        {
            return await StoreAsync("uninstall", plan.PackageId, context);
        }

        if (installed?.QuietUninstallCommand is { Length: > 0 } quiet)
        {
            context.Step("Deinstalliere mit dem Befehl des Herstellers");
            return await RunCommandLineAsync(quiet, context);
        }

        var productCode = ProductCode(installed) ?? package.Detection?.ProductCode ?? plan?.ProductCode;
        if (productCode is not null)
        {
            context.Step($"Deinstalliere das MSI-Paket {productCode}");
            context.Report("Deinstalliere …");
            var result = await ProcessRunner.RunAsync("msiexec.exe", ["/x", productCode, "/qn", "/norestart"], InstallTimeout, context.Cancellation);
            return Outcome(result, context, SuccessCodes);
        }

        // Chocolatey removes its own apps (so it does not keep them as installed)
        if (plan?.Method == InstallMethod.Chocolatey && InstallRoutes.ChocoIdOf(plan) is { } ownChocoId && PackageManagers.Chocolatey is not null)
        {
            return await ChocolateyAsync("uninstall", ownChocoId, context);
        }

        if (plan is not null && InstallRoutes.WingetIdOf(plan) is { } wingetId && PackageManagers.Winget is not null)
        {
            return await WingetAsync("uninstall", wingetId, context);
        }

        if (plan is not null && InstallRoutes.ChocoIdOf(plan) is { } chocoId && PackageManagers.Chocolatey is not null)
        {
            return await ChocolateyAsync("uninstall", chocoId, context);
        }

        if (installed?.UninstallCommand is { Length: > 0 } command && SilentSwitchFor(command) is { } silent)
        {
            context.Step("Deinstalliere still mit dem Deinstallationsprogramm");
            return await RunCommandLineAsync(command + " " + silent, context);
        }

        return JobOutcome.Fail(context, installed is null
            ? $"{package.Name} ist nicht installiert."
            : $"Für {package.Name} ist keine stille Deinstallation bekannt – bitte unter „Installierte Apps“ entfernen.");
    }

    private static async Task<JobOutcome> InstallDownloadAsync(PackageDto package, InstallPlan plan, bool isUpdate, JobContext context)
    {
        string? file = null;
        try
        {
            file = await Downloads.FetchAsync(plan, context);
            context.Report($"Installiere {package.Name} …");
            // a Thix app: its setup with the install options (SETUP-08), the same command line for every Thix app
            var isThixApp = plan.Method == InstallMethod.ThixApp;
            var arguments = isThixApp ? string.Join(' ', ThixAppSetup.WindowsArguments(package.Installer, plan.Choices, isUpdate)) : plan.SilentArguments ?? "";
            ProcessResult result;
            switch (plan.InstallerKind)
            {
                case InstallerKind.Msi:
                    context.Step($"msiexec /i {Path.GetFileName(file)} {DefaultIfEmpty(arguments, "/qn /norestart")}");
                    result = await ProcessRunner.RunAsync("msiexec.exe", null, InstallTimeout, context.Cancellation,
                        rawArguments: $"/i {Elevation.Quote(file)} {DefaultIfEmpty(arguments, "/qn /norestart")}");
                    break;
                case InstallerKind.Msix:
                    context.Step(Elevation.IsLocalSystem ? "Stelle das MSIX-Paket für alle Benutzer bereit" : "Installiere das MSIX-Paket");
                    var command = Elevation.IsLocalSystem
                        ? $"Add-AppxProvisionedPackage -Online -PackagePath '{file.Replace("'", "''")}' -SkipLicense | Out-String"
                        : $"Add-AppxPackage -Path '{file.Replace("'", "''")}' -ForceApplicationShutdown";
                    result = await ProcessRunner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command],
                        InstallTimeout, context.Cancellation);
                    break;
                default:
                    context.Step($"{Path.GetFileName(file)} {arguments}".TrimEnd());
                    result = await ProcessRunner.RunAsync(file, null, InstallTimeout, context.Cancellation, workingDirectory: Path.GetDirectoryName(file), rawArguments: arguments);
                    break;
            }

            if (isThixApp && result.ExitCode == ThixAppSetup.RefusedExitCode)
            {
                return JobOutcome.Fail(context, ThixAppInstall.RefusedMessage(package, result.Output), result.Output, result.ExitCode);
            }

            return Outcome(result, context, SuccessCodes);
        }
        finally
        {
            Downloads.Remove(file);
        }
    }

    private static async Task<JobOutcome> WingetAsync(string verb, string? packageId, JobContext context)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return JobOutcome.Fail(context, "Für dieses Paket ist keine winget-Id hinterlegt.");
        }

        var winget = PackageManagers.Winget;
        if (winget is null)
        {
            return JobOutcome.Fail(context, "winget (App-Installer) ist auf diesem Gerät nicht installiert.");
        }

        var arguments = new List<string> { verb, "--id", packageId, "--exact", "--silent", "--disable-interactivity", "--accept-source-agreements" };
        if (verb != "uninstall")
        {
            arguments.Add("--accept-package-agreements");
        }

        context.Step($"winget {string.Join(' ', arguments)}");
        context.Report(verb switch { "uninstall" => "Deinstalliere mit winget …", "upgrade" => "Aktualisiere mit winget …", _ => "Installiere mit winget …" });
        var result = await ProcessRunner.RunAsync(winget, arguments, InstallTimeout, context.Cancellation);
        return Outcome(result, context, [.. SuccessCodes, .. WingetNothingToDo]);
    }

    private static async Task<JobOutcome> ChocolateyAsync(string verb, string? packageId, JobContext context)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return JobOutcome.Fail(context, "Für dieses Paket ist keine Chocolatey-Id hinterlegt.");
        }

        var choco = PackageManagers.Chocolatey;
        if (choco is null && verb != "uninstall")
        {
            choco = await InstallChocolateyAsync(context);
        }

        if (choco is null)
        {
            return JobOutcome.Fail(context, Elevation.IsElevated
                ? "Chocolatey konnte nicht installiert werden (Ausgabe oben)."
                : "Chocolatey ist auf diesem Gerät nicht installiert – ThixInstall kann es nur mit Administratorrechten nachinstallieren (installiert läuft ThixInstall als Dienst und kann das).");
        }

        string[] arguments = [verb, packageId, "-y", "--no-progress"];
        context.Step($"choco {string.Join(' ', arguments)}");
        context.Report(verb == "uninstall" ? "Deinstalliere mit Chocolatey …" : "Installiere mit Chocolatey …");
        var result = await ProcessRunner.RunAsync(choco, arguments, InstallTimeout, context.Cancellation);
        return Outcome(result, context, SuccessCodes);
    }

    /// <summary>
    /// Chocolatey is missing: install it the way chocolatey.org documents (its install script, run once with administrator rights - the
    /// service is LocalSystem). Returns choco.exe, or null when that failed or ThixInstall has no administrator rights.
    /// </summary>
    private static async Task<string?> InstallChocolateyAsync(JobContext context)
    {
        if (!Elevation.IsElevated)
        {
            return null;
        }

        context.Step("Chocolatey fehlt: installiere es (community.chocolatey.org/install.ps1)");
        context.Report("Installiere Chocolatey …");
        const string command = "Set-ExecutionPolicy Bypass -Scope Process -Force; " +
                               "[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072; " +
                               "iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))";
        var result = await ProcessRunner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command],
            TimeSpan.FromMinutes(10), context.Cancellation);
        var output = result.Output.Trim();
        context.Step($"Installation von Chocolatey beendet (Code {result.ExitCode?.ToString() ?? "–"})" +
                     (output.Length == 0 ? "" : ":" + Environment.NewLine + (output.Length > 2000 ? "…" + output[^2000..] : output)));
        return PackageManagers.Chocolatey;
    }

    /// <summary>
    /// An app of the Microsoft Store through winget's "msstore" source. Runs as the signed-in user: the service hands these jobs to the
    /// app (<see cref="Models.JobRecord.RunsAsUser"/>); as LocalSystem winget cannot use the store.
    /// </summary>
    private static async Task<JobOutcome> StoreAsync(string verb, string? storeId, JobContext context)
    {
        if (string.IsNullOrWhiteSpace(storeId))
        {
            return JobOutcome.Fail(context, "Für diese App ist keine Store-Id hinterlegt.");
        }

        if (Elevation.IsLocalSystem)
        {
            return JobOutcome.Fail(context, "Apps aus dem Microsoft Store installiert ThixInstall beim angemeldeten Benutzer – dafür muss die ThixInstall-App laufen.");
        }

        var winget = PackageManagers.Winget;
        if (winget is null)
        {
            return JobOutcome.Fail(context, "winget (App-Installer) ist auf diesem Gerät nicht installiert.");
        }

        var arguments = new List<string> { verb, "--id", storeId, "--source", "msstore", "--exact", "--silent", "--disable-interactivity", "--accept-source-agreements" };
        if (verb != "uninstall")
        {
            arguments.Add("--accept-package-agreements");
        }

        context.Step($"winget {string.Join(' ', arguments)}");
        context.Report(verb switch { "uninstall" => "Deinstalliere aus dem Microsoft Store …", "upgrade" => "Aktualisiere aus dem Microsoft Store …", _ => "Installiere aus dem Microsoft Store …" });
        var result = await ProcessRunner.RunAsync(winget, arguments, InstallTimeout, context.Cancellation);
        return Outcome(result, context, [.. SuccessCodes, .. WingetNothingToDo]);
    }

    /// <summary>A command line of an uninstall entry ("C:\…\uninst.exe" /S) through cmd, which understands its quoting.</summary>
    private static async Task<JobOutcome> RunCommandLineAsync(string commandLine, JobContext context)
    {
        context.Report("Deinstalliere …");
        var result = await ProcessRunner.RunAsync("cmd.exe", null, InstallTimeout, context.Cancellation, rawArguments: $"/d /s /c \"{commandLine}\"");
        return Outcome(result, context, SuccessCodes);
    }

    private static JobOutcome Outcome(ProcessResult result, JobContext context, int[] successCodes)
    {
        if (result.Cancelled)
        {
            return new JobOutcome(false, null, context.Combine(result.Output), "Abgebrochen.", Cancelled: true);
        }

        if (result.Succeeded(successCodes))
        {
            var message = result.ExitCode is 3010 or 1641 ? "Erfolgreich – ein Neustart wird benötigt." : null;
            return new JobOutcome(true, result.ExitCode, context.Combine(result.Output), message);
        }

        var reason = result.TimedOut ? "Zeitüberschreitung." : result.ExitCode is { } code ? $"Fehlercode {code} (0x{code:X8})." : "Das Programm konnte nicht gestartet werden.";
        return new JobOutcome(false, result.ExitCode, context.Combine(result.Output), reason);
    }

    private static string? ProductCode(InstalledEntry? installed)
    {
        if (installed is null)
        {
            return null;
        }

        if (ProductCodePattern().IsMatch(installed.Key))
        {
            return installed.Key;
        }

        var match = installed.UninstallCommand is null ? null : MsiInCommand().Match(installed.UninstallCommand);
        return match is { Success: true } ? match.Groups[1].Value : null;
    }

    /// <summary>The silent switch of the common setup builders, recognised by their uninstaller's name.</summary>
    private static string? SilentSwitchFor(string command)
    {
        var lower = command.ToLowerInvariant();
        if (lower.Contains("unins000") || lower.Contains("unins001"))
        {
            return "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
        }

        return lower.Contains("uninstall.exe") || lower.Contains("uninst.exe") ? "/S" : null;
    }

    private static string DefaultIfEmpty(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    private static partial Regex ProductCodePattern();

    [GeneratedRegex(@"msiexec(?:\.exe)?\s+/[IX]\s*(\{[0-9A-Fa-f-]{36}\})", RegexOptions.IgnoreCase)]
    private static partial Regex MsiInCommand();
}
