namespace ThixInstall.Shared;

/// <summary>GET <see cref="ApiContract.Catalog"/>: everything a device of one OS and architecture can install or run.</summary>
public sealed record CatalogResponse(
    TargetOs Os,
    CpuArchitecture Architecture,
    IReadOnlyList<PackageDto> Packages,
    IReadOnlyList<ScriptDto> Scripts,
    string ServerVersion,
    DateTimeOffset GeneratedAt);

/// <summary>An app of the catalog as a device of one OS sees it.</summary>
public sealed record PackageDto
{
    public required long Id { get; init; }

    /// <summary>Stable short name, e.g. "firefox" (unique in the cloud).</summary>
    public required string Key { get; init; }

    public required string Name { get; init; }

    public string? Publisher { get; init; }

    public string? Description { get; init; }

    /// <summary>e.g. "Browser", "Entwicklung" (free text, used for grouping).</summary>
    public required string Category { get; init; }

    public string? Homepage { get; init; }

    /// <summary>True when <see cref="ApiContract.PackageIcon"/> has an icon.</summary>
    public bool HasIcon { get; init; }

    /// <summary>Shown first / larger on the start page.</summary>
    public bool IsFeatured { get; init; }

    /// <summary>Visible to anonymous visitors.</summary>
    public bool IsPublic { get; init; }

    /// <summary>The newest version the cloud knows (null when it could not resolve one).</summary>
    public string? LatestVersion { get; init; }

    /// <summary>How to install it on this OS and architecture; null when the package has no source for it.</summary>
    public InstallPlan? Install { get; init; }

    /// <summary>How the device recognises the installed app (inventory, update check).</summary>
    public DetectionRule? Detection { get; init; }
}

/// <summary>
/// How a device installs a package. With a <see cref="DownloadUrl"/> the device downloads the installer from the cloud
/// (cache), checks <see cref="Sha256"/> and starts it by <see cref="InstallerKind"/> with <see cref="SilentArguments"/>;
/// without one it runs the package manager of <see cref="Method"/> with <see cref="PackageId"/>.
/// </summary>
public sealed record InstallPlan
{
    public required InstallMethod Method { get; init; }

    /// <summary>The id at the package manager: winget id ("Mozilla.Firefox"), choco id, brew formula or cask name.</summary>
    public string? PackageId { get; init; }

    /// <summary>The version this plan installs (null = whatever is newest at the package manager).</summary>
    public string? Version { get; init; }

    /// <summary>Path of the cloud (<see cref="ApiContract.Download"/>) or null.</summary>
    public string? DownloadUrl { get; init; }

    public string? FileName { get; init; }

    /// <summary>SHA-256 of the installer, hex; null when the source has none (the device then trusts the cloud).</summary>
    public string? Sha256 { get; init; }

    public long? Size { get; init; }

    public InstallerKind? InstallerKind { get; init; }

    /// <summary>The complete arguments for a silent installation (without the file itself).</summary>
    public string? SilentArguments { get; init; }

    /// <summary>MSI product code (silent uninstall, detection).</summary>
    public string? ProductCode { get; init; }

    /// <summary>macOS: the name of the app bundle, e.g. "Firefox.app".</summary>
    public string? AppBundleName { get; init; }
}

/// <summary>
/// How a device recognises an installed app. Windows: an entry of "Installierte Apps" whose DisplayName starts with
/// <see cref="DisplayName"/> (case-insensitive; a value starting with "^" is a regular expression) or has the MSI
/// <see cref="ProductCode"/>, or a Store app (MSIX) of the <see cref="PackageFamilyName"/> installed for any user. macOS: /Applications/<see cref="AppBundleName"/> or the Homebrew formula <see cref="BrewName"/>.
/// </summary>
public sealed record DetectionRule
{
    public string? DisplayName { get; init; }

    public string? ProductCode { get; init; }

    public string? AppBundleName { get; init; }

    public string? BrewName { get; init; }

    /// <summary>Windows: the package family of a Store app ("Microsoft.WindowsTerminal_8wekyb3d8bbwe").</summary>
    public string? PackageFamilyName { get; init; }
}

/// <summary>A script ("Makro") of the catalog. It belongs to one OS and runs in one shell.</summary>
public sealed record ScriptDto
{
    public required long Id { get; init; }

    public required string Key { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string Category { get; init; }

    public required TargetOs Os { get; init; }

    public required ScriptShell Shell { get; init; }

    public ScriptContext Context { get; init; } = ScriptContext.System;

    /// <summary>The script itself.</summary>
    public required string Content { get; init; }

    /// <summary>The job is stopped after this time (0 = 30 minutes).</summary>
    public int TimeoutSeconds { get; init; }

    public bool IsPublic { get; init; }

    /// <summary>SHA-256 (hex) of <see cref="Content"/>: the history shows which version of a script ran.</summary>
    public required string ContentHash { get; init; }
}

/// <summary>
/// GET <see cref="ApiContract.Bundle"/>: what somebody picked on the start page. The device installs the packages in this
/// order, then runs the scripts in this order. <see cref="IncludesAgent"/>: ThixInstall itself is installed afterwards
/// and enrolled with the bundle's token (only bundles of a signed-in user, valid <see cref="ApiContract.BundleEnrollmentDays"/> days).
/// </summary>
public sealed record BundleDto(
    string Token,
    string Name,
    TargetOs Os,
    bool IncludesAgent,
    bool CanEnroll,
    string? ProfileName,
    IReadOnlyList<PackageDto> Packages,
    IReadOnlyList<ScriptDto> Scripts,
    DateTimeOffset CreatedAt);
