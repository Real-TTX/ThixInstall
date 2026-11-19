using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Data;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Tests;

/// <summary>
/// The Thix apps of the catalog (SETUP-08): the cloud reads their update manifests (version, packages, the install options of "installer"), the start
/// page asks the options, a bundle stores them and hands them to the device in the plan of the app.
/// </summary>
[Collection(TestWorld.Collection)]
public sealed class ThixAppTests
{
    private const string Manifest = """
        {
          "version": "0.1.42-20261119", "channel": "release", "buildNumber": 42,
          "setup": "ThixFredSetup.exe", "sha256": "1111111111111111111111111111111111111111111111111111111111111111",
          "installer": {
            "desktopShortcut": { "default": false },
            "autostart": { "default": true },
            "fileTypes": [ { "extension": ".md", "name": "Markdown-Notiz", "default": true }, { "extension": "TXT", "name": "Text" },
                           { "extension": ".a b", "name": "kaputt" }, { "extension": ".md", "name": "doppelt" } ]
          },
          "packages": {
            "win-x64": { "file": "ThixFredSetup.exe", "sha256": "1111111111111111111111111111111111111111111111111111111111111111" },
            "osx-arm64": { "file": "ThixFred-macOS-arm64.zip", "sha256": "2222222222222222222222222222222222222222222222222222222222222222" },
            "osx-x64": { "file": "../evil.zip", "sha256": "3333333333333333333333333333333333333333333333333333333333333333" }
          }
        }
        """;

    [Fact]
    public void TheManifestNamesVersionPackagesAndTheInstallOptions()
    {
        var manifest = ThixAppResolver.Parse(Manifest);
        Assert.Equal("0.1.42-20261119", manifest.Version);
        Assert.Equal(new[] { "osx-arm64", "win-x64" }, manifest.Packages.Keys.Order());

        // only what a command line can carry: plain extensions, each once, lower case
        var installer = manifest.Installer!;
        Assert.False(installer.DesktopShortcut!.Default);
        Assert.True(installer.Autostart!.Default);
        Assert.Null(installer.AutomaticUpdates);
        Assert.Equal(new[] { ".md", ".txt" }, installer.FileTypes.Select(type => type.Extension));

        var windows = ThixAppResolver.ReleasesFor(manifest, "Real-TTX/ThixFred", "ThixFred.app", TargetOs.Windows);
        Assert.Equal(InstallerKind.Exe, windows[CpuArchitecture.X64].Kind);
        Assert.Same(windows[CpuArchitecture.X64], windows[CpuArchitecture.Arm64]);
        Assert.Equal("https://github.com/Real-TTX/ThixFred/releases/download/release-channel/ThixFredSetup.exe", windows[CpuArchitecture.X64].InstallerUrl);

        var mac = ThixAppResolver.ReleasesFor(manifest, "Real-TTX/ThixFred", "ThixFred.app", TargetOs.MacOS);
        var arm = Assert.Single(mac).Value;
        Assert.Equal(InstallerKind.Zip, arm.Kind);
        Assert.Equal("ThixFred.app", arm.AppBundleName);
    }

    [Fact]
    public void AManifestWithoutInstallerBlockOffersNothingAndAnOldOneNamesTheSetupOnly()
    {
        var manifest = ThixAppResolver.Parse("""{"version":"0.1.3-20261101","setup":"ThixClipSetup.exe","sha256":"AB"}""");
        Assert.Null(manifest.Installer);
        Assert.Equal("ThixClipSetup.exe", manifest.Packages["win-x64"].File);
        Assert.Throws<ResolveException>(() => ThixAppResolver.ReleasesFor(manifest, "Real-TTX/ThixClip", "ThixClip.app", TargetOs.MacOS));
        Assert.Throws<ResolveException>(() => ThixAppResolver.Parse("""{"channel":"release"}"""));
        Assert.Equal("thixfred-update.json", ThixAppResolver.ManifestOf("Real-TTX/ThixFred", null));
        Assert.True(ThixAppResolver.IsValidRepository("Real-TTX/ThixFred"));
        Assert.False(ThixAppResolver.IsValidRepository("https://github.com/Real-TTX/ThixFred"));
        Assert.False(ThixAppResolver.IsValidManifest("../x.json"));
    }

    [Fact]
    public void TheStoredChoicesAreOnlyWhatTheAppOffersAndTheBundlePageNamesThem()
    {
        var installer = ThixAppResolver.Parse(Manifest).Installer;
        var package = new PackageDto { Id = 5, Key = "thixfred", Name = "ThixFred", Category = "Thix-Apps", Installer = installer };
        var picked = new Dictionary<long, InstallChoices> { [5] = new(DesktopShortcut: true, Autostart: false, AutomaticUpdates: true, FileTypes: [".TXT", ".exe"]) };

        var json = BundleService.ChoicesJson(package, picked);
        var stored = BundleService.ParseChoices(json)!;
        Assert.True(stored.DesktopShortcut);
        Assert.False(stored.Autostart);
        Assert.Null(stored.AutomaticUpdates); // not offered
        Assert.Equal(new[] { ".txt" }, stored.FileTypes!); // .exe is not offered
        Assert.Equal("Desktop-Verknüpfung · ohne Autostart · öffnet .txt", BundleService.Describe(stored));

        // nothing picked for it, or an app without options: nothing stored (the defaults)
        Assert.Null(BundleService.ChoicesJson(package, new Dictionary<long, InstallChoices>()));
        Assert.Null(BundleService.ChoicesJson(package with { Installer = null }, picked));
    }

    [DbFact]
    public async Task AThixAppResolvesFromItsManifestAndABundleCarriesTheChosenOptions()
    {
        await using var test = await TestDatabase.CreateAsync();
        var admin = await test.AddUserAsync("admin", admin: true);
        var actor = new Actor(admin.Id, true);
        var world = new FakeWorld(r => r.RequestUri!.AbsoluteUri == "https://github.com/Real-TTX/ThixFred/releases/download/release-channel/thixfred-update.json"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Manifest) }
            : null);
        Net.UseHandlerForTests(world);
        try
        {
            var id = (await CatalogService.SaveGeneralAsync(test.NewContext(), actor, new PackageInput { Name = "ThixFred", Category = "Thix-Apps" })).Value;
            var invalid = await CatalogService.SaveWindowsAsync(test.NewContext(), actor, new PackageInput { Id = id, WindowsMethod = InstallMethod.ThixApp, ThixRepository = "ThixFred" });
            Assert.Equal("ThixRepository", invalid.Field);
            var windows = await CatalogService.SaveWindowsAsync(test.NewContext(), actor, new PackageInput { Id = id, WindowsMethod = InstallMethod.ThixApp, ThixRepository = "Real-TTX/ThixFred" });
            Assert.True(windows.Ok, windows.Error);
            Assert.DoesNotContain("Windows:", windows.Value, StringComparison.Ordinal);
            var mac = await CatalogService.SaveMacAsync(test.NewContext(), actor, new PackageInput { Id = id, MacMethod = InstallMethod.ThixApp, ThixRepository = "Real-TTX/ThixFred" });
            Assert.True(mac.Ok, mac.Error);

            // the device sees the setup from the cache, the install options and how it recognises the app
            var dto = Assert.Single((await CatalogService.CatalogAsync(test.NewContext(), null, TargetOs.Windows, CpuArchitecture.X64)).Packages);
            Assert.Equal(InstallMethod.ThixApp, dto.Install!.Method);
            Assert.NotNull(dto.Install.DownloadUrl);
            Assert.Equal(InstallerKind.Exe, dto.Install.InstallerKind);
            Assert.Equal("0.1.42-20261119", dto.LatestVersion);
            Assert.Equal("ThixFred", dto.Detection!.DisplayName);
            Assert.True(dto.Installer!.Autostart!.Default);
            Assert.Null(dto.Install.Choices);
            var onMac = Assert.Single((await CatalogService.CatalogAsync(test.NewContext(), null, TargetOs.MacOS, CpuArchitecture.Arm64)).Packages);
            Assert.Equal("ThixFred.app", onMac.Detection!.AppBundleName);
            Assert.NotNull(onMac.Installer);

            // the start page shows the options; a bundle keeps what was picked and the device gets it in the plan
            var (picker, _) = await CatalogService.PickerAsync(test.NewContext(), null, TargetOs.Windows);
            Assert.NotNull(Assert.Single(picker).Installer);
            var token = await BundleService.CreateAsync(test.NewContext(), null, new BundleRequest(TargetOs.Windows, [id], [], null, false, null,
                new Dictionary<long, InstallChoices> { [id] = new(DesktopShortcut: true, Autostart: false, FileTypes: []) }));
            Assert.True(token.Ok, token.Error);
            var bundle = await BundleService.DtoAsync(test.NewContext(), token.Value!, CpuArchitecture.X64);
            var choices = Assert.Single(bundle!.Packages).Install!.Choices!;
            Assert.True(choices.SameAs(new InstallChoices(true, false, null, [])));
            Assert.Empty(choices.FileTypes!);
            Assert.Equal("--quiet --desktop-shortcut=yes --autostart=no --file-types=none",
                string.Join(' ', ThixAppSetup.WindowsArguments(bundle.Packages[0].Installer, choices, isUpdate: false)));
            var view = await BundleService.ViewAsync(test.NewContext(), token.Value!);
            Assert.Equal("Desktop-Verknüpfung · ohne Autostart · keine Dateitypen", Assert.Single(view!.Packages).Note);

            // GitHub cannot be asked: the last release and the options stay, with the message
            Net.UseHandlerForTests(TestDatabase.Offline);
            var again = await ReleaseService.ResolveNowAsync(test.NewContext(), actor, id);
            Assert.Contains("Update-Manifest", again.Value, StringComparison.Ordinal);
            Assert.NotNull(Assert.Single((await CatalogService.CatalogAsync(test.NewContext(), null, TargetOs.Windows, CpuArchitecture.X64)).Packages).Installer);
        }
        finally
        {
            Net.UseHandlerForTests(TestDatabase.Offline);
        }
    }

    [DbFact]
    public async Task AnOlderThixInstallStillReadsEveryAnswerWithoutTheThixApps()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna", password: "anna-password");
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Default", test.ConnectionString);
            b.UseSetting("DataDirectory", Directory.CreateTempSubdirectory("thixinstall-test-").FullName);
        });
        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ApiContract.Catalog + "?os=windows")).StatusCode); // the start seeds the Thix apps

        // ThixFred as if resolved
        var db = test.NewContext();
        var fred = await db.Packages.SingleAsync(p => p.Key == "thixfred");
        db.Releases.Add(new PackageRelease
        {
            PackageId = fred.Id, Os = TargetOs.Windows, Architecture = CpuArchitecture.X64, Method = InstallMethod.ThixApp, Version = "0.1.42-20261119",
            InstallerUrl = "https://github.com/Real-TTX/ThixFred/releases/download/release-channel/ThixFredSetup.exe", InstallerKind = InstallerKind.Exe,
            SilentArguments = "--quiet", FileName = "ThixFredSetup.exe", ResolvedDate = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        async Task<IReadOnlyList<PackageDto>> CatalogAsync(bool feature)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiContract.Catalog + "?os=windows&arch=x64");
            if (feature)
            {
                request.Headers.Add(ApiContract.FeaturesHeader, ApiContract.ThixAppsFeature);
            }

            using var response = await client.SendAsync(request);
            return (await response.Content.ReadFromJsonAsync<CatalogResponse>(ApiContract.Json))!.Packages;
        }

        Assert.Contains(await CatalogAsync(feature: true), p => p.Key == "thixfred" && p.Install!.Method == InstallMethod.ThixApp);
        var older = await CatalogAsync(feature: false);
        Assert.DoesNotContain(older, p => p.Key == "thixfred");
        Assert.Contains(older, p => p.Key == "firefox");

        // an action for a Thix app reaches an older device without its plan (the job fails there with a message)
        var enroll = await client.PostAsJsonAsync(ApiContract.Enroll, new EnrollRequest { UserName = "anna", Password = "anna-password", Device = Samples.Windows() }, ApiContract.Json);
        var enrolled = (await enroll.Content.ReadFromJsonAsync<EnrollResponse>(ApiContract.Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", enrolled.Token);
        var pushed = await ActionService.PushAsync(test.NewContext(), new Actor(anna.Id, false), enrolled.Device.Id, new PushRequest(ActionKind.Install, fred.Id, null));
        Assert.True(pushed.Ok, pushed.Error);
        var open = (await client.GetFromJsonAsync<ActionsResponse>(ApiContract.Actions, ApiContract.Json))!;
        Assert.Null(Assert.Single(open.Actions).Package!.Install);
        client.DefaultRequestHeaders.Add(ApiContract.FeaturesHeader, ApiContract.ThixAppsFeature);
        open = (await client.GetFromJsonAsync<ActionsResponse>(ApiContract.Actions, ApiContract.Json))!;
        Assert.Equal(InstallMethod.ThixApp, Assert.Single(open.Actions).Package!.Install!.Method);
    }
}
