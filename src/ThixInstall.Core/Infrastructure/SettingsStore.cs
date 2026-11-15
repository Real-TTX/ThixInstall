using ThixInstall.Core.Models;

namespace ThixInstall.Core.Infrastructure;

/// <summary>
/// The two settings files: the app's (settings.json, per user) and the agent's (agent.json). Only the business layer uses
/// this class. Records: a change is a new object through Update.
/// </summary>
internal static class SettingsStore
{
    private static readonly object Gate = new();
    private static AppSettings? _app;
    private static AgentSettings? _agent;

    public static AppSettings ReadApp()
    {
        lock (Gate)
        {
            return _app ??= JsonFile.Read<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();
        }
    }

    public static AppSettings UpdateApp(Func<AppSettings, AppSettings> change)
    {
        lock (Gate)
        {
            var settings = change(ReadApp());
            JsonFile.Write(AppPaths.SettingsFile, settings);
            _app = settings;
            return settings;
        }
    }

    public static AgentSettings ReadAgent()
    {
        lock (Gate)
        {
            return _agent ??= JsonFile.Read<AgentSettings>(AppPaths.AgentSettingsFile) ?? new AgentSettings();
        }
    }

    public static AgentSettings UpdateAgent(Func<AgentSettings, AgentSettings> change)
    {
        lock (Gate)
        {
            var settings = change(ReadAgent());
            JsonFile.Write(AppPaths.AgentSettingsFile, settings);
            _agent = settings;
            return settings;
        }
    }

    /// <summary>Drops the caches (tests, a new data folder).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _app = null;
            _agent = null;
        }
    }
}
