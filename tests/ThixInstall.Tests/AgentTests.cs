using ThixInstall.Core.Business;
using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Models;
using ThixInstall.Shared;

namespace ThixInstall.Tests;

/// <summary>The agent against a cloud in memory: enrolling, the catalog, actions pushed from the cloud, permissions.</summary>
public sealed class AgentTests
{
    private static readonly AgentCaller User = new("anna", IsAdministrator: false);
    private static readonly AgentCaller Administrator = new("admin", IsAdministrator: true);

    [Fact]
    public async Task Connecting_stores_the_token_and_loads_the_catalog()
    {
        using var data = new TestData();
        var cloud = new FakeCloud();
        CloudClient.HandlerForTests = () => cloud;

        var refused = await Assert.ThrowsAsync<BusinessException>(() => CloudService.ConnectAsync(new ConnectRequest(FakeCloud.Url, "anna", "falsch", null, "Testgerät")));
        Assert.Equal("Password", refused.Field);

        var info = await CloudService.ConnectAsync(new ConnectRequest(FakeCloud.Url, "anna", "geheim", null, "Testgerät"));
        Assert.Equal("Anna Albers", info.UserDisplayName);
        Assert.Equal("Testgerät", cloud.Facts?.Name);
        Assert.Equal(AppInfo.Os, cloud.Facts?.Os);
        Assert.True(CloudService.Connection is { IsConnected: true, IsManaged: false, UserName: "anna" });
        Assert.Equal("device-token-1", CloudService.Token);

        await WaitUntil(() => CatalogService.Current is not null && cloud.Inventory is not null);
        Assert.Equal(8, CatalogService.Current!.Packages.Count);
        Assert.Equal(CloudState.Online, CloudService.State);

        await CloudService.DisconnectAsync();
        Assert.True(cloud.LoggedOut);
        Assert.False(CloudService.Connection.IsConnected);
        Assert.Null(CatalogService.Current);
    }

    [Fact]
    public async Task An_enrollment_key_makes_a_managed_device_that_only_an_administrator_disconnects()
    {
        using var data = new TestData();
        var cloud = new FakeCloud();
        CloudClient.HandlerForTests = () => cloud;
        await CloudService.ConnectAsync(new ConnectRequest(FakeCloud.Url, null, null, "KEY-123", null, EnableSync: false));
        Assert.True(CloudService.Connection.IsManaged);
        Assert.True(cloud.SyncEnabled, "enrolling with a key always switches sync on");
        Assert.Equal("Büro", CloudService.Connection.ProfileName);

        await Assert.ThrowsAsync<BusinessException>(() => AgentHost.DisconnectAsync(User));
        await Assert.ThrowsAsync<BusinessException>(() => AgentHost.ChangeSettingsAsync(new AgentSettingsChange(SyncEnabled: false), User));
        await AgentHost.DisconnectAsync(Administrator);
        Assert.False(CloudService.Connection.IsConnected);
    }

    [Fact]
    public async Task An_action_of_the_cloud_runs_and_its_result_goes_back()
    {
        using var data = new TestData();
        var cloud = new FakeCloud();
        CloudClient.HandlerForTests = () => cloud;
        JobService.Start();
        var script = SampleCatalog.Script(30, "Hallo sagen", ScriptContext.User, OperatingSystem.IsWindows() ? "Write-Output 'Hallo aus der Cloud'" : "echo 'Hallo aus der Cloud'");
        cloud.Push(ActionKind.RunScript, script: script);

        await CloudService.ConnectAsync(new ConnectRequest(FakeCloud.Url, "anna", "geheim", null, "Testgerät"));
        await WaitUntil(() => cloud.Finished.ContainsKey(100), seconds: 30);

        var result = cloud.Finished[100];
        Assert.True(result.Succeeded, result.Output);
        Assert.Contains("Hallo aus der Cloud", result.Output);
        var job = JobService.All().Single(entry => entry.CloudActionId == 100);
        Assert.Equal(ActionOrigin.Push, job.Origin);
        Assert.Equal(ActionState.Succeeded, job.State);
        Assert.True(File.Exists(AppPaths.JobsFile));
    }

    [Fact]
    public async Task A_job_of_the_device_is_reported_and_a_failing_script_fails()
    {
        using var data = new TestData();
        var cloud = new FakeCloud();
        CloudClient.HandlerForTests = () => cloud;
        await CloudService.ConnectAsync(new ConnectRequest(FakeCloud.Url, "anna", "geheim", null, "Testgerät"));
        await WaitUntil(() => CatalogService.Current is not null);
        JobService.Start();

        var good = AgentHost.Enqueue(new JobRequest(ActionKind.RunScript, ScriptId: 20), User);
        await WaitUntil(() => JobService.All().Any(job => job.Id == good.Id && job.IsFinished), seconds: 30);
        Assert.Equal(ActionState.Succeeded, JobService.All().First(job => job.Id == good.Id).State);
        await WaitUntil(() => !cloud.Reports.IsEmpty);
        Assert.Contains(cloud.Reports, report => report.ScriptId == 20 && report.Result.Succeeded && report.Origin == ActionOrigin.Device);

        var failing = JobService.Enqueue(JobService.NewJob(ActionKind.RunScript, ActionOrigin.Device,
            script: SampleCatalog.Script(31, "Fehler", ScriptContext.User, OperatingSystem.IsWindows() ? "exit 7" : "exit 7")));
        await WaitUntil(() => JobService.All().Any(job => job.Id == failing.Id && job.IsFinished), seconds: 30);
        var failed = JobService.All().First(job => job.Id == failing.Id);
        Assert.Equal(ActionState.Failed, failed.State);
        Assert.Equal(7, failed.ExitCode);
    }

    [Fact]
    public async Task Policies_and_the_catalog_decide_what_may_be_queued()
    {
        using var data = new TestData();
        CatalogService.Store(SampleCatalog.Create());

        Assert.Throws<BusinessException>(() => AgentHost.Enqueue(new JobRequest(ActionKind.Install, PackageId: 999), User));

        AccessPolicy.UsePolicyReaderForTests(name => name == "DisableUserInstalls");
        Assert.Throws<BusinessException>(() => AgentHost.Enqueue(new JobRequest(ActionKind.Install, PackageId: 1), User));
        var allowed = AgentHost.Enqueue(new JobRequest(ActionKind.Install, PackageId: 1), Administrator);
        Assert.Equal("Firefox installieren", allowed.Title);
        Assert.True(JobService.Cancel(allowed.Id));
        await WaitUntil(() => JobService.All().Any(job => job.Id == allowed.Id && job.State == ActionState.Cancelled));

        AccessPolicy.UsePolicyReaderForTests(name => name == "DisableScripts");
        Assert.Throws<BusinessException>(() => AgentHost.Enqueue(new JobRequest(ActionKind.RunScript, ScriptId: 20), Administrator));

        AccessPolicy.UsePolicyReaderForTests(name => name == "LockConfiguration");
        await Assert.ThrowsAsync<BusinessException>(() => AgentHost.ConnectAsync(new ConnectRequest(FakeCloud.Url, "anna", "geheim", null, null), Administrator));
    }

    [Fact]
    public async Task Update_all_queues_one_update_per_newer_app()
    {
        using var data = new TestData();
        CatalogService.Store(SampleCatalog.Create());
        if (OperatingSystem.IsMacOS())
        {
            data.Install("Firefox", "120.0", "/Applications/Firefox.app", "app");
            data.Install("Git", "2.40.0", "/Applications/Git.app", "app");
        }
        else
        {
            data.Install("Firefox", "120.0");
            data.Install("Git", "2.40.0");
        }

        var queued = new List<JobRecord>();
        JobService.Changed += Record;
        try
        {
            // Without a worker the queue stays as it is: UpdateAll runs right here.
            var job = JobService.NewJob(ActionKind.UpdateAll, ActionOrigin.Device);
            var done = await JobService.RunNowAsync(job, null, CancellationToken.None);
            Assert.Equal(ActionState.Succeeded, done.State);
            Assert.Equal(["Firefox aktualisieren", "Git aktualisieren"], queued.Where(entry => entry.Kind == ActionKind.Update).Select(entry => entry.Title).Order());
        }
        finally
        {
            JobService.Changed -= Record;
        }

        void Record(object? sender, JobRecord job) => queued.Add(job);
    }

    private static async Task WaitUntil(Func<bool> condition, int seconds = 10)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(seconds))
            {
                Assert.Fail("Timed out waiting for the condition.");
            }

            await Task.Delay(50);
        }
    }
}
