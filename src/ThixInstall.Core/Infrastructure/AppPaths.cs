namespace ThixInstall.Core.Infrastructure;

/// <summary>
/// Where ThixInstall keeps its files. Program files, the app's own settings (per user) and the agent's data are separate.
///   App (per user):   Windows %LocalAppData%\ThixInstall, macOS ~/Library/Application Support/ThixInstall
///   Agent:            Windows service %ProgramData%\ThixInstall (machine-wide: cloud connection, catalog, jobs, downloads);
///                     everywhere else the "agent" folder inside the app's folder (macOS, development, bundle runner)
/// </summary>
public static class AppPaths
{
    /// <summary>Developers can point ThixInstall to a separate folder (sample data, screenshots) with this variable.</summary>
    public const string DataDirectoryVariable = "THIXINSTALL_DATA_DIR";

    private static string? _dataDirectoryOverride = NullIfEmpty(Environment.GetEnvironmentVariable(DataDirectoryVariable));

    /// <summary>Set by the Windows service at start: the agent then keeps its data machine-wide.</summary>
    public static bool IsMachineAgent { get; internal set; }

    public static string DataDirectory => _dataDirectoryOverride ?? Path.Combine(UserFolder(), AppInfo.ProductName);

    /// <summary>The agent's folder (see the class summary).</summary>
    public static string AgentDirectory => IsMachineAgent && _dataDirectoryOverride is null && OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppInfo.ProductName)
        : Path.Combine(DataDirectory, "agent");

    /// <summary>The app's own settings (look, window).</summary>
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>The agent's settings (cloud connection, sync, updates).</summary>
    public static string AgentSettingsFile => Path.Combine(AgentDirectory, "agent.json");

    /// <summary>The last catalog of the cloud (used while it is not reachable).</summary>
    public static string CatalogFile => Path.Combine(AgentDirectory, "catalog.json");

    /// <summary>The last inventory of this device.</summary>
    public static string InventoryFile => Path.Combine(AgentDirectory, "inventory.json");

    /// <summary>The history of the jobs.</summary>
    public static string JobsFile => Path.Combine(AgentDirectory, "jobs.json");

    /// <summary>Installers while they are downloaded and installed (emptied after each job).</summary>
    public static string DownloadDirectory => Path.Combine(AgentDirectory, "downloads");

    /// <summary>Scripts while they run.</summary>
    public static string ScriptDirectory => Path.Combine(AgentDirectory, "scripts");

    public static string UpdateDirectory => Path.Combine(IsMachineAgent ? AgentDirectory : DataDirectory, "updates");

    public static string LogDirectory => Path.Combine(IsMachineAgent ? AgentDirectory : DataDirectory, "logs");

    /// <summary>True when a developer or a test pointed ThixInstall to its own folder: it is then its own instance.</summary>
    public static bool HasOverride => _dataDirectoryOverride is not null;

    /// <summary>Tests use a temporary folder.</summary>
    internal static void UseDirectoryForTests(string? directory) => _dataDirectoryOverride = directory;

    private static string UserFolder() => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
