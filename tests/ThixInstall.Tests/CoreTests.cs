using ThixInstall.Core.Business;
using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Installers;
using ThixInstall.Core.Models;
using ThixInstall.Core.Platform;
using ThixInstall.Shared;

namespace ThixInstall.Tests;

/// <summary>The rules of the business layer that need no cloud: versions, tickets, recognising apps, the catalog view.</summary>
public sealed class CoreTests
{
    [Theory]
    [InlineData("133.0", "132.0.2", 1)]
    [InlineData("1.95.3", "1.95.3.0", 0)]
    [InlineData("24.09", "24.08", 1)]
    [InlineData("2.3.4-beta", "2.3.4", -1)]
    [InlineData("v3.0.21", "3.0.20", 1)]
    [InlineData("10.0", "9.9.9", 1)]
    [InlineData(null, "1.0", -1)]
    public void Versions_compare_number_by_number(string? left, string? right, int expected) =>
        Assert.Equal(expected, Math.Sign(AppVersion.Compare(left, right)));

    [Fact]
    public void An_update_needs_two_known_versions()
    {
        Assert.True(AppVersion.IsNewer("133.0", "132.0"));
        Assert.False(AppVersion.IsNewer("133.0", "133.0"));
        Assert.False(AppVersion.IsNewer("133.0", null));
        Assert.False(AppVersion.IsNewer(null, "1.0"));
        Assert.False(AppVersion.IsNewer("133.0", "latest"));
    }

    [Fact]
    public void A_ticket_appended_to_a_program_is_found_and_stripped_again()
    {
        var ticket = new BundleTicket("https://install.example.com", "Token1234ABCD");
        var program = Enumerable.Range(0, 5000).Select(index => (byte)index).ToArray();
        using var stream = new MemoryStream([.. program, .. ticket.ToTrailer()]);
        Assert.Equal(ticket, BundleTicket.ReadTrailer(stream));
        Assert.Equal(program.Length, BundleTicket.ProgramLength(stream));

        using var plain = new MemoryStream(program);
        Assert.Null(BundleTicket.ReadTrailer(plain));
        Assert.Equal(program.Length, BundleTicket.ProgramLength(plain));
    }

    [Fact]
    public async Task The_whole_bundle_configuration_is_packed_into_the_program()
    {
        using var data = new TestData();
        var catalog = SampleCatalog.Create();
        var bundle = new BundleDto("Token1234ABCD", "Büro-PC", AppInfo.Os, IncludesAgent: true, CanEnroll: true, "Büro",
            catalog.Packages.Take(3).ToList(), catalog.Scripts, DateTimeOffset.Now);
        var ticket = new BundleTicket("https://unreachable.invalid", "Token1234ABCD", bundle);
        var program = Path.Combine(data.Folder, "ThixInstall-Buero-PC.exe");
        File.WriteAllBytes(program, [.. new byte[2048], .. ticket.ToTrailer()]);

        var read = BundleTicket.ReadTrailer(program);
        Assert.NotNull(read?.Bundle);
        Assert.Equal(["firefox", "chrome", "7zip"], read.Bundle.Packages.Select(package => package.Key));

        // No network needed: the configuration comes from the program itself.
        var loaded = await BundleService.LoadAsync(read);
        Assert.Equal("Büro-PC", loaded.Name);
        var steps = BundleService.PlanSteps(read, loaded);
        Assert.Equal(3 + catalog.Scripts.Count + 1, steps.Count);
        Assert.Equal("ThixInstall installieren (Profil „Büro“)", steps[^1].Title);
    }

    [Theory]
    [InlineData("ftp://install.example.com", "Token1234")]
    [InlineData("https://install.example.com", "short")]
    [InlineData("https://install.example.com", "with/slash1234")]
    public void Odd_tickets_are_refused(string url, string token) => Assert.False(BundleTicket.IsValid(new BundleTicket(url, token)));

    [Fact]
    public void Installing_strips_the_ticket_from_the_copied_program()
    {
        using var data = new TestData();
        var source = Path.Combine(data.Folder, "bundle.exe");
        var target = Path.Combine(data.Folder, "installed.exe");
        File.WriteAllBytes(source, [.. new byte[3000], .. new BundleTicket("https://install.example.com", "Token1234ABCD").ToTrailer()]);
        if (OperatingSystem.IsWindows())
        {
            InstallService.CopyProgram(source, target);
            Assert.Equal(3000, new FileInfo(target).Length);
            Assert.Null(BundleTicket.ReadTrailer(target));
        }
    }

    [Fact]
    public void Apps_are_recognised_by_name_prefix_pattern_product_code_and_bundle()
    {
        var firefox = new InstalledEntry("Mozilla Firefox (x64 de)", "133.0", "Mozilla", "registry", "Mozilla Firefox 133.0 (x64 de)");
        var msi = new InstalledEntry("Something else", "1.0", null, "registry", "{12345678-1234-1234-1234-123456789ABC}");
        var app = new InstalledEntry("Firefox", "133.0", null, "app", "/Applications/Firefox.app");
        var brew = new InstalledEntry("git", "2.47.1", null, "brew", "git");

        Assert.True(InventoryService.Matches(new DetectionRule { DisplayName = "Mozilla Firefox" }, firefox));
        Assert.True(InventoryService.Matches(new DetectionRule { DisplayName = "^Mozilla Firefox \\(x64" }, firefox));
        Assert.False(InventoryService.Matches(new DetectionRule { DisplayName = "Firefox Nightly" }, firefox));
        Assert.True(InventoryService.Matches(new DetectionRule { ProductCode = "{12345678-1234-1234-1234-123456789abc}" }, msi));
        Assert.True(InventoryService.Matches(new DetectionRule { AppBundleName = "Firefox.app" }, app));
        Assert.True(InventoryService.Matches(new DetectionRule { BrewName = "git" }, brew));
        Assert.False(InventoryService.Matches(new DetectionRule { DisplayName = "[" + "unclosed" }, firefox));
    }

    [Fact]
    public void The_catalog_view_shows_installed_apps_and_updates()
    {
        using var data = new TestData();
        CatalogService.Store(SampleCatalog.Create());
        if (OperatingSystem.IsMacOS())
        {
            data.Install("Firefox", "132.0", "/Applications/Firefox.app", "app");
            data.Install("7-Zip", "24.09", "/Applications/7-Zip.app", "app");
        }
        else
        {
            data.Install("Firefox (x64 de)", "132.0");
            data.Install("7-Zip 24.09 (x64)", "24.09");
        }

        var snapshot = CatalogService.Snapshot(new HashSet<long>());
        var firefox = snapshot.Apps.Single(app => app.Package.Key == "firefox");
        var sevenZip = snapshot.Apps.Single(app => app.Package.Key == "7zip");
        Assert.True(firefox is { IsInstalled: true, IsUpdateAvailable: true, InstalledVersion: "132.0" });
        Assert.True(sevenZip is { IsInstalled: true, IsUpdateAvailable: false });
        Assert.False(snapshot.Apps.Single(app => app.Package.Key == "git").IsInstalled);
        Assert.Equal(["firefox"], CatalogService.AppsWithUpdates().Select(package => package.Key));
        Assert.Equal(2, snapshot.Scripts.Count);
    }

    [Theory]
    [InlineData("Firefox Setup 133.0.exe", InstallerKind.Exe, "FirefoxSetup133.0.exe")]
    [InlineData("../../evil", InstallerKind.Msi, "evil.msi")]
    [InlineData(null, InstallerKind.Dmg, "installer.dmg")]
    [InlineData("app.msixbundle", InstallerKind.Msix, "app.msixbundle")]
    public void Download_names_are_safe(string? name, InstallerKind kind, string expected) =>
        Assert.Equal(expected, Downloads.SafeFileName(name, kind));

    [Fact]
    public void Server_addresses_get_https_and_lose_the_slash()
    {
        Assert.Equal("https://install.example.com", CloudService.ValidateUrl("install.example.com/"));
        Assert.Equal("http://localhost:4955", CloudService.ValidateUrl("http://localhost:4955"));
        Assert.Throws<BusinessException>(() => CloudService.ValidateUrl(""));
        Assert.Throws<BusinessException>(() => CloudService.ValidateUrl("ftp://x"));
    }

    [Fact]
    public async Task The_process_runner_keeps_the_output_and_the_exit_code()
    {
        var (program, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "echo hallo & exit 3" })
            : ("/bin/sh", new[] { "-c", "echo hallo; exit 3" });
        var result = await ProcessRunner.RunAsync(program, arguments, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("hallo", result.Output);

        var (sleep, sleepArguments) = OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 30" })
            : ("/bin/sh", new[] { "-c", "sleep 30" });
        var stopped = await ProcessRunner.RunAsync(sleep, sleepArguments, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.True(stopped.TimedOut);
        Assert.Null(stopped.ExitCode);
    }
}
