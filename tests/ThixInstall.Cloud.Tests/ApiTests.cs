using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ThixInstall.Cloud.Business;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Tests;

/// <summary>The API over HTTP, the whole app in memory against a real PostgreSQL (THIXINSTALL_TEST_DB).</summary>
public sealed class ApiTests
{
    private static WebApplicationFactory<Program> App(TestDatabase test, string dataDirectory) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseEnvironment("Testing");
        b.UseSetting("ConnectionStrings:Default", test.ConnectionString);
        b.UseSetting("DataDirectory", dataDirectory);
    });

    [DbFact]
    public async Task ADeviceEnrollsReportsItsInventoryAndRunsWhatItIsSent()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna", password: "anna-password");
        var data = Directory.CreateTempSubdirectory("thixinstall-test-").FullName;
        await using var app = App(test, data);
        var client = app.CreateClient();

        // the catalog is public (the start of the app wrote the seed catalog); the protocol is named in every answer
        var catalog = await client.GetAsync(ApiContract.Catalog + "?os=windows&arch=x64");
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        Assert.Equal("1", catalog.Headers.GetValues(ApiContract.ProtocolHeader).Single());
        var packages = (await catalog.Content.ReadFromJsonAsync<CatalogResponse>(ApiContract.Json))!.Packages;
        Assert.Contains(packages, p => p.Key == "firefox" && p.Install!.Method == InstallMethod.Winget && p.Install.PackageId == "Mozilla.Firefox");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(ApiContract.Catalog)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(ApiContract.Device)).StatusCode);
        var wrong = await client.PostAsJsonAsync(ApiContract.Enroll, new EnrollRequest { UserName = "anna", Password = "nope", Device = Samples.Windows() }, ApiContract.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.False(string.IsNullOrEmpty((await wrong.Content.ReadFromJsonAsync<ApiError>(ApiContract.Json))!.Message));

        var enroll = await client.PostAsJsonAsync(ApiContract.Enroll, new EnrollRequest { UserName = "anna", Password = "anna-password", Device = Samples.Windows() }, ApiContract.Json);
        Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);
        var enrolled = (await enroll.Content.ReadFromJsonAsync<EnrollResponse>(ApiContract.Json))!;
        Assert.Equal("anna", enrolled.Device.UserName);
        Assert.True(enrolled.Device.SyncEnabled);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", enrolled.Token);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ThixInstall/0.1.7-20261115");

        var firefox = packages.First(p => p.Key == "firefox");
        var inventory = new InventoryReport([new InstalledApp("Mozilla Firefox (x64 de)", "120.0", "Mozilla", firefox.Id, "registry"), new InstalledApp("Something", null, null, null, "registry")], DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(ApiContract.Inventory, inventory, ApiContract.Json)).StatusCode);

        var wait = client.GetFromJsonAsync<WaitResult>(ApiContract.ActionsWait + "?since=0&timeout=20", ApiContract.Json);
        var pushed = await ActionService.PushAsync(test.NewContext(), new Actor(anna.Id, false), enrolled.Device.Id, new PushRequest(ActionKind.Update, firefox.Id, null));
        Assert.True(pushed.Ok, pushed.Error);
        Assert.True((await wait.WaitAsync(TimeSpan.FromSeconds(10)))!.Latest > 0);

        var open = (await client.GetFromJsonAsync<ActionsResponse>(ApiContract.Actions, ApiContract.Json))!;
        var action = Assert.Single(open.Actions);
        Assert.Equal(ActionKind.Update, action.Kind);
        Assert.Equal("Mozilla Firefox", action.Package!.Detection!.DisplayName);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(ApiContract.ActionStart(action.Id), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(ApiContract.ActionStart(action.Id), null)).StatusCode);
        var now = DateTimeOffset.UtcNow;
        var finish = await client.PostAsJsonAsync(ApiContract.ActionFinish(action.Id), new ActionResult(false, 1603, "Fehler 1603", null, now, now), ApiContract.Json);
        Assert.Equal(HttpStatusCode.NoContent, finish.StatusCode);

        var report = await client.PostAsJsonAsync(ApiContract.ActionReport,
            new LocalRunReport(ActionKind.Install, ActionOrigin.Device, "7-Zip installieren", null, null, new ActionResult(true, 0, null, "24.08", now, now)), ApiContract.Json);
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        var history = await ActionService.ListAsync(test.NewContext(), new Actor(anna.Id, false), new ActionQuery(null, null, null, enrolled.Device.Id, "created", true, 1, 10));
        Assert.Equal([ActionState.Succeeded, ActionState.Failed], history.Items.Select(i => i.State));

        // the device turns Sync off: its inventory is no longer taken
        var update = await client.PutAsJsonAsync(ApiContract.Device, new DeviceUpdate(SyncEnabled: false), ApiContract.Json);
        Assert.False((await update.Content.ReadFromJsonAsync<DeviceInfo>(ApiContract.Json))!.SyncEnabled);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(ApiContract.Inventory, inventory, ApiContract.Json)).StatusCode);

        // signing out ends the token
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(ApiContract.DeviceLogout, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(ApiContract.Device)).StatusCode);
    }

    [DbFact]
    public async Task TheWindowsDownloadOfABundleIsTheSetupWithTheTicketAppended()
    {
        await using var test = await TestDatabase.CreateAsync();
        var admin = await test.AddUserAsync("admin", admin: true);
        var data = Directory.CreateTempSubdirectory("thixinstall-test-").FullName;
        await using var app = App(test, data);
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _ = await client.GetAsync("/health");

        // an uploaded setup that already carries an old ticket: the old one is left out
        var setup = "MZ the ThixInstall setup"u8.ToArray();
        var old = new BundleTicket("https://old.example.com", "00000000000000000000000000000000").ToTrailer();
        await using (var upload = new MemoryStream([.. setup, .. old]))
        {
            Assert.True((await AgentService.UploadAsync(new Actor(admin.Id, true), "win-x64", upload, upload.Length)).Ok);
        }

        var vlc = await test.AddPackageAsync("vlc-test");
        var token = (await BundleService.CreateAsync(test.NewContext(), null, new BundleRequest(TargetOs.Windows, [vlc.Id], [], "Büro PC", false, null))).Value!;

        var page = await client.GetAsync(ApiContract.BundlePage(token));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Büro PC", await page.Content.ReadAsStringAsync());

        var download = await client.GetAsync(ApiContract.BundleDownload(token) + "?platform=win-x64");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("ThixInstall-buero-pc.exe", download.Content.Headers.ContentDisposition!.FileName);
        var bytes = await download.Content.ReadAsByteArrayAsync();
        var ticket = BundleTicket.ReadTrailer(new MemoryStream(bytes))!;
        Assert.Equal("http://localhost", ticket.CloudUrl);
        Assert.Equal(token, ticket.Token);
        Assert.Equal(setup, bytes[..setup.Length]);
        Assert.Equal(setup.Length, BundleTicket.ProgramLength(new MemoryStream(bytes)));

        // the program carries the whole configuration: the same bundle the API answers for its architecture
        var bundle = await client.GetFromJsonAsync<BundleDto>(ApiContract.Bundle(token) + "?arch=x64", ApiContract.Json);
        Assert.Equal(["vlc-test"], bundle!.Packages.Select(p => p.Key));
        Assert.NotNull(ticket.Bundle);
        Assert.Equal(bundle.Packages.Count, ticket.Bundle!.Packages.Count);
        Assert.Equal(bundle.Packages.Select(p => p.Install), ticket.Bundle.Packages.Select(p => p.Install));
        Assert.Equal("vlc-test", ticket.Bundle.Packages[0].Install!.PackageId);
        Assert.Equal("Büro PC", ticket.Bundle.Name);

        // a Mac download of a Windows bundle is refused with a message on the page
        var mac = await client.GetAsync(ApiContract.BundleDownload(token) + "?platform=osx-arm64");
        Assert.Equal(HttpStatusCode.Redirect, mac.StatusCode);
        Assert.Contains("error=", mac.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(ApiContract.Bundle("ffffffffffffffffffffffffffffffff"))).StatusCode);
    }

    [DbFact]
    public async Task TheStartPageShowsTheCatalogAndMakesABundle()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.AddUserAsync("admin", admin: true);
        var data = Directory.CreateTempSubdirectory("thixinstall-test-").FullName;
        await using var app = App(test, data);
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var start = await client.GetAsync("/?os=macos");
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var html = await start.Content.ReadAsStringAsync();
        Assert.Contains("Firefox", html);
        Assert.Contains("Dock automatisch ausblenden", html);
        Assert.DoesNotContain("Notepad++", html); // Windows only

        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Devices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/robots.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/llms.txt")).StatusCode);
    }
}
