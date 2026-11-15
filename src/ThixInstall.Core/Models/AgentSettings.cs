namespace ThixInstall.Core.Models;

/// <summary>
/// What the agent keeps (agent.json in <see cref="Infrastructure.AppPaths.AgentDirectory"/>): the connection to the cloud and
/// how it handles updates. On an installed Windows computer the service owns it (machine-wide), on macOS the app.
/// Only the agent's business classes write it.
/// </summary>
public sealed record AgentSettings
{
    public CloudConnection Cloud { get; init; } = new();

    /// <summary>
    /// "Sync": the device reports its inventory and runs the actions the cloud sends. Off: it only reads the catalog.
    /// </summary>
    public bool SyncEnabled { get; init; } = true;

    /// <summary>Install newer versions of catalog apps by itself (once a day).</summary>
    public bool AutoUpdateApps { get; init; }

    /// <summary>Look for new versions of ThixInstall itself.</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Full auto update of ThixInstall: install new versions without asking.</summary>
    public bool InstallUpdatesAutomatically { get; init; } = true;

    /// <summary>When the apps were last updated automatically.</summary>
    public DateTimeOffset? LastAutoUpdate { get; init; }
}

/// <summary>The connection to ThixInstall.Cloud. The device token is stored protected (DPAPI / Keychain), not in here.</summary>
public sealed record CloudConnection
{
    /// <summary>The address of the cloud, e.g. https://install.example.com. Empty = not connected.</summary>
    public string ServerUrl { get; init; } = "";

    public string UserName { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public string DeviceName { get; init; } = "";

    public long DeviceId { get; init; }

    public string? ProfileName { get; init; }

    /// <summary>The device joined with a profile's key or a bundle: only an administrator may disconnect it.</summary>
    public bool IsManaged { get; init; }

    /// <summary>The highest action revision this device has seen.</summary>
    public long LastActionRevision { get; init; }

    /// <summary>The device token, protected (Windows DPAPI); on macOS it lives in the Keychain.</summary>
    public string ProtectedToken { get; init; } = "";

    public bool IsConnected => ServerUrl.Length > 0 && DeviceId > 0;
}
