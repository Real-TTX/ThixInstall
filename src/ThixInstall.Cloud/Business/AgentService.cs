using System.Text.Json;
using System.Text.Json.Serialization;
using ThixInstall.Cloud.Data;

namespace ThixInstall.Cloud.Business;

/// <summary>One file of a version of the desktop app (the Windows setup, a zipped Mac app).</summary>
public sealed record AgentPackageInfo(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>The update manifest the desktop CI publishes next to the packages of a channel (thixinstall-update.json).</summary>
public sealed record AgentManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("buildNumber")] int BuildNumber,
    [property: JsonPropertyName("setup")] string? Setup,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("publishedAt")] DateTimeOffset? PublishedAt,
    [property: JsonPropertyName("packages")] Dictionary<string, AgentPackageInfo>? Packages);

/// <summary>Where the package of a platform comes from, for the settings page.</summary>
public sealed record AgentSource(string PlatformKey, string Label, bool HasUpload, long? UploadSize, DateTime? UploadDate, string? GitHubFile, string? GitHubVersion);

/// <summary>
/// The desktop app the cloud hands out in bundles: from an upload of an administrator (Einstellungen › Desktop-App, <c>/data/agent</c>) or from the
/// GitHub release of the channel (the manifest is read every 10 minutes at most, the files go through the download cache).
/// </summary>
public static class AgentService
{
    public const string Repository = "Real-TTX/ThixInstall";
    public const string ManifestFileName = "thixinstall-update.json";
    public const long MaxUploadBytes = 400L * 1024 * 1024;

    public static readonly IReadOnlyList<(string Key, string Label, string Extension)> Platforms =
    [
        ("win-x64", "Windows (x64)", ".exe"),
        ("osx-arm64", "macOS (Apple Silicon)", ".zip"),
        ("osx-x64", "macOS (Intel)", ".zip"),
    ];

    private static readonly TimeSpan ManifestFor = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim ManifestGate = new(1, 1);
    private static (string Channel, AgentManifest? Manifest, DateTime Read)? _manifest;
    private static string _directory = "/data/agent";

    public static string Directory => _directory;

    public static void UseDirectory(string directory)
    {
        _directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public static bool IsPlatform(string? key) => Platforms.Any(p => p.Key == key);

    public static string ManifestUrl(string channel) => $"https://github.com/{Repository}/releases/download/{channel}-channel/{ManifestFileName}";

    public static string FileUrl(string channel, string file) => $"https://github.com/{Repository}/releases/download/{channel}-channel/{Uri.EscapeDataString(file)}";

    /// <summary>The manifest of the channel the settings name; null when GitHub has none (yet) or cannot be reached.</summary>
    public static async Task<AgentManifest?> ManifestAsync(AppDbContext db, CancellationToken ct = default)
    {
        var channel = (await SettingService.GetAsync(db, ct)).DesktopChannel;
        if (_manifest is { } hit && hit.Channel == channel && DateTime.UtcNow - hit.Read < ManifestFor)
        {
            return hit.Manifest;
        }

        await ManifestGate.WaitAsync(ct);
        try
        {
            if (_manifest is { } again && again.Channel == channel && DateTime.UtcNow - again.Read < ManifestFor)
            {
                return again.Manifest;
            }

            AgentManifest? manifest = null;
            try
            {
                using var response = await Net.GetAsync(ManifestUrl(channel), TimeSpan.FromSeconds(20), ct);
                if (response.IsSuccessStatusCode)
                {
                    manifest = JsonSerializer.Deserialize<AgentManifest>(await response.Content.ReadAsStringAsync(ct));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                manifest = null;
            }

            _manifest = (channel, manifest, DateTime.UtcNow);
            return manifest;
        }
        finally
        {
            ManifestGate.Release();
        }
    }

    /// <summary>
    /// Whether the cloud has a program for the platform (an upload, or the manifest of the channel names one), without fetching it: the page of a
    /// bundle offers only what it can hand out.
    /// </summary>
    public static async Task<bool> IsAvailableAsync(AppDbContext db, string platformKey, CancellationToken ct = default)
    {
        var platform = Platforms.FirstOrDefault(p => p.Key == platformKey);
        if (platform.Key is null)
        {
            return false;
        }

        if (File.Exists(UploadPath(platform.Key, platform.Extension)))
        {
            return true;
        }

        var manifest = await ManifestAsync(db, ct);
        return manifest?.Packages?.ContainsKey(platform.Key) == true || (platform.Key == "win-x64" && !string.IsNullOrEmpty(manifest?.Setup));
    }

    /// <summary>The package of a platform: an upload first, else the file of the GitHub release (through the cache).</summary>
    public static async Task<Outcome<CachedFile>> PackageAsync(AppDbContext db, string platformKey, CancellationToken ct = default)
    {
        var platform = Platforms.FirstOrDefault(p => p.Key == platformKey);
        if (platform.Key is null)
        {
            return Outcome<CachedFile>.Missing();
        }

        var upload = UploadPath(platform.Key, platform.Extension);
        if (File.Exists(upload))
        {
            var info = new FileInfo(upload);
            return Outcome<CachedFile>.Success(new CachedFile(upload, $"ThixInstall-{platform.Key}{platform.Extension}", ContentTypeOf(platform.Extension), info.Length, string.Empty));
        }

        var manifest = await ManifestAsync(db, ct);
        var package = manifest?.Packages?.GetValueOrDefault(platform.Key)
                      ?? (platform.Key == "win-x64" && !string.IsNullOrEmpty(manifest?.Setup) ? new AgentPackageInfo(manifest.Setup!, manifest.Sha256 ?? string.Empty) : null);
        if (manifest is null || package is null || string.IsNullOrEmpty(package.File) || package.File.Contains('/') || package.File.Contains('\\'))
        {
            return Outcome<CachedFile>.Fail("Für diese Plattform gibt es noch kein ThixInstall-Paket. Ein Administrator kann es unter Einstellungen › Desktop-App hochladen.");
        }

        return await DownloadCache.GetAsync(db, FileUrl(manifest.Channel, package.File), package.Sha256, package.File, ct);
    }

    public static async Task<IReadOnlyList<AgentSource>> SourcesAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var manifest = await ManifestAsync(db, ct);
        return Platforms.Select(p =>
        {
            var path = UploadPath(p.Key, p.Extension);
            var info = File.Exists(path) ? new FileInfo(path) : null;
            var github = manifest?.Packages?.GetValueOrDefault(p.Key)?.File ?? (p.Key == "win-x64" ? manifest?.Setup : null);
            return new AgentSource(p.Key, p.Label, info is not null, info?.Length, info?.LastWriteTimeUtc, github, github is null ? null : manifest?.Version);
        }).ToList();
    }

    /// <summary>An administrator uploads the package of a platform (it is used instead of GitHub until it is removed).</summary>
    public static async Task<Outcome> UploadAsync(Actor actor, string platformKey, Stream content, long length, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var platform = Platforms.FirstOrDefault(p => p.Key == platformKey);
        if (platform.Key is null)
        {
            return Outcome.Fail("Diese Plattform gibt es nicht.", "Platform");
        }

        if (length <= 0 || length > MaxUploadBytes)
        {
            return Outcome.Fail("Die Datei ist leer oder größer als 400 MB.", "File");
        }

        System.IO.Directory.CreateDirectory(_directory);
        var temp = Path.Combine(_directory, "upload-" + Guid.NewGuid().ToString("N"));
        await using (var target = File.Create(temp))
        {
            await content.CopyToAsync(target, ct);
        }

        var head = new byte[4];
        await using (var check = File.OpenRead(temp))
        {
            _ = await check.ReadAsync(head, ct);
        }

        var ok = platform.Extension == ".exe" ? head[0] == 'M' && head[1] == 'Z' : head[0] == 'P' && head[1] == 'K';
        if (!ok)
        {
            File.Delete(temp);
            return Outcome.Fail(platform.Extension == ".exe" ? "Das ist kein Windows-Programm (ThixInstallSetup.exe)." : "Das ist keine Zip-Datei (ThixInstall-macOS-….zip).", "File");
        }

        File.Move(temp, UploadPath(platform.Key, platform.Extension), overwrite: true);
        return Outcome.Success();
    }

    public static Outcome RemoveUpload(Actor actor, string platformKey)
    {
        UserService.RequireAdmin(actor);
        var platform = Platforms.FirstOrDefault(p => p.Key == platformKey);
        if (platform.Key is null)
        {
            return Outcome.Missing();
        }

        File.Delete(UploadPath(platform.Key, platform.Extension));
        return Outcome.Success();
    }

    /// <summary>The manifest is read again at the next request (a new channel was chosen).</summary>
    public static void Forget() => _manifest = null;

    private static string UploadPath(string key, string extension) => Path.Combine(_directory, key + extension);

    private static string ContentTypeOf(string extension) => extension == ".zip" ? "application/zip" : "application/vnd.microsoft.portable-executable";
}
