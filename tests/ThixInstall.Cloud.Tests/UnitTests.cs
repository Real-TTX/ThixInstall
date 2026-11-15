using System.IO.Compression;
using System.Text;
using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Web;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Tests;

/// <summary>What needs no database: the winget and Homebrew mappings, the bundle files, texts, icons, the permission checks of the business layer.</summary>
public sealed class UnitTests
{
    private const string FirefoxManifest = """
        # Created with komac
        PackageIdentifier: Mozilla.Firefox
        PackageVersion: 133.0.3
        InstallerType: nullsoft
        Scope: machine
        InstallerSwitches:
          Custom: /PreventRebootRequired=true
        UpgradeBehavior: install
        Installers:
        - Architecture: x86
          InstallerUrl: https://download-installer.cdn.mozilla.net/pub/firefox/releases/133.0.3/win32/en-US/Firefox%20Setup%20133.0.3.exe
          InstallerSha256: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
        - Architecture: x64
          InstallerUrl: https://download-installer.cdn.mozilla.net/pub/firefox/releases/133.0.3/win64/en-US/Firefox%20Setup%20133.0.3.exe
          InstallerSha256: BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB
        - Architecture: arm64
          InstallerUrl: https://download-installer.cdn.mozilla.net/pub/firefox/releases/133.0.3/win64-aarch64/en-US/Firefox%20Setup%20133.0.3.exe
          InstallerSha256: CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC
        ManifestType: installer
        ManifestVersion: 1.9.0
        """;

    [Fact]
    public void WingetPicksTheInstallerOfTheArchitectureWithWingetsSilentSwitches()
    {
        var x64 = WingetResolver.FromManifest(FirefoxManifest, "Mozilla.Firefox", "1", CpuArchitecture.X64);
        Assert.Equal("133.0.3", x64.Version);
        Assert.Equal(InstallMethod.Winget, x64.Method);
        Assert.Contains("/win64/", x64.InstallerUrl);
        Assert.Equal(new string('b', 64), x64.Sha256);
        Assert.Equal(InstallerKind.Exe, x64.Kind);
        Assert.Equal("/S /PreventRebootRequired=true", x64.SilentArguments);
        Assert.Equal("Firefox Setup 133.0.3.exe", x64.FileName);

        var arm = WingetResolver.FromManifest(FirefoxManifest, "Mozilla.Firefox", "1", CpuArchitecture.Arm64);
        Assert.Contains("aarch64", arm.InstallerUrl);
    }

    [Fact]
    public void WingetPrefersMachineScopeAndFallsBackToX64OnArmAndToTheCommandLineForWhatItCannotRunSilently()
    {
        const string yaml = """
            PackageIdentifier: Example.Tool
            PackageVersion: 2.1.0
            Installers:
            - Architecture: x64
              InstallerType: inno
              Scope: user
              InstallerUrl: https://example.com/tool-user.exe
              InstallerSha256: 1111111111111111111111111111111111111111111111111111111111111111
            - Architecture: x64
              InstallerType: wix
              Scope: machine
              ProductCode: '{11111111-2222-3333-4444-555555555555}'
              InstallerUrl: https://example.com/tool.msi
              InstallerSha256: 2222222222222222222222222222222222222222222222222222222222222222
            """;
        var x64 = WingetResolver.FromManifest(yaml, "Example.Tool", "1", CpuArchitecture.X64);
        Assert.Equal(InstallerKind.Msi, x64.Kind);
        Assert.Equal("/qn /norestart", x64.SilentArguments);
        Assert.Equal("{11111111-2222-3333-4444-555555555555}", x64.ProductCode);
        Assert.Equal("tool.msi", x64.FileName);

        // no arm64 installer: the x64 one runs in emulation
        Assert.Equal(InstallerKind.Msi, WingetResolver.FromManifest(yaml, "Example.Tool", "1", CpuArchitecture.Arm64).Kind);

        const string exeWithoutSwitch = """
            PackageIdentifier: Example.Exe
            PackageVersion: 1.0
            InstallerType: exe
            Installers:
            - Architecture: x64
              InstallerUrl: https://example.com/setup.exe
              InstallerSha256: 3333333333333333333333333333333333333333333333333333333333333333
            """;
        var fallback = WingetResolver.FromManifest(exeWithoutSwitch, "Example.Exe", "1", CpuArchitecture.X64);
        Assert.Null(fallback.InstallerUrl);
        Assert.Equal("1.0", fallback.Version);
        Assert.NotNull(fallback.Note);

        var zip = WingetResolver.FromManifest(exeWithoutSwitch.Replace("InstallerType: exe", "InstallerType: zip"), "Example.Exe", "1", CpuArchitecture.X64);
        Assert.Null(zip.InstallerUrl);
    }

    [Theory]
    [InlineData("msi", null, null, "/qn /norestart")]
    [InlineData("wix", null, "ALLUSERS=1", "/qn /norestart ALLUSERS=1")]
    [InlineData("nullsoft", null, null, "/S")]
    [InlineData("burn", null, null, "/quiet /norestart")]
    [InlineData("exe", "--silent", null, "--silent")]
    [InlineData("nullsoft", "/SILENT", null, "/SILENT")]
    public void SilentSwitchesFollowWinget(string type, string? silent, string? custom, string expected)
        => Assert.Equal(expected, WingetResolver.Map(type, silent, custom, null)!.Value.Arguments);

    [Fact]
    public void InnoForAllUsersAndUnknownTypesAreNotRun()
    {
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /ALLUSERS", WingetResolver.Map("inno", null, null, "machine")!.Value.Arguments);
        Assert.Null(WingetResolver.Map("exe", null, null, null));
        Assert.Null(WingetResolver.Map("portable", null, null, null));
        Assert.Equal(InstallerKind.Msix, WingetResolver.Map("msix", null, null, null)!.Value.Kind);
    }

    [Theory]
    [InlineData("Mozilla.Firefox", "manifests/m/Mozilla/Firefox")]
    [InlineData("Python.Python.3.13", "manifests/p/Python/Python/3/13")]
    [InlineData("7zip.7zip", "manifests/7/7zip/7zip")]
    [InlineData("Notepad++.Notepad++", "manifests/n/Notepad++/Notepad++")]
    [InlineData("nopublisher", null)]
    [InlineData("../etc.passwd", null)]
    public void TheManifestFolderComesFromTheId(string id, string? path) => Assert.Equal(path, WingetResolver.ManifestPath(id));

    [Fact]
    public void TheNewestVersionFolderWinsAndOtherPackagesAreIgnored()
        => Assert.Equal("133.0.3", WingetResolver.NewestVersion(["132.0", "133.0.3", "133.0", "ESR", "de", "99.0.1"]));

    private const string FirefoxCask = """
        {"token":"firefox","version":"133.0.3","url":"https://download-installer.cdn.mozilla.net/pub/firefox/releases/133.0.3/mac/en-US/Firefox%20133.0.3.dmg",
         "sha256":"no_check","artifacts":[{"uninstall":[{"quit":"org.mozilla.firefox"}]},{"app":["Firefox.app"]},{"zap":[{"trash":["~/Library/Caches/Firefox"]}]}]}
        """;

    [Fact]
    public void ACaskWithOneAppInADmgIsInstalledByThixInstallFromTheCache()
    {
        var release = BrewResolver.FromCask(FirefoxCask, "firefox", CpuArchitecture.Arm64);
        Assert.Equal(InstallMethod.HomebrewCask, release.Method);
        Assert.Equal("133.0.3", release.Version);
        Assert.Equal(InstallerKind.Dmg, release.Kind);
        Assert.Equal("Firefox.app", release.AppBundleName);
        Assert.Equal("Firefox 133.0.3.dmg", release.FileName);
        Assert.Null(release.Sha256); // "no_check"
    }

    [Fact]
    public void IntelMacsTakeTheDownloadOfTheirVariation()
    {
        const string cask = """
            {"token":"tool","version":"4.2,1234","url":"https://example.com/tool-arm64.zip","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
             "artifacts":[{"app":["Tool.app"]}],
             "variations":{"arm64_ventura":{"url":"https://example.com/tool-arm64-old.zip"},"sonoma":{"url":"https://example.com/tool-intel.zip","sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}}
            """;
        var arm = BrewResolver.FromCask(cask, "tool", CpuArchitecture.Arm64);
        var intel = BrewResolver.FromCask(cask, "tool", CpuArchitecture.X64);
        Assert.Equal("4.2", arm.Version);
        Assert.Equal("https://example.com/tool-arm64.zip", arm.InstallerUrl);
        Assert.Equal(InstallerKind.Zip, arm.Kind);
        Assert.Equal("https://example.com/tool-intel.zip", intel.InstallerUrl);
        Assert.Equal(new string('b', 64), intel.Sha256);
    }

    [Fact]
    public void WhatACaskInstallsBesidesAnAppIsLeftToBrew()
    {
        // a link of the app's program into the command line is brew's convenience: the app is still installed from the cache
        const string binary = """{"token":"x","version":"1","url":"https://example.com/x.dmg","sha256":"no_check","artifacts":[{"app":["X.app"],"target":"/Applications/X.app"},{"binary":["$APPDIR/X.app/Contents/MacOS/x"]}]}""";
        Assert.Equal(InstallerKind.Dmg, BrewResolver.FromCask(binary, "x", CpuArchitecture.Arm64).Kind);

        // an installer script, a suite or a download without a file type: brew knows how
        const string script = """{"token":"s","version":"1","url":"https://example.com/s.dmg","sha256":"no_check","artifacts":[{"installer":[{"script":"install.sh"}]}]}""";
        Assert.Null(BrewResolver.FromCask(script, "s", CpuArchitecture.Arm64).InstallerUrl);
        const string noType = """{"token":"n","version":"1","url":"https://update.example.com/1/darwin-arm64/stable","sha256":"no_check","artifacts":[{"app":["N.app"]}]}""";
        Assert.Null(BrewResolver.FromCask(noType, "n", CpuArchitecture.Arm64).InstallerUrl);

        const string pkg = """{"token":"y","version":"2","url":"https://example.com/y.pkg","sha256":"no_check","artifacts":[{"pkg":["y.pkg"]}]}""";
        Assert.Equal(InstallerKind.Pkg, BrewResolver.FromCask(pkg, "y", CpuArchitecture.Arm64).Kind);

        const string formula = """{"name":"git","versions":{"stable":"2.47.1"}}""";
        var git = BrewResolver.FromFormula(formula, "git");
        Assert.Equal(InstallMethod.Homebrew, git.Method);
        Assert.Equal("2.47.1", git.Version);
        Assert.Null(git.InstallerUrl);
    }

    [Fact]
    public void TheMacBundleZipKeepsTheExecutableBitAndCarriesTheTicket()
    {
        var folder = Directory.CreateTempSubdirectory("thixinstall-zip-").FullName;
        var app = Path.Combine(folder, "app.zip");
        using (var zip = ZipFile.Open(app, ZipArchiveMode.Create))
        {
            zip.CreateEntry("ThixInstall.app/");
            var program = zip.CreateEntry("ThixInstall.app/Contents/MacOS/ThixInstall");
            program.ExternalAttributes = Convert.ToInt32("100755", 8) << 16;
            using var writer = new StreamWriter(program.Open());
            writer.Write("binary");
        }

        var target = Path.Combine(folder, "bundle.zip");
        var ticket = new BundleTicket("https://cloud.example.com", "0123456789abcdef0123456789abcdef");
        BundleService.BuildMacZip(app, target, "ThixInstall-buero.thixinstall", ticket);

        using (var built = ZipFile.OpenRead(target))
        {
            var copied = built.GetEntry("ThixInstall.app/Contents/MacOS/ThixInstall")!;
            Assert.Equal(Convert.ToInt32("100755", 8), (copied.ExternalAttributes >> 16) & 0xFFFF);
            using var reader = new StreamReader(built.GetEntry("ThixInstall-buero.thixinstall")!.Open());
            Assert.Equal(ticket, BundleTicket.FromJson(reader.ReadToEnd()));
        }

        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void TheTicketAppendedToAProgramIsFoundAgain()
    {
        var program = Encoding.ASCII.GetBytes("MZ...the program...");
        var ticket = new BundleTicket("http://localhost:4955", "0123456789abcdef0123456789abcdef");
        using var stream = new MemoryStream([.. program, .. ticket.ToTrailer()]);
        Assert.Equal(ticket, BundleTicket.ReadTrailer(stream));
        Assert.Equal(program.Length, BundleTicket.ProgramLength(stream));
        Assert.Null(BundleTicket.ReadTrailer(new MemoryStream(program)));
    }

    [Theory]
    [InlineData("Visual Studio Code", "visual-studio-code")]
    [InlineData("Büro & Grüße", "buero-gruesse")]
    [InlineData("Notepad++", "notepadplusplus")]
    [InlineData("  ", "")]
    public void SlugsAreLowerCaseAscii(string text, string slug) => Assert.Equal(slug, Text.Slug(text));

    [Fact]
    public void IconsAreRecognisedByTheirBytesAndAScriptInAnSvgIsRefused()
    {
        Assert.Equal("image/png", CatalogService.IconType([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.Equal("image/x-icon", CatalogService.IconType([0, 0, 1, 0, 1, 0, 16, 16]));
        Assert.Equal("image/svg+xml", CatalogService.IconType(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'></svg>")));
        Assert.Null(CatalogService.IconType(Encoding.UTF8.GetBytes("<svg><script>alert(1)</script></svg>")));
        Assert.Null(CatalogService.IconType(Encoding.UTF8.GetBytes("<html>not an icon</html>")));
    }

    [Fact]
    public async Task OnlyAdministratorsKeepTheCatalogAndTheSettings()
    {
        var user = new Actor(5, IsAdmin: false);
        // the business layer refuses before it touches the database
        await Assert.ThrowsAsync<NotAllowedException>(() => CatalogService.ListPackagesAsync(null!, user, new PackageQuery(null, null, null, null, "name", false, 1, 10)));
        await Assert.ThrowsAsync<NotAllowedException>(() => CatalogService.SaveGeneralAsync(null!, user, new PackageInput { Name = "X", Category = "Y" }));
        await Assert.ThrowsAsync<NotAllowedException>(() => ProfileService.SaveAsync(null!, user, null, "Büro", null));
        await Assert.ThrowsAsync<NotAllowedException>(() => SettingService.SaveGeneralAsync(null!, user, true, 10));
        await Assert.ThrowsAsync<NotAllowedException>(() => DownloadCache.ClearAsync(null!, user));
        await Assert.ThrowsAsync<NotAllowedException>(() => UserService.ListAsync(null!, user, new UserQuery(null, null, null, "name", false, 1, 10)));
        Assert.Throws<NotAllowedException>(() => AgentService.RemoveUpload(user, "win-x64"));
    }

    [Fact]
    public void LetterTilesKeepTheirColour() => Assert.Equal(Ui.TileColour("firefox"), Ui.TileColour("firefox"));

    [Fact]
    public void TheContractJsonUsesCamelCaseAndEnumNames()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new InstallPlan { Method = InstallMethod.HomebrewCask, InstallerKind = InstallerKind.Dmg }, ApiContract.Json);
        Assert.Equal("""{"method":"HomebrewCask","installerKind":"Dmg"}""", json);
    }
}
