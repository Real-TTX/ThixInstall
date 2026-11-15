using ThixInstall.Core.Business;
using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Models;

namespace ThixInstall.Tests;

/// <summary>Autostart and updates. Nothing here touches the real program folder, registry values or LaunchAgents (the setup needs administrator rights: CI and the owner try it).</summary>
public sealed class PlatformTests : IDisposable
{
    private readonly TestData _data = new();

    public void Dispose()
    {
        AutostartService.UseLaunchAgentFolderForTests(null);
        _data.Dispose();
    }

    [Fact]
    public void The_launch_agent_starts_thixinstall_hidden_at_sign_in()
    {
        var plist = AutostartService.LaunchAgent("/Applications/ThixInstall.app/Contents/MacOS/ThixInstall");
        Assert.Contains("<string>info.schmoldt.thixinstall</string>", plist);
        Assert.Contains("<string>/Applications/ThixInstall.app/Contents/MacOS/ThixInstall</string>", plist);
        Assert.Contains($"<string>{AutostartService.StartupArgument}</string>", plist);
        Assert.Contains("<key>RunAtLoad</key>", plist);
    }

    [Fact]
    public void Autostart_on_macos_writes_and_removes_the_launch_agent()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        AutostartService.UseLaunchAgentFolderForTests(_data.Folder);
        AutostartService.WriteEntry("/Applications/ThixInstall.app/Contents/MacOS/ThixInstall");
        Assert.True(AutostartService.IsEnabled());
        AutostartService.Disable();
        Assert.False(AutostartService.IsEnabled());
    }

    [Fact]
    public void Updates_only_come_from_the_own_channel_and_a_higher_build()
    {
        var manifest = new UpdateManifest("0.1.50-20261201", "release", 50, "ThixInstallSetup.exe", "AB", DateTimeOffset.UtcNow,
            new Dictionary<string, UpdatePackage> { ["osx-arm64"] = new("ThixInstall-macOS-arm64.zip", "CD") });

        Assert.True(UpdateService.IsNewer(manifest, "release", 49));
        Assert.False(UpdateService.IsNewer(manifest, "release", 50));
        Assert.False(UpdateService.IsNewer(manifest, "nightly", 1));
        Assert.Equal("ThixInstallSetup.exe", UpdateService.PackageFor(manifest, "win-x64")!.File);
        Assert.Equal("ThixInstall-macOS-arm64.zip", UpdateService.PackageFor(manifest, "osx-arm64")!.File);
        Assert.Null(UpdateService.PackageFor(manifest, "osx-x64"));
        Assert.True(UpdateService.IsSafeFileName("ThixInstallSetup.exe"));
        Assert.False(UpdateService.IsSafeFileName("../evil.exe"));
        Assert.False(UpdateService.IsSafeFileName("setup.ps1"));
    }

    [Fact]
    public async Task A_local_build_or_the_policy_stops_updates()
    {
        // CI builds are release or nightly builds; a local build never updates itself.
        if (AppInfo.IsLocalBuild)
        {
            Assert.False(UpdateService.CanUpdate);
        }

        AccessPolicy.UsePolicyReaderForTests(name => name == "DisableUpdates");
        Assert.False(UpdateService.CanUpdate);
        await Assert.ThrowsAsync<BusinessException>(() => UpdateService.CheckAsync());
    }

    [Fact]
    public void The_mac_update_script_swaps_the_bundle_and_restarts_in_the_menu_bar()
    {
        var script = MacAppBundle.ReplacementScript(42, "/Applications/ThixInstall.app", "/tmp/new/ThixInstall.app", "/tmp/update.log");
        Assert.Contains("while kill -0 42", script);
        Assert.Contains("BUNDLE='/Applications/ThixInstall.app'", script);
        Assert.Contains("--args --tray", script);
        Assert.EndsWith("Applications" + Path.DirectorySeparatorChar + "ThixInstall.app",
            MacAppBundle.BundleOf("/Applications/ThixInstall.app/Contents/MacOS/ThixInstall")!.Replace('/', Path.DirectorySeparatorChar));
        Assert.Null(MacAppBundle.BundleOf("/usr/local/bin/ThixInstall"));
    }
}
