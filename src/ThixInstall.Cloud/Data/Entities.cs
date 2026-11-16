using ThixInstall.Shared;

namespace ThixInstall.Cloud.Data;

/// <summary>What every record has: an Id (BIGINT) and who created and changed it, and when (the columns are filled by <see cref="AppDbContext"/>).</summary>
public abstract class Entity
{
    public long Id { get; set; }

    public DateTime CreateDate { get; set; }

    public long? CreateUserId { get; set; }

    public DateTime UpdateDate { get; set; }

    public long? UpdateUserId { get; set; }
}

/// <summary>A record that supports soft delete: 0 = deleted, 1 = created, 2 = updated.</summary>
public abstract class SoftEntity : Entity
{
    public const short Deleted = 0;
    public const short Created = 1;
    public const short Updated = 2;

    public short UpdateState { get; set; } = Created;
}

/// <summary>An account. Administrators keep the catalog, the profiles and the settings and see every device; users their own devices.</summary>
public sealed class User : SoftEntity
{
    /// <summary>The name to sign in with (browser and desktop); unique, stored in lower case.</summary>
    public string UserName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string PasswordHash { get; set; } = string.Empty;

    public bool IsAdmin { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>A password set by an administrator: the person chooses their own at the next sign-in.</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>system, light or dark.</summary>
    public string Theme { get; set; } = "system";

    public int FailedLogins { get; set; }

    public DateTime? LockoutUntil { get; set; }

    public DateTime? LastLoginDate { get; set; }
}

/// <summary>A signed-in browser. The cookie carries the <see cref="Token"/>; the session lives in the database.</summary>
public sealed class UserSession : Entity
{
    public Guid Token { get; set; } = Guid.NewGuid();

    public long UserId { get; set; }

    public User? User { get; set; }

    public DateTime LastSeenDate { get; set; }

    public DateTime ExpiresDate { get; set; }

    public bool IsRevoked { get; set; }

    public string? UserAgent { get; set; }

    public string? IpAddress { get; set; }
}

/// <summary>
/// An app of the catalog. One record for both systems: the Windows source (winget, Chocolatey or a direct download) and the macOS source
/// (a Homebrew cask or formula). What a source resolves to is a <see cref="PackageRelease"/> per system and architecture.
/// </summary>
public sealed class Package : SoftEntity
{
    /// <summary>Short stable name ("firefox"), unique.</summary>
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Publisher { get; set; }

    public string? Description { get; set; }

    public string Category { get; set; } = string.Empty;

    public string? Homepage { get; set; }

    public bool IsPublic { get; set; } = true;

    public bool IsFeatured { get; set; }

    public int SortOrder { get; set; }

    /// <summary>Winget, MicrosoftStore, Chocolatey, Direct, Upload or null (not for Windows).</summary>
    public InstallMethod? WindowsMethod { get; set; }

    public string? WingetId { get; set; }

    public string? ChocoId { get; set; }

    /// <summary>The id of an app in the Microsoft Store ("9NKSQGP7F2NH", "XP…").</summary>
    public string? StoreId { get; set; }

    /// <summary>The package family of a Store app (MSIX): how a device recognises it; null for Store apps that are classic programs.</summary>
    public string? StorePackageFamilyName { get; set; }

    public string? DirectUrl { get; set; }

    /// <summary>The silent arguments of a direct download or an uploaded installer (Windows).</summary>
    public string? DirectSilentArguments { get; set; }

    public string? DirectSha256 { get; set; }

    /// <summary>The version of a direct download or an uploaded installer (Windows), as the administrator gives it.</summary>
    public string? DirectVersion { get; set; }

    /// <summary>The installer an administrator uploaded for Windows (<see cref="InstallMethod.Upload"/>): its SHA-256 names the file below /data/uploads.</summary>
    public string? WindowsUploadSha256 { get; set; }

    public string? WindowsUploadFileName { get; set; }

    /// <summary>The start of the name in "Installierte Apps" (Windows); empty = the name of the package.</summary>
    public string? DetectDisplayName { get; set; }

    /// <summary>HomebrewCask, Homebrew, Direct, Upload or null (not for macOS).</summary>
    public InstallMethod? MacMethod { get; set; }

    public string? BrewName { get; set; }

    /// <summary>A direct download for macOS: a dmg, pkg or zip.</summary>
    public string? MacDirectUrl { get; set; }

    public string? MacDirectSha256 { get; set; }

    /// <summary>The version of a direct download or an uploaded installer (macOS), as the administrator gives it.</summary>
    public string? MacDirectVersion { get; set; }

    /// <summary>The installer an administrator uploaded for macOS (dmg, pkg, zip).</summary>
    public string? MacUploadSha256 { get; set; }

    public string? MacUploadFileName { get; set; }

    /// <summary>"Firefox.app"; empty = what the cask names.</summary>
    public string? MacAppBundleName { get; set; }

    /// <summary>When the sources were resolved the last time (null = never).</summary>
    public DateTime? ResolvedDate { get; set; }

    /// <summary>When the icon was looked for on the homepage (once; null = not yet).</summary>
    public DateTime? IconFetchDate { get; set; }

    public PackageIcon? Icon { get; set; }
}

/// <summary>The icon of a package: a row of its own, so that lists never load the bytes.</summary>
public sealed class PackageIcon : Entity
{
    public long PackageId { get; set; }

    public Package? Package { get; set; }

    public byte[] Data { get; set; } = [];

    public string ContentType { get; set; } = "image/png";
}

/// <summary>What the source of a package resolved to for one system and architecture: the newest version and how to install it silently.</summary>
public sealed class PackageRelease : Entity
{
    public long PackageId { get; set; }

    public Package? Package { get; set; }

    public TargetOs Os { get; set; }

    public CpuArchitecture Architecture { get; set; }

    public InstallMethod Method { get; set; }

    public string? Version { get; set; }

    /// <summary>The installer at its source; null = the device runs the package manager.</summary>
    public string? InstallerUrl { get; set; }

    public string? Sha256 { get; set; }

    public InstallerKind? InstallerKind { get; set; }

    public string? SilentArguments { get; set; }

    public string? ProductCode { get; set; }

    public string? AppBundleName { get; set; }

    public string? FileName { get; set; }

    public long? Size { get; set; }

    public DateTime ResolvedDate { get; set; }

    /// <summary>Why the last resolve failed (the older values stay).</summary>
    public string? Error { get; set; }

    /// <summary>What the resolve says without being a failure (e.g. "Wird mit brew installiert.").</summary>
    public string? Note { get; set; }
}

/// <summary>A script ("Makro") of the catalog for one system.</summary>
public sealed class Script : SoftEntity
{
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Category { get; set; } = string.Empty;

    public TargetOs Os { get; set; }

    public ScriptShell Shell { get; set; }

    public ScriptContext Context { get; set; } = ScriptContext.System;

    public string Content { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; }

    public bool IsPublic { get; set; } = true;

    public int SortOrder { get; set; }
}

/// <summary>Apps and scripts for devices; devices that enroll with its <see cref="EnrollmentKey"/> get it at once (auto-enrollment).</summary>
public sealed class Profile : SoftEntity
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The secret of the auto-enrollment.</summary>
    public Guid EnrollmentKey { get; set; } = Guid.NewGuid();
}

/// <summary>One app or script of a profile (exactly one of the two).</summary>
public sealed class ProfileItem : Entity
{
    public long ProfileId { get; set; }

    public long? PackageId { get; set; }

    public Package? Package { get; set; }

    public long? ScriptId { get; set; }

    public Script? Script { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>
/// A computer with ThixInstall, enrolled in an account. It sends its token as a bearer token; only the SHA-256 of the token is stored. Removing a
/// device (soft delete) ends its access at once.
/// </summary>
public sealed class Device : SoftEntity
{
    /// <summary>SHA-256 (hex) of the token the device got at its enrollment.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public long UserId { get; set; }

    public User? User { get; set; }

    public long? ProfileId { get; set; }

    public Profile? Profile { get; set; }

    public string Name { get; set; } = string.Empty;

    public TargetOs Os { get; set; }

    public string OsVersion { get; set; } = string.Empty;

    public CpuArchitecture Architecture { get; set; }

    public string AgentVersion { get; set; } = string.Empty;

    public string? HardwareId { get; set; }

    /// <summary>The device reports its inventory and takes actions from the cloud.</summary>
    public bool SyncEnabled { get; set; }

    public DateTime LastSeenDate { get; set; }

    public string? IpAddress { get; set; }

    public DateTime? InventoryDate { get; set; }
}

/// <summary>An app the device reported in its last inventory.</summary>
public sealed class DeviceApp : Entity
{
    public long DeviceId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Version { get; set; }

    public string? Publisher { get; set; }

    public long? PackageId { get; set; }

    public string Source { get; set; } = string.Empty;
}

/// <summary>An action for a device ("Auftrag"). Every change of it takes the next number of the sequence <c>ActionRevision</c>.</summary>
public sealed class DeviceAction : Entity
{
    public long DeviceId { get; set; }

    public Device? Device { get; set; }

    public ActionKind Kind { get; set; }

    public ActionState State { get; set; } = ActionState.Pending;

    public ActionOrigin Origin { get; set; }

    public long? PackageId { get; set; }

    public Package? Package { get; set; }

    public long? ScriptId { get; set; }

    public Script? Script { get; set; }

    public long? ProfileId { get; set; }

    public string Title { get; set; } = string.Empty;

    public long? RequestedByUserId { get; set; }

    public long Revision { get; set; }

    public DateTime? StartedDate { get; set; }

    public DateTime? FinishedDate { get; set; }

    public int? ExitCode { get; set; }

    public string? Output { get; set; }

    public string? InstalledVersion { get; set; }
}

/// <summary>A selection of the start page. Its token is the secret behind the download link; anonymous bundles have no owner.</summary>
public sealed class Bundle : Entity
{
    public Guid Token { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public TargetOs Os { get; set; }

    public bool IncludesAgent { get; set; }

    public long? ProfileId { get; set; }

    public Profile? Profile { get; set; }

    public long? OwnerUserId { get; set; }

    public User? Owner { get; set; }

    public int DownloadCount { get; set; }

    public DateTime? LastDownloadDate { get; set; }
}

/// <summary>One app or script of a bundle (exactly one of the two).</summary>
public sealed class BundleItem : Entity
{
    public long BundleId { get; set; }

    public long? PackageId { get; set; }

    public Package? Package { get; set; }

    public long? ScriptId { get; set; }

    public Script? Script { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>A file of the download cache (<c>/data/cache/&lt;Sha256&gt;</c>), fetched once from <see cref="SourceUrl"/>.</summary>
public sealed class CacheFile : Entity
{
    public string Sha256 { get; set; } = string.Empty;

    public string SourceUrl { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";

    public long Size { get; set; }

    public DateTime LastUsedDate { get; set; }

    public int UseCount { get; set; }
}

/// <summary>A setting of the cloud (key and value).</summary>
public sealed class Setting : Entity
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}
