using System.Text.RegularExpressions;

namespace ThixInstall.Shared;

/// <summary>
/// What the setup of a Thix app offers (SETUP-08 of MatDesign, one contract for every Thix app): the app's <c>installer.json</c>, published
/// as the block <c>"installer"</c> of its update manifest. An option that is null is not offered. ThixInstall reads it for its own setup and,
/// for the Thix apps of the catalog, from their manifests (the cloud stores it with the app, the start page asks the same questions).
/// </summary>
public sealed record InstallerConfig
{
    /// <summary>A shortcut on the desktop (Windows).</summary>
    public InstallerSwitch? DesktopShortcut { get; init; }

    /// <summary>Start at sign-in (Windows: the Run key, macOS: a LaunchAgent).</summary>
    public InstallerSwitch? Autostart { get; init; }

    /// <summary>Install updates without asking.</summary>
    public InstallerSwitch? AutomaticUpdates { get; init; }

    /// <summary>The file types the app can open; each one may be associated with it.</summary>
    public IReadOnlyList<InstallerFileType> FileTypes { get; init; } = [];

    /// <summary>True when the app offers nothing to choose.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => DesktopShortcut is null && Autostart is null && AutomaticUpdates is null && FileTypes.Count == 0;
}

/// <summary>An option that is on or off; <see cref="Default"/> is what a quiet install does when it is not given.</summary>
public sealed record InstallerSwitch
{
    public bool Default { get; init; }
}

/// <summary>A file type the app opens: ".md", its name ("Markdown-Notiz") and whether it is associated by default.</summary>
public sealed record InstallerFileType
{
    public string Extension { get; init; } = "";

    public string Name { get; init; } = "";

    public bool Default { get; init; }
}

/// <summary>
/// The choices of one install: a value per option, null = not given (a first install takes the default of the <see cref="InstallerConfig"/>,
/// an installed app keeps what it has). <see cref="FileTypes"/> lists the extensions to associate.
/// </summary>
public sealed record InstallChoices(bool? DesktopShortcut = null, bool? Autostart = null, bool? AutomaticUpdates = null, IReadOnlyList<string>? FileTypes = null)
{
    /// <summary>These choices where given, <paramref name="fallback"/> where not.</summary>
    public InstallChoices Over(InstallChoices fallback) => new(
        DesktopShortcut ?? fallback.DesktopShortcut,
        Autostart ?? fallback.Autostart,
        AutomaticUpdates ?? fallback.AutomaticUpdates,
        FileTypes ?? fallback.FileTypes);

    /// <summary>Equal values, also for the list of file types (a record compares lists by reference).</summary>
    public bool SameAs(InstallChoices other) =>
        DesktopShortcut == other.DesktopShortcut && Autostart == other.Autostart && AutomaticUpdates == other.AutomaticUpdates &&
        (FileTypes is null ? other.FileTypes is null : other.FileTypes is not null && FileTypes.SequenceEqual(other.FileTypes, StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// The command line of a Thix app's setup (SETUP-05/08) as ThixInstall passes it - the same for every Thix app, so devices and the cloud build
/// it in one place:
/// <list type="bullet">
/// <item>Windows install: <c>&lt;Setup&gt;.exe --quiet --desktop-shortcut=yes|no --autostart=yes|no --automatic-updates=yes|no --file-types=.a,.b|none</c>
/// (only the options the app offers) and <c>--start</c> when autostart was chosen.</item>
/// <item>Windows update: <c>--update</c> (the installed app keeps its choices). An app whose manifest has no <c>installer</c> block (an older
/// build) is installed with <c>--update</c> too: that is the silent install every Thix app has understood from the start.</item>
/// <item>macOS: after the bundle was placed, <c>&lt;App&gt;.app/Contents/MacOS/&lt;App&gt; --configure &lt;options&gt;</c>.</item>
/// </list>
/// Exit code <see cref="RefusedExitCode"/>: the app refused the options (an unknown option or value) and installed nothing.
/// </summary>
public static partial class ThixAppSetup
{
    public const string QuietArgument = "--quiet";
    public const string UpdateArgument = "--update";
    public const string ConfigureArgument = "--configure";
    public const string StartArgument = "--start";
    public const string DesktopShortcutOption = "--desktop-shortcut";
    public const string AutostartOption = "--autostart";
    public const string AutomaticUpdatesOption = "--automatic-updates";
    public const string FileTypesOption = "--file-types";

    /// <summary>The most file types a config keeps (a manifest is not trusted blindly).</summary>
    public const int MaxFileTypes = 50;

    /// <summary>The exit code of a setup that refused its options (nothing was installed); the message is on stderr.</summary>
    public const int RefusedExitCode = 2;

    /// <summary>The choices of a first install that is told nothing: the defaults of <paramref name="config"/>, only for what it offers.</summary>
    public static InstallChoices Defaults(InstallerConfig config) => new(
        config.DesktopShortcut?.Default,
        config.Autostart?.Default,
        config.AutomaticUpdates?.Default,
        config.FileTypes.Count == 0 ? null : config.FileTypes.Where(type => type.Default).Select(type => type.Extension).ToList());

    /// <summary>
    /// What an install passes: for every option <paramref name="config"/> offers, the choice or else its default; options it does not offer and
    /// file types it does not name are dropped (a setup refuses them). The extensions take the spelling of the config.
    /// </summary>
    public static InstallChoices Effective(InstallerConfig config, InstallChoices? choices)
    {
        var defaults = Defaults(config);
        var wanted = (choices ?? new InstallChoices()).Over(defaults);
        IReadOnlyList<string>? fileTypes = null;
        if (config.FileTypes.Count > 0)
        {
            fileTypes = config.FileTypes
                .Where(type => (wanted.FileTypes ?? []).Any(extension => SameExtension(extension, type.Extension)))
                .Select(type => type.Extension)
                .ToList();
        }

        return new InstallChoices(
            config.DesktopShortcut is null ? null : wanted.DesktopShortcut,
            config.Autostart is null ? null : wanted.Autostart,
            config.AutomaticUpdates is null ? null : wanted.AutomaticUpdates,
            fileTypes);
    }

    /// <summary>The options of SETUP-08 for these choices, without the action (what is null is left out).</summary>
    public static IReadOnlyList<string> Options(InstallChoices choices)
    {
        var arguments = new List<string>();
        static string YesNo(bool value) => value ? "yes" : "no";
        if (choices.DesktopShortcut is { } shortcut)
        {
            arguments.Add($"{DesktopShortcutOption}={YesNo(shortcut)}");
        }

        if (choices.Autostart is { } autostart)
        {
            arguments.Add($"{AutostartOption}={YesNo(autostart)}");
        }

        if (choices.AutomaticUpdates is { } updates)
        {
            arguments.Add($"{AutomaticUpdatesOption}={YesNo(updates)}");
        }

        if (choices.FileTypes is { } types)
        {
            arguments.Add($"{FileTypesOption}={(types.Count == 0 ? "none" : string.Join(',', types))}");
        }

        return arguments;
    }

    /// <summary>
    /// The arguments of the Windows setup of a Thix app: an update <c>--update</c>; an install <c>--quiet</c> with the options of
    /// <paramref name="config"/> (the choices, else the defaults) and <c>--start</c> when autostart is on; without a config <c>--update</c>.
    /// </summary>
    public static IReadOnlyList<string> WindowsArguments(InstallerConfig? config, InstallChoices? choices, bool isUpdate)
    {
        if (isUpdate || config is null)
        {
            return [UpdateArgument];
        }

        var effective = Effective(config, choices);
        var arguments = new List<string> { QuietArgument };
        arguments.AddRange(Options(effective));
        if (effective.Autostart == true)
        {
            arguments.Add(StartArgument);
        }

        return arguments;
    }

    /// <summary>
    /// The arguments for the app's own program after its bundle was placed on macOS (<c>--configure</c> with the options); null when there is
    /// nothing to configure (an update keeps the choices, an app without a config offers none).
    /// </summary>
    public static IReadOnlyList<string>? MacConfigureArguments(InstallerConfig? config, InstallChoices? choices, bool isUpdate)
    {
        if (isUpdate || config is null)
        {
            return null;
        }

        var options = Options(Effective(config, choices));
        return options.Count == 0 ? null : [ConfigureArgument, .. options];
    }

    /// <summary>
    /// A config as it may travel on a command line: extensions of letters, digits, "-" and "_" (with a dot, at most 20 characters), each once,
    /// names cut to 100 characters, at most <see cref="MaxFileTypes"/>. Null stays null.
    /// </summary>
    public static InstallerConfig? Sanitize(InstallerConfig? config)
    {
        if (config is null)
        {
            return null;
        }

        var types = new List<InstallerFileType>();
        foreach (var type in config.FileTypes ?? [])
        {
            if (types.Count == MaxFileTypes)
            {
                break;
            }

            if (type is null || NormalizeExtension(type.Extension) is not { } extension || types.Any(known => SameExtension(known.Extension, extension)))
            {
                continue;
            }

            var name = (type.Name ?? "").Trim();
            types.Add(type with { Extension = extension, Name = name.Length > 100 ? name[..100] : name });
        }

        return config with { FileTypes = types };
    }

    /// <summary>".md" for "md", ".MD" or ".md"; null for anything that is no plain extension.</summary>
    public static string? NormalizeExtension(string? value)
    {
        var trimmed = (value ?? "").Trim();
        var extension = trimmed.StartsWith('.') ? trimmed : "." + trimmed;
        return ExtensionPattern().IsMatch(extension) ? extension.ToLowerInvariant() : null;
    }

    private static bool SameExtension(string a, string b) =>
        string.Equals(NormalizeExtension(a), NormalizeExtension(b), StringComparison.Ordinal) && NormalizeExtension(a) is not null;

    [GeneratedRegex(@"^\.[A-Za-z0-9_-]{1,20}$")]
    private static partial Regex ExtensionPattern();
}
