using ThixInstall.Core.Business;
using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Installers;
using ThixInstall.Core.Models;
using ThixInstall.Shared;

namespace ThixInstall.Tests;

/// <summary>
/// The agent against a real ThixInstall.Cloud (e.g. the local stack): enroll, catalog with resolved plans, one installer
/// through the download cache (checked, never run), a report, leaving. Runs only with THIXINSTALL_CLOUD_URL,
/// THIXINSTALL_CLOUD_USER and THIXINSTALL_CLOUD_PASSWORD; otherwise it passes without doing anything.
/// </summary>
public sealed class CloudEndToEndTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("THIXINSTALL_CLOUD_URL");
    private static readonly string? User = Environment.GetEnvironmentVariable("THIXINSTALL_CLOUD_USER");
    private static readonly string? Password = Environment.GetEnvironmentVariable("THIXINSTALL_CLOUD_PASSWORD");

    [Fact]
    public async Task A_device_enrolls_reads_the_catalog_downloads_through_the_cache_and_leaves()
    {
        if (Url is null || User is null || Password is null)
        {
            return;
        }

        using var data = new TestData();
        var info = await CloudService.ConnectAsync(new ConnectRequest(Url, User, Password, null, "E2E-Testgerät"), startLoop: false);
        Assert.Equal("E2E-Testgerät", info.Name);
        Assert.True(info.SyncEnabled);

        await CloudService.RefreshCatalogAsync();
        var catalog = CatalogService.Current;
        Assert.NotNull(catalog);
        Assert.NotEmpty(catalog.Packages);

        // A package the cloud resolved: its installer comes from the cache and matches its checksum.
        var cached = catalog.Packages.Where(package => package.Install?.DownloadUrl is not null)
            .OrderBy(package => package.Install!.Size ?? long.MaxValue)
            .FirstOrDefault();
        if (cached is not null)
        {
            var context = new JobContext(Url, CloudService.Token, (_, _) => { }, CancellationToken.None);
            var file = await Downloads.FetchAsync(cached.Install!, context);
            try
            {
                Assert.True(new FileInfo(file).Length > 0);
            }
            finally
            {
                Downloads.Remove(file);
            }
        }

        // The inventory and a report of the device reach the cloud.
        InventoryService.Refresh();
        await CloudService.ReportInventoryAsync(CancellationToken.None);
        var job = JobService.NewJob(ActionKind.RunScript, ActionOrigin.Device, title: "E2E-Bericht") with
        {
            State = ActionState.Succeeded,
            StartedAt = DateTimeOffset.Now.AddSeconds(-2),
            FinishedAt = DateTimeOffset.Now,
            Output = "ok",
        };
        await CloudService.ReportJobAsync(job);

        await CloudService.DisconnectAsync();
        Assert.False(CloudService.Connection.IsConnected);
    }
}
