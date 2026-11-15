namespace ThixInstall.Shared;

/// <summary>What a device says about itself (enrollment and every <see cref="DeviceUpdate"/>).</summary>
public sealed record DeviceFacts(
    string Name,
    TargetOs Os,
    string OsVersion,
    CpuArchitecture Architecture,
    string AgentVersion,
    // A stable id of the machine (Windows MachineGuid, macOS IOPlatformUUID): enrolling again reuses the device.
    string? HardwareId);

/// <summary>
/// POST <see cref="ApiContract.Enroll"/>: a device joins an account with exactly one of: user name + password, the
/// enrollment key of a profile (auto-enrollment: the device gets that profile) or the token of a bundle that installs
/// ThixInstall (the device belongs to the bundle's creator and gets the bundle's profile).
/// </summary>
public sealed record EnrollRequest
{
    public string? UserName { get; init; }

    public string? Password { get; init; }

    public string? EnrollmentKey { get; init; }

    public string? BundleToken { get; init; }

    public required DeviceFacts Device { get; init; }

    /// <summary>Switch "Sync" on at once (the device can then receive actions). Enrollment by key or bundle always does.</summary>
    public bool EnableSync { get; init; } = true;
}

/// <summary>The answer of a successful enrollment: the token is shown once and identifies the device from now on.</summary>
public sealed record EnrollResponse(string Token, DeviceInfo Device);

/// <summary>GET <see cref="ApiContract.Device"/>: the device as the cloud sees it.</summary>
public sealed record DeviceInfo(
    long Id,
    string Name,
    string UserName,
    string UserDisplayName,
    string? ProfileName,
    bool SyncEnabled,
    string ServerVersion);

/// <summary>PUT <see cref="ApiContract.Device"/>: what a device may change about itself (null = unchanged).</summary>
public sealed record DeviceUpdate(bool? SyncEnabled = null, DeviceFacts? Facts = null);

/// <summary>PUT <see cref="ApiContract.Inventory"/>: the apps installed on the device.</summary>
public sealed record InventoryReport(IReadOnlyList<InstalledApp> Apps, DateTimeOffset CollectedAt);

/// <summary>One installed app. <see cref="PackageId"/> is set when the device matched it to a catalog package.</summary>
public sealed record InstalledApp(
    string Name,
    string? Version,
    string? Publisher,
    long? PackageId,
    // "registry", "app" (macOS /Applications) or "brew".
    string Source);
