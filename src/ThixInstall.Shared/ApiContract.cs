using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThixInstall.Shared;

/// <summary>
/// The protocol between ThixInstall (desktop app, Windows service, bundle runner) and ThixInstall.Cloud. JSON everywhere
/// (<see cref="Json"/>: camelCase, enums as text). Errors are 4xx/5xx with an <see cref="ApiError"/> body.
///
/// Public routes (no token): the catalog of public items, package icons, cached installer downloads, bundles (by their
/// secret token) and the enrollment of a device. Every other route needs the device token as "Authorization: Bearer
/// &lt;token&gt;"; an unknown or removed device answers 401 (the desktop then forgets its connection).
///
/// How a device is managed (only while the device has "Sync" switched on):
/// 1. <see cref="Inventory"/>: the device reports what is installed (at start, after every job, every few hours).
/// 2. <see cref="ActionsWait"/> is a long poll that answers as soon as the device has an action with a revision above
///    "since" (or after the timeout); <see cref="Actions"/> then returns the open actions, oldest first.
/// 3. The device runs them one by one: <see cref="ActionStart"/>, the work, <see cref="ActionFinish"/> with the result.
/// 4. What the device does on its own (the user installs something in the app) is reported with <see cref="ActionReport"/>,
///    so the cloud has one history per device.
/// </summary>
public static class ApiContract
{
    /// <summary>Raised when a change is not backwards compatible. Sent as header <see cref="ProtocolHeader"/>.</summary>
    public const int ProtocolVersion = 1;

    public const string ProtocolHeader = "X-ThixInstall-Protocol";

    // ---- public -------------------------------------------------------------------------------------------------------

    /// <summary>GET ?os=windows|macos&amp;arch=x64|arm64: <see cref="CatalogResponse"/>. Anonymous: public items only.</summary>
    public const string Catalog = "/api/v1/catalog";

    /// <summary>GET: the icon of a package (PNG, SVG or ICO as stored); 404 when it has none.</summary>
    public static string PackageIcon(long packageId) => $"/api/v1/packages/{packageId}/icon";

    /// <summary>
    /// GET: the installer of a resolved release, served from the cloud's download cache (fetched from its source on the
    /// first request and checked against its SHA-256). Public: the release id is not a secret, the file is public anyway.
    /// </summary>
    public static string Download(long releaseId) => $"/api/v1/downloads/{releaseId}";

    /// <summary>GET ?arch=x64|arm64: <see cref="BundleDto"/>, the selection behind a bundle (the token is its secret).</summary>
    public static string Bundle(string token) => $"/api/v1/bundles/{token}";

    /// <summary>POST <see cref="EnrollRequest"/> -> <see cref="EnrollResponse"/>.</summary>
    public const string Enroll = "/api/v1/devices/enroll";

    /// <summary>GET: the update manifest of the desktop app of the cloud's channel (the GitHub manifest, cached).</summary>
    public const string AgentManifest = "/api/v1/agent/manifest";

    /// <summary>GET: the ThixInstall package of a platform (win-x64 setup, osx-arm64 / osx-x64 zip), from the cache.</summary>
    public static string AgentPackage(string platformKey) => $"/api/v1/agent/{platformKey}";

    // ---- device token -------------------------------------------------------------------------------------------------

    /// <summary>POST: the device leaves the cloud (its token is revoked; the device stays in the list as removed).</summary>
    public const string DeviceLogout = "/api/v1/devices/logout";

    /// <summary>GET: <see cref="DeviceInfo"/>; PUT <see cref="DeviceUpdate"/> -> <see cref="DeviceInfo"/>.</summary>
    public const string Device = "/api/v1/device";

    /// <summary>PUT <see cref="InventoryReport"/>: what is installed on the device (replaces the last report).</summary>
    public const string Inventory = "/api/v1/device/inventory";

    /// <summary>GET ?since=&amp;timeout=: <see cref="WaitResult"/>, long poll for new actions of this device.</summary>
    public const string ActionsWait = "/api/v1/device/actions/wait";

    /// <summary>GET: <see cref="ActionsResponse"/>, the open actions (pending) of this device, oldest first.</summary>
    public const string Actions = "/api/v1/device/actions";

    /// <summary>POST: the device starts an action (pending -> running). 409 when it was cancelled meanwhile.</summary>
    public static string ActionStart(long actionId) => $"/api/v1/device/actions/{actionId}/start";

    /// <summary>POST <see cref="ActionResult"/>: the device finished an action.</summary>
    public static string ActionFinish(long actionId) => $"/api/v1/device/actions/{actionId}/finish";

    /// <summary>POST <see cref="LocalRunReport"/>: something the device did on its own, for the history in the cloud.</summary>
    public const string ActionReport = "/api/v1/device/actions/report";

    // ---- web (not JSON) -----------------------------------------------------------------------------------------------

    /// <summary>The page of a bundle (summary, download button, link to share).</summary>
    public static string BundlePage(string token) => $"/b/{token}";

    /// <summary>GET ?platform=win-x64|osx-arm64|osx-x64: the bundle as a program (Windows: the setup with a <see cref="BundleTicket"/> appended; macOS: zip of the app and a .thixinstall file).</summary>
    public static string BundleDownload(string token) => $"/b/{token}/download";

    // ---- limits -------------------------------------------------------------------------------------------------------

    /// <summary>Longest wait of a long poll in seconds.</summary>
    public const int MaxWaitSeconds = 30;

    /// <summary>The output of a job that is kept (characters, the end is kept).</summary>
    public const int MaxOutputLength = 64_000;

    /// <summary>The largest script (characters).</summary>
    public const int MaxScriptLength = 200_000;

    /// <summary>The most apps one inventory report carries.</summary>
    public const int MaxInventoryApps = 5_000;

    /// <summary>How long the token of a bundle that installs ThixInstall may enroll devices.</summary>
    public const int BundleEnrollmentDays = 30;

    /// <summary>The JSON settings of every request and response (camelCase, enums as text, nulls left out).</summary>
    public static readonly JsonSerializerOptions Json = CreateJsonOptions();

    /// <summary>"windows" / "macos" as used in query strings.</summary>
    public static string ToQuery(TargetOs os) => os == TargetOs.MacOS ? "macos" : "windows";

    /// <summary>"x64" / "arm64" as used in query strings.</summary>
    public static string ToQuery(CpuArchitecture architecture) => architecture == CpuArchitecture.Arm64 ? "arm64" : "x64";

    public static TargetOs? ParseOs(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "windows" or "win" => TargetOs.Windows,
        "macos" or "mac" or "osx" => TargetOs.MacOS,
        _ => null,
    };

    public static CpuArchitecture? ParseArchitecture(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "x64" or "amd64" => CpuArchitecture.X64,
        "arm64" or "aarch64" => CpuArchitecture.Arm64,
        _ => null,
    };

    /// <summary>The package key of a platform: win-x64, win-arm64, osx-arm64, osx-x64.</summary>
    public static string PlatformKey(TargetOs os, CpuArchitecture architecture) =>
        (os == TargetOs.MacOS ? "osx-" : "win-") + ToQuery(architecture);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>The body of every error of the API (4xx/5xx), with a German message for the user.</summary>
public sealed record ApiError(string Message, string? Field = null);
