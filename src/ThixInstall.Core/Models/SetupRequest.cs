using ThixInstall.Shared;

namespace ThixInstall.Core.Models;

/// <summary>What the command line of the program asks for (SETUP-05).</summary>
public enum SetupAction
{
    /// <summary>No setup argument: the program (or a bundle, or the setup assistant).</summary>
    None,

    /// <summary>"--quiet" or "--install": install without a window.</summary>
    Install,

    /// <summary>"--update": update without a window (the installed ThixInstall keeps its choices).</summary>
    Update,

    /// <summary>"--configure": apply the choices to the installed ThixInstall, nothing is copied (macOS after the bundle was copied).</summary>
    Configure,

    /// <summary>"--uninstall" (with "--quiet" without the assistant).</summary>
    Uninstall,
}

/// <summary>The setup part of a command line: what to do, with which choices, and whether to start the app afterwards ("--start").</summary>
public sealed record SetupRequest(SetupAction Action, InstallChoices Choices, bool Quiet = false, bool Start = false);
