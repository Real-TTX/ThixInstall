namespace ThixInstall.Core.Models;

public enum ThemeChoice
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>
/// The settings of the app for this user (settings.json in the user's data folder): look and window. What the agent
/// does (cloud, sync, updates) is <see cref="AgentSettings"/>. Only <see cref="Business.SettingsService"/> writes them.
/// </summary>
public sealed record AppSettings
{
    public ThemeChoice Theme { get; init; } = ThemeChoice.System;

    /// <summary>Tray messages when a job ends or updates are found.</summary>
    public bool ShowNotifications { get; init; } = true;

    /// <summary>Size of the main window (device-independent pixels).</summary>
    public double WindowWidth { get; init; } = 1180;

    public double WindowHeight { get; init; } = 780;

    /// <summary>The menu at the left of the main window is folded to its icons.</summary>
    public bool IsSidebarCollapsed { get; init; }

    /// <summary>The catalog shows tiles (true) or a list.</summary>
    public bool CatalogAsTiles { get; init; } = true;
}
