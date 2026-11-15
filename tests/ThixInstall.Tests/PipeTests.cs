using ThixInstall.Core.Agent;
using ThixInstall.Core.Business;
using ThixInstall.Core.Models;
using ThixInstall.Shared;

namespace ThixInstall.Tests;

/// <summary>
/// The way of the app to the Windows service: every request and answer as one JSON line over the named pipe, errors of the
/// business layer with their message and field. Windows only; the pipe has a name of its own (an installed service keeps its).
/// </summary>
public sealed class PipeTests
{
    [Fact]
    public async Task Requests_and_errors_travel_through_the_pipe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var data = new TestData();
        CatalogService.Store(SampleCatalog.Create());
        data.Install("Firefox (x64 de)", "132.0");
        var previous = AgentProtocol.PipeName;
        AgentProtocol.PipeName = $"ThixInstall.Tests.{Guid.NewGuid():N}";
        using var stop = new CancellationTokenSource();
        var server = AgentPipeServer.RunAsync(stop.Token);
        try
        {
            AgentClient.UsePipe();
            var status = await AgentClient.GetStatusAsync();
            Assert.Equal(Core.Infrastructure.AppInfo.Version, status.AgentVersion);
            Assert.Equal(1, status.UpdatesAvailable);

            var catalog = await AgentClient.GetCatalogAsync();
            Assert.Equal(8, catalog.Apps.Count);
            Assert.True(catalog.Apps.Single(app => app.Package.Key == "firefox") is { IsUpdateAvailable: true, Package.Install.Method: InstallMethod.Winget });

            var job = await AgentClient.EnqueueAsync(new JobRequest(ActionKind.Install, PackageId: 3));
            Assert.Equal("7-Zip installieren", job.Title);
            var jobs = await AgentClient.GetJobsAsync();
            Assert.Contains(jobs, entry => entry.Id == job.Id && entry.State == ActionState.Pending);
            await AgentClient.CancelAsync(job.Id);

            var error = await Assert.ThrowsAsync<BusinessException>(() => AgentClient.ConnectAsync(new ConnectRequest("", "anna", "x", null, null)));
            Assert.Equal("ServerUrl", error.Field);
            Assert.Null(await AgentClient.ClaimUserJobAsync());
        }
        catch (Exception exception)
        {
            var log = Directory.Exists(Core.Infrastructure.AppPaths.LogDirectory)
                ? string.Join(Environment.NewLine, Directory.GetFiles(Core.Infrastructure.AppPaths.LogDirectory).Select(File.ReadAllText))
                : "(no log)";
            throw new InvalidOperationException($"{exception.Message} | LOG: {log} | SERVER: {server.Status} {server.Exception}", exception);
        }
        finally
        {
            stop.Cancel();
            AgentClient.UseInProcess();
            AgentProtocol.PipeName = previous;
            await Task.WhenAny(server, Task.Delay(2000));
        }
    }
}
