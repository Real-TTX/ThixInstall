using ThixInstall.Core.Business;
using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Installers;
using ThixInstall.Core.Models;
using ThixInstall.Shared;

namespace ThixInstall.Tests;

/// <summary>
/// The install options of SETUP-08: the command line of ThixInstall's own setup (installer.json, --quiet / --update / --configure and the
/// options, the older switches), and the command line ThixInstall builds for the Thix apps of the catalog (Windows setup, macOS --configure).
/// </summary>
public sealed class InstallOptionsTests : IDisposable
{
    private static readonly InstallerConfig Fred = new()
    {
        DesktopShortcut = new InstallerSwitch { Default = false },
        Autostart = new InstallerSwitch { Default = true },
        AutomaticUpdates = new InstallerSwitch { Default = true },
    };

    private static readonly InstallerConfig Note = new()
    {
        Autostart = new InstallerSwitch { Default = false },
        FileTypes = [new InstallerFileType { Extension = ".md", Name = "Markdown-Notiz", Default = true }, new InstallerFileType { Extension = ".txt", Name = "Text" }],
    };

    private readonly TestData _data = new();

    public void Dispose()
    {
        InstallerOptions.UseConfigForTests(null);
        _data.Dispose();
    }

    // ---- ThixInstall's own setup ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_embedded_installer_json_offers_shortcut_autostart_and_updates_without_file_types()
    {
        var config = InstallerOptions.Config;
        Assert.False(config.DesktopShortcut!.Default);
        Assert.True(config.Autostart!.Default);
        Assert.True(config.AutomaticUpdates!.Default);
        Assert.Empty(config.FileTypes);
        Assert.True(InstallerOptions.Defaults.SameAs(new InstallChoices(false, true, true, [])));
    }

    [Fact]
    public void A_quiet_install_reads_the_options_and_the_older_switches()
    {
        var quiet = InstallerOptions.Parse(["--quiet", "--desktop-shortcut=yes", "--autostart=no", "--automatic-updates=nein", "--file-types=none", "--start"]);
        Assert.Equal(SetupAction.Install, quiet.Action);
        Assert.True(quiet.Start);
        Assert.True(quiet.Choices.SameAs(new InstallChoices(true, false, false, [])));

        // the switches of the older setup (and the profile page's command line) still work
        var older = InstallerOptions.Parse(["--install", "--quiet", "--no-autostart", "--desktop", "--no-auto-update", "--cloud", "https://install.example.com", "--enroll-key", "abc"]);
        Assert.True(older.Choices.SameAs(new InstallChoices(true, false, false)));
        var assistant = InstallerOptions.Parse(["--install", "--quiet", "--autostart"]);
        Assert.True(assistant.Choices.Autostart);

        // nothing given: a first install takes the defaults, an update keeps what it has (the values stay null here)
        Assert.True(InstallerOptions.Parse(["--update", "--quiet"]) is { Action: SetupAction.Update, Choices: { DesktopShortcut: null, Autostart: null } });
        Assert.Equal(SetupAction.Configure, InstallerOptions.Parse(["--configure", "--autostart=yes"]).Action);
        Assert.Equal(SetupAction.Uninstall, InstallerOptions.Parse(["--uninstall", "--quiet"]).Action);
    }

    [Fact]
    public void A_bare_autostart_without_an_action_is_the_start_at_sign_in()
    {
        var request = InstallerOptions.Parse([AutostartService.StartupArgument]);
        Assert.Equal(SetupAction.None, request.Action);
        Assert.True(request.Choices.SameAs(new InstallChoices()));
    }

    [Theory]
    [InlineData("--quiet", "--colour=blue")] // unknown option
    [InlineData("--quiet", "--autostart=maybe")] // wrong value
    [InlineData("--quiet", "--file-types=.md")] // ThixInstall opens no file types
    [InlineData("--autostart=yes", null)] // an option without --quiet / --update / --configure
    [InlineData("--uninstall", "--desktop-shortcut=no")]
    public void A_wrong_option_is_refused_before_anything_happens(string first, string? second)
    {
        string[] args = second is null ? [first] : [first, second];
        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(args));
    }

    [Fact]
    public void An_option_the_app_does_not_offer_is_refused()
    {
        InstallerOptions.UseConfigForTests(Note);
        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--quiet", "--desktop-shortcut=yes"]));
        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--quiet", "--file-types=.md,.exe"]));
        Assert.Equal(new[] { ".md", ".txt" }, InstallerOptions.Parse(["--quiet", "--file-types=md,.TXT,.md"]).Choices.FileTypes!);
        Assert.Equal(new[] { ".md" }, InstallerOptions.Parse(["--quiet", "--file-types=default"]).Choices.FileTypes!);
    }

    [Fact]
    public void Configure_on_macos_writes_the_launch_agent_and_the_update_setting()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        AutostartService.UseLaunchAgentFolderForTests(_data.Folder);
        try
        {
            InstallerOptions.Apply(new InstallChoices(Autostart: true, AutomaticUpdates: false), "/Applications/ThixInstall.app/Contents/MacOS/ThixInstall", _ => { });
            Assert.True(AutostartService.IsEnabled());
            Assert.False(SettingsStore.ReadAgent().InstallUpdatesAutomatically);
            InstallerOptions.Apply(new InstallChoices(Autostart: false), "/Applications/ThixInstall.app/Contents/MacOS/ThixInstall", _ => { });
            Assert.False(AutostartService.IsEnabled());
        }
        finally
        {
            AutostartService.UseLaunchAgentFolderForTests(null);
        }
    }

    [Fact]
    public void The_options_of_the_setup_assistant_are_the_arguments_of_setup_08()
    {
        Assert.Equal(new[] { "--desktop-shortcut=yes", "--autostart=no", "--automatic-updates=yes" },
            InstallerOptions.ToArguments(new InstallChoices(true, false, true)));
        var back = InstallerOptions.Parse(["--quiet", .. InstallerOptions.ToArguments(new InstallChoices(true, false, true))]);
        Assert.True(back.Choices.SameAs(new InstallChoices(true, false, true)));
    }

    // ---- the Thix apps of the catalog --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_thix_app_is_installed_quietly_with_its_options_and_started_when_autostart_is_on()
    {
        // nothing picked: the defaults of the app, every offered option explicit
        Assert.Equal("--quiet --desktop-shortcut=no --autostart=yes --automatic-updates=yes --start",
            string.Join(' ', ThixAppSetup.WindowsArguments(Fred, null, isUpdate: false)));

        // picked on the start page; what the app does not offer is left out
        Assert.Equal("--quiet --desktop-shortcut=yes --autostart=no --automatic-updates=no",
            string.Join(' ', ThixAppSetup.WindowsArguments(Fred, new InstallChoices(true, false, false, [".md"]), isUpdate: false)));
        Assert.Equal("--quiet --autostart=no --file-types=.md",
            string.Join(' ', ThixAppSetup.WindowsArguments(Note, new InstallChoices(DesktopShortcut: true), isUpdate: false)));
        Assert.Equal("--quiet --autostart=yes --file-types=none --start",
            string.Join(' ', ThixAppSetup.WindowsArguments(Note, new InstallChoices(Autostart: true, FileTypes: [".exe"]), isUpdate: false)));
    }

    [Fact]
    public void An_update_or_an_app_without_options_keeps_the_known_silent_install()
    {
        Assert.Equal(new[] { "--update" }, ThixAppSetup.WindowsArguments(Fred, new InstallChoices(true), isUpdate: true));
        Assert.Equal(new[] { "--update" }, ThixAppSetup.WindowsArguments(null, null, isUpdate: false));
        Assert.Null(ThixAppSetup.MacConfigureArguments(Fred, null, isUpdate: true));
        Assert.Null(ThixAppSetup.MacConfigureArguments(null, null, isUpdate: false));
        Assert.Null(ThixAppSetup.MacConfigureArguments(new InstallerConfig(), null, isUpdate: false));
    }

    [Fact]
    public void On_macos_the_placed_bundle_is_configured_by_its_own_program()
    {
        Assert.Equal("--configure --autostart=no --file-types=.md,.txt",
            string.Join(' ', ThixAppSetup.MacConfigureArguments(Note, new InstallChoices(FileTypes: [".TXT", ".md"]), isUpdate: false)!));

        var bundle = Path.Combine(_data.Folder, "ThixNote.app");
        Assert.Null(ThixAppInstall.MacExecutable(bundle));
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(bundle, "Contents", "MacOS", "Helper"), "");
        Assert.Equal(Path.Combine(bundle, "Contents", "MacOS", "Helper"), ThixAppInstall.MacExecutable(bundle));
        File.WriteAllText(Path.Combine(bundle, "Contents", "MacOS", "ThixNote"), "");
        Assert.Equal(Path.Combine(bundle, "Contents", "MacOS", "ThixNote"), ThixAppInstall.MacExecutable(bundle));
    }

    [Fact]
    public void A_refused_setup_names_its_reason_and_thix_apps_run_as_the_user()
    {
        var package = SampleCatalog.Package(9, "thixfred", "ThixFred", "Thix-Apps", "0.1.42-20261119") with
        {
            Install = new InstallPlan { Method = InstallMethod.ThixApp, DownloadUrl = ApiContract.Download(4), InstallerKind = InstallerKind.Exe },
            Installer = Fred,
        };
        Assert.Equal("Das Setup von ThixFred hat die Installationsoptionen abgelehnt und nichts geändert: Unbekannte Option „--colour“.",
            ThixAppInstall.RefusedMessage(package, "starting\r\nUnbekannte Option „--colour“.\r\n"));

        // per user: the Windows service hands install, update and uninstall to the app in the user's session
        foreach (var kind in new[] { ActionKind.Install, ActionKind.Update, ActionKind.Uninstall })
        {
            Assert.True(JobService.NewJob(kind, ActionOrigin.Push, package: package).RunsAsUser);
        }

        Assert.False(JobService.NewJob(ActionKind.Install, ActionOrigin.Push, package: SampleCatalog.Create().Packages[0]).RunsAsUser);
    }

    [Fact]
    public void A_config_from_a_manifest_keeps_plain_extensions_only()
    {
        var config = ThixAppSetup.Sanitize(new InstallerConfig
        {
            FileTypes =
            [
                new InstallerFileType { Extension = "MD", Name = "Notiz" }, new InstallerFileType { Extension = ".md", Name = "doppelt" },
                new InstallerFileType { Extension = ".a b" }, new InstallerFileType { Extension = ".x;calc" }, new InstallerFileType { Extension = ".thix-note_1" },
            ],
        })!;
        Assert.Equal(new[] { ".md", ".thix-note_1" }, config.FileTypes.Select(type => type.Extension));
        Assert.Null(ThixAppSetup.Sanitize(null));
    }
}
