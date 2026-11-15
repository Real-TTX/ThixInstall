using System.Runtime.Versioning;
using System.ServiceProcess;
using ThixInstall.Core.Business;
using ThixInstall.Core.Infrastructure;

namespace ThixInstall.Core.Agent;

/// <summary>
/// "ThixInstall.exe --service": the agent as the Windows service "ThixInstall Agent" (LocalSystem). It installs without
/// UAC prompts, keeps the device connected whether anybody is signed in or not, and serves the apps of the users through
/// the pipe. "--agent-console" runs the same in a console window (development: the pipe without installing the service).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentWindowsService : ServiceBase
{
    private CancellationTokenSource? _stop;
    private Task? _pipe;

    public AgentWindowsService()
    {
        ServiceName = InstallService.ServiceName;
        CanStop = true;
        CanShutdown = true;
    }

    /// <summary>Runs the service (blocks until the service manager stops it).</summary>
    public static void RunAsService() => Run(new AgentWindowsService());

    /// <summary>Development: the agent with its pipe in this process, until Enter is pressed.</summary>
    public static void RunInConsole()
    {
        var service = new AgentWindowsService();
        service.OnStart([]);
        Console.WriteLine("ThixInstall-Agent läuft (Pipe „ThixInstall.Agent“). Enter beendet.");
        Console.ReadLine();
        service.OnStop();
    }

    protected override void OnStart(string[] args)
    {
        AppPaths.IsMachineAgent = true;
        Directory.CreateDirectory(AppPaths.AgentDirectory);
        InstallService.RemoveOldExecutables();
        AgentHost.Start(asService: true);
        _stop = new CancellationTokenSource();
        _pipe = AgentPipeServer.RunAsync(_stop.Token);
    }

    protected override void OnStop()
    {
        _stop?.Cancel();
        AgentHost.Stop();
        try
        {
            _pipe?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }
    }

    protected override void OnShutdown() => OnStop();
}
