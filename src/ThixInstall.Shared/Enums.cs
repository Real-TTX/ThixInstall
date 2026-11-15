namespace ThixInstall.Shared;

/// <summary>The operating systems ThixInstall manages. Every catalog item belongs to exactly one.</summary>
public enum TargetOs
{
    Windows = 1,
    MacOS = 2,
}

public enum CpuArchitecture
{
    X64 = 1,
    Arm64 = 2,
}

/// <summary>
/// Where a package comes from. The cloud resolves Winget and HomebrewCask packages itself (manifest / cask API) and then
/// serves the installer from its download cache; the device falls back to the package manager's own command line when
/// a plan has no <see cref="InstallPlan.DownloadUrl"/>. Chocolatey and Homebrew formulae always run their command line.
/// </summary>
public enum InstallMethod
{
    Winget = 1,
    Chocolatey = 2,
    /// <summary>A direct download (URL + silent arguments), cached by the cloud.</summary>
    Direct = 3,
    /// <summary>A Homebrew formula (command line tools): always "brew install".</summary>
    Homebrew = 4,
    /// <summary>A Homebrew cask (apps): the cloud resolves the download; "brew install --cask" is the fallback.</summary>
    HomebrewCask = 5,
}

/// <summary>What a downloaded installer is, which decides how the device starts it.</summary>
public enum InstallerKind
{
    /// <summary>A program, started with <see cref="InstallPlan.SilentArguments"/>.</summary>
    Exe = 1,
    /// <summary>"msiexec /i file" + <see cref="InstallPlan.SilentArguments"/>.</summary>
    Msi = 2,
    /// <summary>An MSIX / APPX package (Add-AppxPackage, provisioned for all users when running as system).</summary>
    Msix = 3,
    /// <summary>macOS disk image: the .app named <see cref="InstallPlan.AppBundleName"/> is copied to /Applications.</summary>
    Dmg = 4,
    /// <summary>macOS installer package ("installer -pkg", needs an administrator).</summary>
    Pkg = 5,
    /// <summary>A zip with the .app named <see cref="InstallPlan.AppBundleName"/> (macOS).</summary>
    Zip = 6,
}

public enum ScriptShell
{
    PowerShell = 1,
    Cmd = 2,
    Bash = 3,
    Zsh = 4,
}

/// <summary>Who runs a script on the device.</summary>
public enum ScriptContext
{
    /// <summary>With administrator rights: on an installed Windows device the ThixInstall service (LocalSystem).</summary>
    System = 1,
    /// <summary>As the signed-in user (the ThixInstall app in the user's session): for settings of the user.</summary>
    User = 2,
}

public enum ActionKind
{
    Install = 1,
    Update = 2,
    Uninstall = 3,
    RunScript = 4,
    /// <summary>Update every catalog app that has a newer version.</summary>
    UpdateAll = 5,
    /// <summary>Report the inventory again now.</summary>
    Refresh = 6,
}

public enum ActionState
{
    Pending = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    Cancelled = 5,
}

/// <summary>Who asked for an action.</summary>
public enum ActionOrigin
{
    /// <summary>The user on the device itself (reported afterwards).</summary>
    Device = 1,
    /// <summary>Sent from the cloud to this device (or to all devices of a profile).</summary>
    Push = 2,
    /// <summary>The device's profile asks for it (enrollment, a new item in the profile).</summary>
    Profile = 3,
    /// <summary>Part of a downloaded bundle (reported afterwards when the bundle enrolled the device).</summary>
    Bundle = 4,
}
