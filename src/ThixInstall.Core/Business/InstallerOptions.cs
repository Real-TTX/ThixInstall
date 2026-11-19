using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Models;
using ThixInstall.Shared;

namespace ThixInstall.Core.Business;

/// <summary>
/// The install options of ThixInstall itself (SETUP-08 of MatDesign) – one contract for every Thix app:
/// <list type="bullet">
/// <item><see cref="Config"/>: what the setup offers, from <c>installer.json</c> (embedded; CI puts the same file into the update manifest as
/// <c>"installer"</c>, where ThixLauncher reads it).</item>
/// <item><see cref="Parse"/>: the command line of SETUP-05 – <c>--quiet</c> or <c>--install</c> (install), <c>--update</c>, <c>--configure</c>
/// (only apply), <c>--uninstall</c>, <c>--start</c>, and the options <c>--desktop-shortcut=yes|no</c>, <c>--autostart=yes|no</c>,
/// <c>--automatic-updates=yes|no</c>, <c>--file-types=none|default</c> (ThixInstall opens no file types). The older switches of the setup
/// (<c>--no-autostart</c>, <c>--desktop</c>, <c>--no-auto-update</c>) still work with an install. A wrong value fails at once (exit code 2).</item>
/// <item><see cref="Current"/> and <see cref="Apply"/>: what the installed ThixInstall has, and making it so (Windows: the desktop shortcut
/// for all users, the autostart in HKLM; macOS: the LaunchAgent; both: automatic updates in the agent's settings).</item>
/// </list>
/// </summary>
public static class InstallerOptions
{
    public const string QuietArgument = ThixAppSetup.QuietArgument;
    public const string InstallArgument = "--install";
    public const string UpdateArgument = UpdateService.WindowsUpdateArgument;
    public const string ConfigureArgument = ThixAppSetup.ConfigureArgument;
    public const string UninstallArgument = "--uninstall";
    public const string StartArgument = ThixAppSetup.StartArgument;

    /// <summary>The exit code of a command line that was refused (nothing was done).</summary>
    public const int RefusedExitCode = ThixAppSetup.RefusedExitCode;

    private static InstallerConfig? _config;

    /// <summary>What the setup of ThixInstall offers (installer.json).</summary>
    public static InstallerConfig Config => _config ??= Load();

    /// <summary>The choices of a first install that is told nothing: the defaults of <see cref="Config"/>.</summary>
    public static InstallChoices Defaults => new(
        Config.DesktopShortcut?.Default ?? false,
        Config.Autostart?.Default ?? false,
        Config.AutomaticUpdates?.Default ?? false,
        Config.FileTypes.Where(type => type.Default).Select(type => type.Extension).ToList());

    /// <summary>Another configuration (tests); null = installer.json again.</summary>
    public static void UseConfigForTests(InstallerConfig? config) => _config = config;

    /// <summary>
    /// Reads the setup part of a command line. Throws <see cref="BusinessException"/> for a value it does not know – a quiet install must not
    /// guess (SETUP-05). A bare "--autostart" without an action is the start at sign-in, not an option.
    /// </summary>
    public static SetupRequest Parse(IReadOnlyList<string> args)
    {
        var action = args.Contains(UninstallArgument) ? SetupAction.Uninstall
            : args.Contains(ConfigureArgument) ? SetupAction.Configure
            : args.Contains(UpdateArgument) ? SetupAction.Update
            : args.Contains(InstallArgument) || args.Contains(QuietArgument) ? SetupAction.Install
            : SetupAction.None;
        var choices = new InstallChoices();
        foreach (var arg in args)
        {
            var separator = arg.IndexOf('=', StringComparison.Ordinal);
            if (!arg.StartsWith("--", StringComparison.Ordinal) || separator < 0)
            {
                continue;
            }

            var (name, value) = (arg[..separator], arg[(separator + 1)..].Trim());
            choices = name switch
            {
                ThixAppSetup.DesktopShortcutOption => choices with { DesktopShortcut = Switch(name, value, Config.DesktopShortcut) },
                ThixAppSetup.AutostartOption => choices with { Autostart = Switch(name, value, Config.Autostart) },
                ThixAppSetup.AutomaticUpdatesOption => choices with { AutomaticUpdates = Switch(name, value, Config.AutomaticUpdates) },
                ThixAppSetup.FileTypesOption => choices with { FileTypes = FileTypes(value) },
                _ => throw new BusinessException($"Unbekannte Option „{name}“. Möglich: {ThixAppSetup.DesktopShortcutOption}, {ThixAppSetup.AutostartOption}, " +
                                                 $"{ThixAppSetup.AutomaticUpdatesOption}, {ThixAppSetup.FileTypesOption}."),
            };
        }

        if (action is SetupAction.None or SetupAction.Uninstall && !choices.SameAs(new InstallChoices()))
        {
            throw new BusinessException($"Optionen gelten nur mit {QuietArgument}, {InstallArgument}, {UpdateArgument} oder {ConfigureArgument}.");
        }

        if (action == SetupAction.Install)
        {
            // the switches of the older setup (and of the assistant until SETUP-08): an option given with "=" wins
            choices = choices.Over(new InstallChoices(
                args.Contains("--desktop") ? true : null,
                args.Contains("--no-autostart") ? false : args.Contains(ThixAppSetup.AutostartOption) ? true : null,
                args.Contains("--no-auto-update") ? false : null));
        }

        return new SetupRequest(action, choices, args.Contains(QuietArgument), args.Contains(StartArgument));
    }

    /// <summary>The options of SETUP-08 for these choices (the setup assistant passes them to the elevated setup).</summary>
    public static IReadOnlyList<string> ToArguments(InstallChoices choices) => ThixAppSetup.Options(choices);

    /// <summary>What the installed ThixInstall has now (an update keeps it). Reads the machine-wide places on an installed Windows.</summary>
    public static InstallChoices Current() => new(
        OperatingSystem.IsWindows() && InstallService.HasDesktopShortcut,
        AutostartService.IsEnabled(),
        SettingsStore.ReadAgent().InstallUpdatesAutomatically,
        []);

    /// <summary>
    /// Makes the choices so for the program <paramref name="executable"/> (the installed one). Only offered options are touched; a value of
    /// null stays as it is. Each step is reported to <paramref name="log"/>.
    /// </summary>
    public static void Apply(InstallChoices choices, string executable, Action<string> log)
    {
        if (Config.DesktopShortcut is not null && choices.DesktopShortcut is { } shortcut && OperatingSystem.IsWindows())
        {
            InstallService.SetDesktopShortcut(shortcut);
            log(shortcut ? "Desktop-Verknüpfung angelegt" : "Keine Desktop-Verknüpfung");
        }

        if (Config.Autostart is not null && choices.Autostart is { } autostart)
        {
            if (autostart)
            {
                AutostartService.WriteEntry(executable);
                log(OperatingSystem.IsMacOS() ? "Startet bei der Anmeldung" : "Autostart bei der Anmeldung eingerichtet");
            }
            else
            {
                AutostartService.RemoveEntry();
                log("Kein Autostart");
            }
        }

        if (Config.AutomaticUpdates is not null && choices.AutomaticUpdates is { } updates)
        {
            SettingsStore.UpdateAgent(settings => settings with { InstallUpdatesAutomatically = updates });
            log(updates ? "Updates werden automatisch installiert" : "Updates werden nur angeboten");
        }
    }

    private static bool Switch(string name, string value, InstallerSwitch? offered)
    {
        if (offered is null)
        {
            throw new BusinessException($"{AppInfo.ProductName} bietet „{name}“ nicht an.");
        }

        return value.ToLowerInvariant() switch
        {
            "yes" or "true" or "1" or "ja" => true,
            "no" or "false" or "0" or "nein" => false,
            _ => throw new BusinessException($"{name} erwartet yes oder no, nicht „{value}“."),
        };
    }

    private static IReadOnlyList<string> FileTypes(string value)
    {
        if (value.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return Defaults.FileTypes!;
        }

        if (value.Length == 0 || value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var types = new List<string>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var extension = part.StartsWith('.') ? part : "." + part;
            var offered = Config.FileTypes.FirstOrDefault(type => type.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase))
                          ?? throw new BusinessException(Config.FileTypes.Count == 0
                              ? $"{AppInfo.ProductName} verknüpft keine Dateitypen."
                              : $"{AppInfo.ProductName} verknüpft „{extension}“ nicht. Möglich: {string.Join(", ", Config.FileTypes.Select(type => type.Extension))}.");
            if (!types.Contains(offered.Extension))
            {
                types.Add(offered.Extension);
            }
        }

        return types;
    }

    private static InstallerConfig Load()
    {
        using var stream = typeof(InstallerOptions).Assembly.GetManifestResourceStream("installer.json")
                           ?? throw new InvalidOperationException("installer.json is not embedded");
        return JsonFile.Deserialize<InstallerConfig>(stream) ?? new InstallerConfig();
    }
}
