using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Data;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Tests;

/// <summary>The business layer against a real PostgreSQL (THIXINSTALL_TEST_DB): enrollment, actions, profiles, bundles, the catalog and the cache.</summary>
public sealed class FlowTests
{
    private static async Task<(DeviceIdentity Identity, string Token)> EnrollAsync(TestDatabase test, EnrollRequest request)
    {
        var outcome = await DeviceService.EnrollAsync(test.NewContext(), request, "127.0.0.1");
        Assert.True(outcome.Ok, outcome.Error);
        var identity = await DeviceService.AuthenticateAsync(test.NewContext(), outcome.Value!.Token);
        Assert.NotNull(identity);
        return (identity!, outcome.Value.Token);
    }

    [DbFact]
    public async Task ADeviceEnrollsTakesAPushedActionAndReportsHowItEnded()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna", password: "anna-password");
        var vlc = await test.AddPackageAsync("vlc");

        var wrong = await DeviceService.EnrollAsync(test.NewContext(), new EnrollRequest { UserName = "anna", Password = "wrong", Device = Samples.Windows() }, null);
        Assert.Equal("password", wrong.Field);

        var (device, token) = await EnrollAsync(test, new EnrollRequest { UserName = "anna", Password = "anna-password", Device = Samples.Windows(), EnableSync = false });
        Assert.False(device.SyncEnabled);

        // without "Sync" the device takes no actions
        var refused = await ActionService.PushAsync(test.NewContext(), new Actor(anna.Id, false), device.DeviceId, new PushRequest(ActionKind.Install, vlc.Id, null));
        Assert.False(refused.Ok);

        await DeviceService.UpdateSelfAsync(test.NewContext(), device, new DeviceUpdate(SyncEnabled: true));
        device = (await DeviceService.AuthenticateAsync(test.NewContext(), token))!;
        Assert.True(device.SyncEnabled);

        // the device waits; the push wakes it
        var waiting = ActionService.WaitAsync(test.NewContext(), device.DeviceId, 0, TimeSpan.FromSeconds(20), CancellationToken.None);
        var pushed = await ActionService.PushAsync(test.NewContext(), new Actor(anna.Id, false), device.DeviceId, new PushRequest(ActionKind.Install, vlc.Id, null));
        Assert.True(pushed.Ok, pushed.Error);
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(10)) > 0);

        var open = await ActionService.OpenAsync(test.NewContext(), device);
        var action = Assert.Single(open.Actions);
        Assert.Equal("VLC installieren", action.Title);
        Assert.Equal(InstallMethod.Chocolatey, action.Package!.Install!.Method);
        Assert.Equal("vlc", action.Package.Install.PackageId);

        Assert.True((await ActionService.StartAsync(test.NewContext(), device, action.Id)).Ok);
        Assert.False((await ActionService.StartAsync(test.NewContext(), device, action.Id)).Ok);
        var now = DateTimeOffset.UtcNow;
        Assert.True((await ActionService.FinishAsync(test.NewContext(), device, action.Id, new ActionResult(true, 0, "ok", "3.0.21", now.AddSeconds(-5), now))).Ok);
        Assert.Empty((await ActionService.OpenAsync(test.NewContext(), device)).Actions);

        var view = await ActionService.GetAsync(test.NewContext(), new Actor(anna.Id, false), action.Id);
        Assert.Equal(ActionState.Succeeded, view.Value!.State);
        Assert.Equal("3.0.21", view.Value.InstalledVersion);

        // somebody else sees neither the device nor its actions
        var bob = await test.AddUserAsync("bob");
        Assert.True((await ActionService.GetAsync(test.NewContext(), new Actor(bob.Id, false), action.Id)).NotFound);
        Assert.True((await DeviceService.GetAsync(test.NewContext(), new Actor(bob.Id, false), device.DeviceId)).NotFound);

        // enrolling the same machine again reuses the device; the old token ends
        var (again, _) = await EnrollAsync(test, new EnrollRequest { UserName = "anna", Password = "anna-password", Device = Samples.Windows() });
        Assert.Equal(device.DeviceId, again.DeviceId);
        Assert.Null(await DeviceService.AuthenticateAsync(test.NewContext(), token));
    }

    [DbFact]
    public async Task ACancelledActionCannotBeStarted()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna", password: "anna-password");
        var (device, _) = await EnrollAsync(test, new EnrollRequest { UserName = "anna", Password = "anna-password", Device = Samples.Windows() });
        var pushed = await ActionService.PushAsync(test.NewContext(), new Actor(anna.Id, false), device.DeviceId, new PushRequest(ActionKind.UpdateAll, null, null));
        Assert.True((await ActionService.CancelAsync(test.NewContext(), new Actor(anna.Id, false), pushed.Value)).Ok);
        Assert.False((await ActionService.StartAsync(test.NewContext(), device, pushed.Value)).Ok);
    }

    [DbFact]
    public async Task AutoEnrollmentGivesTheProfileAndItsItemsOfTheRightSystemBecomeActions()
    {
        await using var test = await TestDatabase.CreateAsync();
        var admin = await test.AddUserAsync("admin", admin: true);
        var actor = new Actor(admin.Id, true);
        var firefox = await test.AddPackageAsync("firefox", windows: InstallMethod.Chocolatey, mac: InstallMethod.Homebrew);
        var sevenZip = await test.AddPackageAsync("7zip");
        var winScript = await test.AddScriptAsync("win-tweak", TargetOs.Windows);
        var macScript = await test.AddScriptAsync("mac-tweak", TargetOs.MacOS);

        var db = test.NewContext();
        db.ActingUserId = admin.Id;
        var profile = (await ProfileService.SaveAsync(db, actor, null, "Büro", null)).Value;
        foreach (var (package, script) in new (long?, long?)[] { (firefox.Id, null), (null, winScript.Id), (null, macScript.Id) })
        {
            Assert.True((await ProfileService.AddItemAsync(test.NewContext(), actor, profile, package, script)).Ok);
        }

        var key = (await ProfileService.GetAsync(test.NewContext(), actor, profile)).Value!.EnrollmentKey;
        var bad = await DeviceService.EnrollAsync(test.NewContext(), new EnrollRequest { EnrollmentKey = Guid.NewGuid().ToString(), Device = Samples.Mac() }, null);
        Assert.False(bad.Ok);

        var (mac, _) = await EnrollAsync(test, new EnrollRequest { EnrollmentKey = key, Device = Samples.Mac() });
        Assert.True(mac.SyncEnabled);
        Assert.Equal(admin.Id, mac.UserId);
        var macActions = (await ActionService.OpenAsync(test.NewContext(), mac)).Actions;
        Assert.Equal(["FIREFOX installieren", "Skript „mac-tweak“ ausführen"], macActions.Select(a => a.Title));
        Assert.All(macActions, a => Assert.Equal(ActionOrigin.Profile, a.Origin));
        Assert.Equal(InstallMethod.Homebrew, macActions[0].Package!.Install!.Method);

        var (pc, _) = await EnrollAsync(test, new EnrollRequest { EnrollmentKey = key, Device = Samples.Windows() });
        Assert.Equal(2, (await ActionService.OpenAsync(test.NewContext(), pc)).Actions.Count);

        // a new item of the profile reaches the devices that have it (the Mac has no 7-Zip)
        Assert.Equal(1, (await ProfileService.AddItemAsync(test.NewContext(), actor, profile, sevenZip.Id, null)).Value);
        Assert.Equal(3, (await ActionService.OpenAsync(test.NewContext(), pc)).Actions.Count);
        Assert.Equal(2, (await ActionService.OpenAsync(test.NewContext(), mac)).Actions.Count);

        // applying the profile again does not repeat what is open
        Assert.Equal(0, await ActionService.ApplyProfileAsync(test.NewContext(), pc.DeviceId));
    }

    [DbFact]
    public async Task AnonymousVisitorsSeeAndBundleOnlyPublicItems()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna", password: "anna-password");
        var open = await test.AddPackageAsync("open");
        var secret = await test.AddPackageAsync("secret", isPublic: false);
        await test.AddScriptAsync("hidden", TargetOs.Windows, isPublic: false);
        await ReleaseService.ResolveAsync(test.NewContext(), open.Id);

        var anonymous = await CatalogService.CatalogAsync(test.NewContext(), null, TargetOs.Windows, CpuArchitecture.X64);
        Assert.Equal(["open"], anonymous.Packages.Select(p => p.Key));
        Assert.Empty(anonymous.Scripts);
        var signedIn = await CatalogService.CatalogAsync(test.NewContext(), new Actor(anna.Id, false), TargetOs.Windows, CpuArchitecture.X64);
        Assert.Equal(2, signedIn.Packages.Count);
        Assert.Single(signedIn.Scripts);
        Assert.Empty((await CatalogService.CatalogAsync(test.NewContext(), null, TargetOs.MacOS, CpuArchitecture.Arm64)).Packages);

        Assert.False((await BundleService.CreateAsync(test.NewContext(), null, new BundleRequest(TargetOs.Windows, [secret.Id], [], null, false, null))).Ok);
        Assert.False((await BundleService.CreateAsync(test.NewContext(), null, new BundleRequest(TargetOs.Windows, [open.Id], [], null, true, null))).Ok);
        var token = (await BundleService.CreateAsync(test.NewContext(), null, new BundleRequest(TargetOs.Windows, [open.Id], [], null, false, null))).Value!;
        var bundle = await BundleService.DtoAsync(test.NewContext(), token, CpuArchitecture.X64);
        Assert.Equal(["open"], bundle!.Packages.Select(p => p.Key));
        Assert.False(bundle.CanEnroll);

        // an anonymous bundle enrolls nobody; the bundle of a signed-in person that installs ThixInstall does
        var refused = await DeviceService.EnrollAsync(test.NewContext(), new EnrollRequest { BundleToken = token, Device = Samples.Windows() }, null);
        Assert.False(refused.Ok);
        var agentToken = (await BundleService.CreateAsync(test.NewContext(), new Actor(anna.Id, false), new BundleRequest(TargetOs.Windows, [secret.Id], [], "Büro-PC", true, null))).Value!;
        var (device, _) = await EnrollAsync(test, new EnrollRequest { BundleToken = agentToken, Device = Samples.Windows() });
        Assert.Equal(anna.Id, device.UserId);

        // the setting closes the catalog for visitors
        await SettingService.SetAsync(test.NewContext(), SettingService.PublicCatalogKey, "false");
        Assert.False(await CatalogService.IsPublicAsync(test.NewContext()));
        Assert.False((await BundleService.CreateAsync(test.NewContext(), null, new BundleRequest(TargetOs.Windows, [open.Id], [], null, false, null))).Ok);
    }

    [DbFact]
    public async Task ADirectDownloadIsFetchedOnceCheckedAndThenServedFromTheCache()
    {
        await using var test = await TestDatabase.CreateAsync();
        var admin = await test.AddUserAsync("admin", admin: true);
        var bytes = "MZ fake installer"u8.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var world = new FakeWorld(r => r.RequestUri!.AbsoluteUri == "https://downloads.example.com/tool-setup.exe"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : null);
        Net.UseHandlerForTests(world);
        DownloadCache.UseDirectory(Directory.CreateTempSubdirectory("thixinstall-cache-").FullName);
        try
        {
            var db = test.NewContext();
            var actor = new Actor(admin.Id, true);
            var id = (await CatalogService.SaveGeneralAsync(db, actor, new PackageInput { Name = "Tool", Category = "Werkzeuge" })).Value;
            var wrongSha = await CatalogService.SaveWindowsAsync(test.NewContext(), actor, new PackageInput
            {
                Id = id, WindowsMethod = InstallMethod.Direct, DirectUrl = "https://downloads.example.com/tool-setup.exe", DirectSilentArguments = "/S", DirectSha256 = new string('0', 64), DirectVersion = "1.2",
            });
            Assert.True(wrongSha.Ok);

            var catalog = await CatalogService.CatalogAsync(test.NewContext(), null, TargetOs.Windows, CpuArchitecture.X64);
            var plan = Assert.Single(catalog.Packages).Install!;
            Assert.Equal(InstallMethod.Direct, plan.Method);
            Assert.Equal(InstallerKind.Exe, plan.InstallerKind);
            Assert.Equal("/S", plan.SilentArguments);
            Assert.Equal("1.2", catalog.Packages[0].LatestVersion);
            var releaseId = long.Parse(plan.DownloadUrl!.Split('/').Last(), System.Globalization.CultureInfo.InvariantCulture);

            // a file that is not what the catalog promises is refused and not kept
            Assert.False((await DownloadCache.ReleaseAsync(test.NewContext(), releaseId, CancellationToken.None)).Ok);
            Assert.Equal(0, await test.NewContext().CacheFiles.CountAsync());

            await CatalogService.SaveWindowsAsync(test.NewContext(), actor, new PackageInput
            {
                Id = id, WindowsMethod = InstallMethod.Direct, DirectUrl = "https://downloads.example.com/tool-setup.exe", DirectSilentArguments = "/S", DirectSha256 = sha,
            });
            var first = await DownloadCache.ReleaseAsync(test.NewContext(), releaseId, CancellationToken.None);
            Assert.True(first.Ok, first.Error);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(first.Value!.Path));
            Assert.Equal("tool-setup.exe", first.Value.FileName);
            var requests = world.Requests;
            var second = await DownloadCache.ReleaseAsync(test.NewContext(), releaseId, CancellationToken.None);
            Assert.True(second.Ok);
            Assert.Equal(requests, world.Requests); // from the cache
            var row = await test.NewContext().CacheFiles.SingleAsync();
            Assert.Equal(2, row.UseCount);
        }
        finally
        {
            Net.UseHandlerForTests(null);
        }
    }
}
