using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Models;

namespace ThixInstall.Core.Business;

/// <summary>
/// The app's own settings for this user (look, window, notifications). The agent's settings (cloud, sync, updates) go
/// through <see cref="AgentHost"/>. <see cref="Changed"/> tells the app what is new.
/// </summary>
public static class SettingsService
{
    /// <summary>Raised after every saved change (any thread).</summary>
    public static event EventHandler<AppSettings>? Changed;

    public static AppSettings Get() => SettingsStore.ReadApp();

    /// <summary>Changes the settings the user chooses (checked against the policy).</summary>
    public static AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        AccessPolicy.EnsureConfigurationEditable();
        return Save(change);
    }

    /// <summary>Things the app remembers by itself (window size, folded menu, catalog view): no configuration lock.</summary>
    public static AppSettings Remember(Func<AppSettings, AppSettings> change)
    {
        var current = SettingsStore.ReadApp();
        var next = change(current);
        return Save(_ => current with
        {
            WindowWidth = Math.Clamp(next.WindowWidth, 640, 4000),
            WindowHeight = Math.Clamp(next.WindowHeight, 420, 3000),
            IsSidebarCollapsed = next.IsSidebarCollapsed,
            CatalogAsTiles = next.CatalogAsTiles,
        });
    }

    private static AppSettings Save(Func<AppSettings, AppSettings> change)
    {
        var saved = SettingsStore.UpdateApp(change);
        Changed?.Invoke(null, saved);
        return saved;
    }
}
