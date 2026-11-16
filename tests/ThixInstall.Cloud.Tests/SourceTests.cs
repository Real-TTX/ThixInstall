using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ThixInstall.Cloud.Business;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Tests;

/// <summary>
/// The sources of "App hinzufügen" and the new kinds of sources: the search of every source (winget index, Store, Chocolatey, Homebrew), adding an
/// app from hits, the Microsoft Store, Chocolatey with a version, uploaded installers, direct downloads for macOS.
/// </summary>
[Collection(TestWorld.Collection)]
public sealed class SourceTests
{
    private const string StoreSearch = """
        {"Data":[{"PackageIdentifier":"9NKSQGP7F2NH","PackageName":"WhatsApp","Publisher":"WhatsApp Inc.",
          "Versions":[{"PackageVersion":"Unknown","PackageFamilyNames":["5319275A.WhatsAppDesktop_cv1g1gvanyjgm"]}]},
         {"PackageIdentifier":"XP99J3KP4XZ4VV","PackageName":"WhatsApp Business Tools","Publisher":"Somebody","Versions":[{"PackageVersion":"2.1"}]}]}
        """;

    private const string StoreManifest = """
        {"Data":{"PackageIdentifier":"9NKSQGP7F2NH","Versions":[{"PackageVersion":"Unknown",
          "DefaultLocale":{"PackageLocale":"en-us","Publisher":"WhatsApp Inc.","PublisherUrl":"http://whatsapp.com/","PackageName":"WhatsApp","ShortDescription":"Simple. Reliable. Private."},
          "Installers":[{"InstallerType":"msstore","PackageFamilyName":"5319275A.WhatsAppDesktop_cv1g1gvanyjgm"}]}]}}
        """;

    private const string ChocolateyFeed = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <feed xml:base="https://community.chocolatey.org/api/v2/" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata" xmlns="http://www.w3.org/2005/Atom">
          <entry>
            <title type="text">7zip.install</title><summary type="text">7-Zip (install)</summary><author><name>Igor Pavlov</name></author>
            <m:properties><d:Version>26.6.0</d:Version><d:Title>7-Zip (Install)</d:Title><d:DownloadCount m:type="Edm.Int32">100</d:DownloadCount></m:properties>
          </entry>
          <entry>
            <title type="text">7zip</title><summary type="text">7-Zip is a file archiver with a high compression ratio.</summary><author><name>Igor Pavlov</name></author>
            <m:properties><d:Version>26.6.0</d:Version><d:Title>7-Zip</d:Title><d:Tags>7zip zip archiver</d:Tags><d:IconUrl>https://icons.example.com/7zip.png</d:IconUrl>
              <d:ProjectUrl>http://www.7-zip.org/</d:ProjectUrl><d:DownloadCount m:type="Edm.Int32">35669613</d:DownloadCount></m:properties>
          </entry>
        </feed>
        """;

    private const string CaskList = """
        [{"token":"firefox","name":["Mozilla Firefox"],"desc":"Web browser","homepage":"https://www.mozilla.org/firefox/","version":"160.0"},
         {"token":"firefox@nightly","name":["Mozilla Firefox Nightly"],"desc":"Web browser","homepage":"https://www.mozilla.org/firefox/","version":"161.0a1"},
         {"token":"old-thing","name":["Old"],"desc":"gone","homepage":"https://old.example.com","version":"1","disabled":true}]
        """;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    // ---- the sources, no database -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheWingetIndexFindsThePackageAndFoldsItsLanguageVariants()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("thixinstall-winget-").FullName, "index.db");
        using (var connection = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE packages(rowid INTEGER PRIMARY KEY, id TEXT NOT NULL, name TEXT NOT NULL, moniker TEXT, latest_version TEXT NOT NULL, arp_min_version TEXT, arp_max_version TEXT, hash BLOB);
                CREATE TABLE tags2(rowid INTEGER PRIMARY KEY, tag TEXT NOT NULL);
                CREATE TABLE tags2_map(tag INT64 NOT NULL, package INT64 NOT NULL, PRIMARY KEY(tag, package)) WITHOUT ROWID;
                INSERT INTO packages(rowid, id, name, moniker, latest_version) VALUES
                  (1, 'Mozilla.Firefox', 'Mozilla Firefox (en-US)', 'firefox', '160.0'),
                  (2, 'Mozilla.Firefox.de', 'Mozilla Firefox (de)', 'firefox', '160.0'),
                  (3, 'filips.FirefoxPWA', 'PWAsForFirefox', 'firefoxpwa', '2.20.1'),
                  (4, 'Mozilla.Thunderbird', 'Mozilla Thunderbird (en-US)', 'thunderbird', '140.0'),
                  (5, 'Example.Browser', 'Some Browser', NULL, '1.0');
                INSERT INTO tags2(rowid, tag) VALUES (1, 'firefox');
                INSERT INTO tags2_map(tag, package) VALUES (1, 5);
                """;
            create.ExecuteNonQuery();
        }

        var hits = WingetIndex.Search(file, "firefox", 10);
        Assert.Equal("Mozilla.Firefox", hits[0].Id);
        Assert.Equal("Mozilla Firefox", hits[0].Name);
        Assert.Equal("Mozilla", hits[0].Publisher);
        Assert.Equal("firefox", hits[0].Moniker);
        Assert.DoesNotContain(hits, h => h.Id == "Mozilla.Firefox.de");
        Assert.Contains(hits, h => h.Id == "filips.FirefoxPWA");
        Assert.Contains(hits, h => h.Id == "Example.Browser"); // by its tag
        Assert.DoesNotContain(hits, h => h.Id == "Mozilla.Thunderbird");
        Assert.Empty(WingetIndex.Search(file, " ", 10));
    }

    [Fact]
    public void TheLocaleManifestOfWingetGivesPublisherDescriptionAndHomepage()
    {
        var hit = new SourceHit(SourceKind.Winget, "Mozilla.Firefox", "Mozilla Firefox", "Mozilla", null, null, "160.0");
        var details = WingetIndex.FromLocale(hit, """
            PackageIdentifier: Mozilla.Firefox
            PackageVersion: "160.0"
            PackageLocale: en-US
            Publisher: Mozilla
            PackageName: Mozilla Firefox (en-US)
            PackageUrl: https://www.mozilla.org/firefox/
            ShortDescription: Mozilla Firefox is free and open source software.
            Tags:
            - browser
            - web
            """);
        Assert.Equal("Mozilla Firefox", details.Name);
        Assert.Equal("https://www.mozilla.org/firefox/", details.Homepage);
        Assert.Equal("Mozilla Firefox is free and open source software.", details.Description);
        Assert.Equal("Browser", SourceSearch.GuessCategory(details));
    }

    [Fact]
    public void TheStoreAnswersAreRead()
    {
        var hits = StoreSource.ParseSearch(StoreSearch, "whatsapp");
        Assert.Equal("9NKSQGP7F2NH", hits[0].Id);
        Assert.Null(hits[0].Version); // "Unknown"
        Assert.Equal("5319275A.WhatsAppDesktop_cv1g1gvanyjgm", hits[0].PackageFamilyName);
        Assert.Equal("2.1", hits[1].Version);

        var manifest = StoreSource.ParseManifest(StoreManifest, "9NKSQGP7F2NH")!;
        Assert.Equal("WhatsApp", manifest.Name);
        Assert.Equal("http://whatsapp.com/", manifest.Homepage);
        Assert.Equal("5319275A.WhatsAppDesktop_cv1g1gvanyjgm", manifest.PackageFamilyName);
        Assert.Null(manifest.Version);
        Assert.False(StoreSource.IsValidId("9NK-../x"));
    }

    [Fact]
    public void TheChocolateyFeedIsReadAndTheExactIdComesFirst()
    {
        var hits = ChocolateySource.Parse(ChocolateyFeed, "7zip");
        Assert.Equal("7zip", hits[0].Id);
        Assert.Equal("7-Zip", hits[0].Name);
        Assert.Equal("26.6.0", hits[0].Version);
        Assert.Equal("https://icons.example.com/7zip.png", hits[0].IconUrl);
        Assert.Equal("http://www.7-zip.org/", hits[0].Homepage);
        Assert.Equal("Igor Pavlov", hits[0].Publisher);
        Assert.Throws<ResolveException>(() => ChocolateySource.Parse("<no xml", "x"));
    }

    [Fact]
    public void TheHomebrewListIsCompactAndSearched()
    {
        var entries = BrewIndex.ParseList(new MemoryStream(Encoding.UTF8.GetBytes(CaskList)), cask: true);
        Assert.Equal(["firefox", "firefox@nightly"], entries.Select(e => e.Token));
        var hits = BrewIndex.Search(entries, "firefox", true, 5);
        Assert.Equal("firefox", hits[0].Id);
        Assert.Equal(SourceKind.Cask, hits[0].Kind);
        Assert.Equal("Mozilla Firefox", hits[0].Name);

        var formulae = BrewIndex.ParseList(new MemoryStream("""[{"name":"git","desc":"Distributed revision control system","homepage":"https://git-scm.com","versions":{"stable":"2.47.1"}}]"""u8.ToArray()), cask: false);
        Assert.Equal("2.47.1", Assert.Single(formulae).Version);
    }

    [Fact]
    public void ChoicesNamesAndMatchesAreRead()
    {
        Assert.Equal((SourceKind.Winget, "Mozilla.Firefox"), SourceSearch.ParsePick("Winget:Mozilla.Firefox"));
        Assert.Equal((SourceKind.Cask, "firefox@nightly"), SourceSearch.ParsePick("Cask:firefox@nightly"));
        Assert.Null(SourceSearch.ParsePick("Winget:no id"));
        Assert.Null(SourceSearch.ParsePick("Nothing:x"));
        Assert.Null(SourceSearch.ParsePick(""));
        Assert.Equal("Mozilla Firefox", SourceSearch.CleanName("Mozilla Firefox (en-US)"));
        Assert.Equal("Firefox (MSIX)", SourceSearch.CleanName("Firefox (MSIX)"));
        Assert.Equal("IrfanView", SourceSearch.CleanName("IrfanView (Install)"));
        Assert.True(SourceSearch.Score("git", "Git", "git", null, "Git.Git") < SourceSearch.Score("git", "GitHub Desktop", null, "GitHubDesktop", "GitHub.GitHubDesktop"));

        var winget = new SourceHit(SourceKind.Winget, "Mozilla.Firefox", "Mozilla Firefox", "Mozilla", null, "https://www.mozilla.org/firefox/", "160.0") { Moniker = "firefox" };
        var mac = new[]
        {
            new SourceHit(SourceKind.Formula, "firefoxpwa", "firefoxpwa", null, null, null, "1") { Score = 2 },
            new SourceHit(SourceKind.Cask, "firefox", "Mozilla Firefox", null, null, "https://www.mozilla.org/firefox/", "160.0") { Score = 0 },
        };
        Assert.Equal("firefox", SourceSearch.BestMatch(winget, mac)!.Id);
    }

    [Fact]
    public void InstallersAreKnownByTheirEnding()
    {
        Assert.Equal(InstallerKind.Msi, PackageUploads.KindOf("Setup.MSI", TargetOs.Windows));
        Assert.Equal(InstallerKind.Msix, PackageUploads.KindOf("app.msixbundle", TargetOs.Windows));
        Assert.Null(PackageUploads.KindOf("app.dmg", TargetOs.Windows));
        Assert.Equal(InstallerKind.Pkg, PackageUploads.KindOf("Tool.pkg", TargetOs.MacOS));
        Assert.Null(PackageUploads.KindOf("tool.exe", TargetOs.MacOS));
        Assert.Equal(new string('a', 64), PackageUploads.ShaOf("upload:" + new string('a', 64)));
        Assert.Null(PackageUploads.ShaOf("https://example.com/x"));
    }

    // ---- with the database ------------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task TheSearchAsksEverySourceMarksWhatIsThereAndSurvivesASourceThatFails()
    {
        await using var test = await TestDatabase.CreateAsync();
        var admin = await test.AddUserAsync("admin", admin: true);
        var actor = new Actor(admin.Id, true);
        UseSources(
            winget: [new SourceHit(SourceKind.Winget, "Mozilla.Firefox", "Mozilla Firefox", "Mozilla", null, null, "160.0") { Moniker = "firefox", Score = 0, Tags = "browser" }],
            store: null,
            casks: [new SourceHit(SourceKind.Cask, "firefox", "Mozilla Firefox", null, "Web browser", null, "160.0") { Score = 0 }]);
        try
        {
            var result = await SourceSearch.SearchAsync(test.NewContext(), actor, "firefox");
            Assert.Equal("Winget:Mozilla.Firefox", result.SuggestedWindows);
            Assert.Equal("Cask:firefox", result.SuggestedMac);
            Assert.Equal("Browser", result.SuggestedCategory);
            var problem = Assert.Single(result.Problems);
            Assert.Equal(SourceKind.Store, problem.Kind);

            test.Db.Packages.Add(new Data.Package { Key = "firefox", Name = "Firefox", Category = "Browser", WindowsMethod = InstallMethod.Winget, WingetId = "mozilla.firefox" });
            await test.Db.SaveChangesAsync();
            var again = await SourceSearch.SearchAsync(test.NewContext(), actor, "firefox");
            Assert.NotNull(again.Windows[0].ExistingPackageId);
            Assert.Null(again.SuggestedWindows);

            await Assert.ThrowsAsync<NotAllowedException>(() => SourceSearch.SearchAsync(test.NewContext(), new Actor(admin.Id, false), "firefox"));
        }
        finally
        {
            ResetSources();
        }
    }

    [DbFact]
    public async Task AnAppIsAddedFromTwoHitsWithIconSourcesAndVersions()
    {
        await using var test = await TestDatabase.CreateAsync();
        var admin = await test.AddUserAsync("admin", admin: true);
        var actor = new Actor(admin.Id, true);
        var world = new FakeWorld(r => r.RequestUri!.AbsoluteUri switch
        {
            var u when u.StartsWith(ChocolateySource.ApiBase + "/Packages()", StringComparison.Ordinal) && u.Contains("7zip", StringComparison.Ordinal)
                => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ChocolateyFeed) },
            "https://icons.example.com/7zip.png" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) },
            var u when u == BrewResolver.ApiBase + "/formula/p7zip.json"
                => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"name":"p7zip","versions":{"stable":"17.06"}}""") },
            _ => null,
        });
        Net.UseHandlerForTests(world);
        SourceSearch.Details = (kind, id, ct) => Task.FromResult<SourceHit?>(kind switch
        {
            SourceKind.Chocolatey => ChocolateySource.Parse(ChocolateyFeed, id).First(h => h.Id == id),
            SourceKind.Formula => new SourceHit(SourceKind.Formula, id, id, null, "7-Zip (high compression file archiver) implementation", "https://github.com/p7zip-project/p7zip", "17.06"),
            _ => null,
        });
        try
        {
            var noCategory = await SourceSearch.AddAsync(test.NewContext(), actor, new QuickAddInput("Chocolatey:7zip", "Formula:p7zip", ""));
            Assert.Equal("Category", noCategory.Field);
            var wrongSystem = await SourceSearch.AddAsync(test.NewContext(), actor, new QuickAddInput("Formula:p7zip", null, "Werkzeuge"));
            Assert.False(wrongSystem.Ok);

            var added = await SourceSearch.AddAsync(test.NewContext(), actor, new QuickAddInput("Chocolatey:7zip", "Formula:p7zip", "Werkzeuge"));
            Assert.True(added.Ok, added.Error);
            var package = await test.NewContext().Packages.SingleAsync(p => p.Id == added.Value.Id);
            Assert.Equal("7zip", package.Key);
            Assert.Equal("7-Zip", package.Name);
            Assert.Equal("Igor Pavlov", package.Publisher);
            Assert.Equal("http://www.7-zip.org/", package.Homepage);
            Assert.Equal(InstallMethod.Chocolatey, package.WindowsMethod);
            Assert.Equal("7zip", package.ChocoId);
            Assert.Equal(InstallMethod.Homebrew, package.MacMethod);
            Assert.Equal("p7zip", package.BrewName);
            Assert.True(await test.NewContext().PackageIcons.AnyAsync(i => i.PackageId == package.Id));

            // Chocolatey now has a version: devices see updates
            var catalog = await CatalogService.CatalogAsync(test.NewContext(), null, TargetOs.Windows, CpuArchitecture.X64);
            var dto = Assert.Single(catalog.Packages);
            Assert.Equal("26.6.0", dto.LatestVersion);
            Assert.Equal(InstallMethod.Chocolatey, dto.Install!.Method);
            Assert.Equal("7zip", dto.Install.PackageId);
            Assert.Equal("17.06", Assert.Single((await CatalogService.CatalogAsync(test.NewContext(), null, TargetOs.MacOS, CpuArchitecture.Arm64)).Packages).LatestVersion);

            var twice = await SourceSearch.AddAsync(test.NewContext(), actor, new QuickAddInput("Chocolatey:7zip", null, "Werkzeuge"));
            Assert.False(twice.Ok);
            Assert.Contains("schon", twice.Error, StringComparison.Ordinal);
        }
        finally
        {
            ResetSources();
            Net.UseHandlerForTests(TestDatabase.Offline);
        }
    }

    [DbFact]
    public async Task AStoreAppIsInstalledByItsIdAndRecognisedByItsPackageFamily()
    {
        await using var test = await TestDatabase.CreateAsync();
        var admin = await test.AddUserAsync("admin", admin: true);
        var actor = new Actor(admin.Id, true);
        Net.UseHandlerForTests(new FakeWorld(r => r.RequestUri!.AbsoluteUri.StartsWith(StoreSource.ApiBase + "/packageManifests/9NKSQGP7F2NH", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(StoreManifest) }
            : null));
        try
        {
            var id = (await CatalogService.SaveGeneralAsync(test.NewContext(), actor, new PackageInput { Name = "WhatsApp", Category = "Kommunikation" })).Value;
            var invalid = await CatalogService.SaveWindowsAsync(test.NewContext(), actor, new PackageInput { Id = id, WindowsMethod = InstallMethod.MicrosoftStore, StoreId = "no" });
            Assert.Equal("StoreId", invalid.Field);
            var saved = await CatalogService.SaveWindowsAsync(test.NewContext(), actor, new PackageInput { Id = id, WindowsMethod = InstallMethod.MicrosoftStore, StoreId = "9nksqgp7f2nh" });
            Assert.True(saved.Ok, saved.Error);
            Assert.DoesNotContain("Windows:", saved.Value, StringComparison.Ordinal);

            var dto = Assert.Single((await CatalogService.CatalogAsync(test.NewContext(), null, TargetOs.Windows, CpuArchitecture.X64)).Packages);
            Assert.Equal(InstallMethod.MicrosoftStore, dto.Install!.Method);
            Assert.Equal("9NKSQGP7F2NH", dto.Install.PackageId);
            Assert.Null(dto.Install.DownloadUrl);
            Assert.Null(dto.LatestVersion);
            Assert.Equal("5319275A.WhatsAppDesktop_cv1g1gvanyjgm", dto.Detection!.PackageFamilyName);
        }
        finally
        {
            Net.UseHandlerForTests(TestDatabase.Offline);
        }
    }

    [DbFact]
    public async Task AnUploadedInstallerIsKeptOutsideTheCacheAndReplacedCleanly()
    {
        await using var test = await TestDatabase.CreateAsync();
        var admin = await test.AddUserAsync("admin", admin: true);
        var actor = new Actor(admin.Id, true);
        PackageUploads.UseDirectory(Directory.CreateTempSubdirectory("thixinstall-uploads-").FullName);
        var id = (await CatalogService.SaveGeneralAsync(test.NewContext(), actor, new PackageInput { Name = "Firmen-Tool", Category = "Werkzeuge" })).Value;
        static UploadFile File(string name, byte[] bytes) => new(new MemoryStream(bytes), name, bytes.Length);
        byte[] msi = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 1, 2, 3];

        var missing = await CatalogService.SaveWindowsAsync(test.NewContext(), actor, new PackageInput { Id = id, WindowsMethod = InstallMethod.Upload });
        Assert.Equal("WindowsFile", missing.Field);
        var fake = await CatalogService.SaveWindowsAsync(test.NewContext(), actor, new PackageInput { Id = id, WindowsMethod = InstallMethod.Upload }, File("tool.msi", "MZ not an msi"u8.ToArray()));
        Assert.Equal("WindowsFile", fake.Field);
        var wrongType = await CatalogService.SaveWindowsAsync(test.NewContext(), actor, new PackageInput { Id = id, WindowsMethod = InstallMethod.Upload }, File("tool.dmg", msi));
        Assert.Equal("WindowsFile", wrongType.Field);

        var saved = await CatalogService.SaveWindowsAsync(test.NewContext(), actor, new PackageInput { Id = id, WindowsMethod = InstallMethod.Upload, DirectVersion = "1.0" }, File("tool.msi", msi));
        Assert.True(saved.Ok, saved.Error);
        var dto = Assert.Single((await CatalogService.CatalogAsync(test.NewContext(), null, TargetOs.Windows, CpuArchitecture.X64)).Packages);
        Assert.Equal(InstallMethod.Upload, dto.Install!.Method);
        Assert.Equal(InstallerKind.Msi, dto.Install.InstallerKind);
        Assert.Equal("/qn /norestart", dto.Install.SilentArguments);
        Assert.Equal("1.0", dto.LatestVersion);
        Assert.Equal("tool.msi", dto.Install.FileName);
        var releaseId = long.Parse(dto.Install.DownloadUrl!.Split('/').Last(), System.Globalization.CultureInfo.InvariantCulture);
        var sent = await DownloadCache.ReleaseAsync(test.NewContext(), releaseId, CancellationToken.None);
        Assert.True(sent.Ok, sent.Error);
        Assert.Equal(msi, await System.IO.File.ReadAllBytesAsync(sent.Value!.Path));
        Assert.Equal(0, await test.NewContext().CacheFiles.CountAsync());
        var firstPath = sent.Value.Path;

        // a new file replaces the old one, which goes; the version tells the devices
        byte[] newer = [.. msi, 4, 5];
        await CatalogService.SaveWindowsAsync(test.NewContext(), actor, new PackageInput { Id = id, WindowsMethod = InstallMethod.Upload, DirectVersion = "1.1" }, File("tool-1.1.msi", newer));
        Assert.False(System.IO.File.Exists(firstPath));
        Assert.Equal("1.1", Assert.Single((await CatalogService.CatalogAsync(test.NewContext(), null, TargetOs.Windows, CpuArchitecture.X64)).Packages).LatestVersion);

        // macOS: a pkg needs no app name, a dmg does
        byte[] pkg = [(byte)'x', (byte)'a', (byte)'r', (byte)'!', 0, 1, 2, 3];
        var dmgWithoutName = await CatalogService.SaveMacAsync(test.NewContext(), actor, new PackageInput { Id = id, MacMethod = InstallMethod.Upload }, File("tool.dmg", pkg));
        Assert.Equal("MacAppBundleName", dmgWithoutName.Field);
        var mac = await CatalogService.SaveMacAsync(test.NewContext(), actor, new PackageInput { Id = id, MacMethod = InstallMethod.Upload, MacDirectVersion = "1.1" }, File("tool.pkg", pkg));
        Assert.True(mac.Ok, mac.Error);
        var macPlan = Assert.Single((await CatalogService.CatalogAsync(test.NewContext(), null, TargetOs.MacOS, CpuArchitecture.Arm64)).Packages).Install!;
        Assert.Equal(InstallerKind.Pkg, macPlan.InstallerKind);
        Assert.NotNull(macPlan.DownloadUrl);

        // another source: the upload goes; deleting the app removes the other one
        var newerPath = PackageUploads.PathOf((await test.NewContext().Packages.SingleAsync()).WindowsUploadSha256!);
        await CatalogService.SaveWindowsAsync(test.NewContext(), actor, new PackageInput { Id = id, WindowsMethod = InstallMethod.Chocolatey, ChocoId = "firmen-tool" });
        Assert.False(System.IO.File.Exists(newerPath));
        var pkgPath = PackageUploads.PathOf((await test.NewContext().Packages.SingleAsync()).MacUploadSha256!);
        Assert.True(System.IO.File.Exists(pkgPath));
        await CatalogService.DeletePackageAsync(test.NewContext(), actor, id);
        Assert.False(System.IO.File.Exists(pkgPath));
    }

    [DbFact]
    public async Task AMacDirectDownloadNeedsADiskImagePackageOrZip()
    {
        await using var test = await TestDatabase.CreateAsync();
        var admin = await test.AddUserAsync("admin", admin: true);
        var actor = new Actor(admin.Id, true);
        var id = (await CatalogService.SaveGeneralAsync(test.NewContext(), actor, new PackageInput { Name = "Tool", Category = "Werkzeuge" })).Value;

        var noKind = await CatalogService.SaveMacAsync(test.NewContext(), actor, new PackageInput { Id = id, MacMethod = InstallMethod.Direct, MacDirectUrl = "https://example.com/download?os=mac", MacAppBundleName = "Tool.app" });
        Assert.Equal("MacDirectUrl", noKind.Field);
        var noName = await CatalogService.SaveMacAsync(test.NewContext(), actor, new PackageInput { Id = id, MacMethod = InstallMethod.Direct, MacDirectUrl = "https://example.com/Tool.dmg" });
        Assert.Equal("MacAppBundleName", noName.Field);
        var saved = await CatalogService.SaveMacAsync(test.NewContext(), actor, new PackageInput
        {
            Id = id, MacMethod = InstallMethod.Direct, MacDirectUrl = "https://example.com/Tool-2.0.dmg", MacAppBundleName = "Tool.app", MacDirectVersion = "2.0",
        });
        Assert.True(saved.Ok, saved.Error);
        var dto = Assert.Single((await CatalogService.CatalogAsync(test.NewContext(), null, TargetOs.MacOS, CpuArchitecture.X64)).Packages);
        Assert.Equal(InstallMethod.Direct, dto.Install!.Method);
        Assert.Equal(InstallerKind.Dmg, dto.Install.InstallerKind);
        Assert.Equal("Tool.app", dto.Install.AppBundleName);
        Assert.Equal("Tool.app", dto.Detection!.AppBundleName);
        Assert.Equal("2.0", dto.LatestVersion);
    }

    private static void UseSources(IReadOnlyList<SourceHit>? winget = null, IReadOnlyList<SourceHit>? store = null, IReadOnlyList<SourceHit>? choco = null,
        IReadOnlyList<SourceHit>? casks = null, IReadOnlyList<SourceHit>? formulae = null)
    {
        static Func<string, CancellationToken, Task<IReadOnlyList<SourceHit>>> Answer(IReadOnlyList<SourceHit>? hits)
            => (_, _) => hits is null ? throw new ResolveException("Nicht erreichbar.") : Task.FromResult(hits);
        SourceSearch.Forget();
        SourceSearch.SearchWinget = Answer(winget);
        SourceSearch.SearchStore = Answer(store);
        SourceSearch.SearchChocolatey = Answer(choco ?? []);
        SourceSearch.SearchCasks = Answer(casks ?? []);
        SourceSearch.SearchFormulae = Answer(formulae ?? []);
    }

    private static void ResetSources()
    {
        SourceSearch.Forget();
        SourceSearch.SearchWinget = (q, ct) => WingetIndex.SearchAsync(q, 8, ct);
        SourceSearch.SearchStore = (q, ct) => StoreSource.SearchAsync(q, 6, ct);
        SourceSearch.SearchChocolatey = (q, ct) => ChocolateySource.SearchAsync(q, 6, ct);
        SourceSearch.SearchCasks = (q, ct) => BrewIndex.SearchAsync(q, true, 6, ct);
        SourceSearch.SearchFormulae = (q, ct) => BrewIndex.SearchAsync(q, false, 6, ct);
        SourceSearch.Details = SourceSearch.DefaultDetails;
    }
}
