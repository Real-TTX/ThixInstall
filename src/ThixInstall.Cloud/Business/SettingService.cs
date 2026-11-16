using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ThixInstall.Cloud.Data;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Business;

/// <summary>The settings of the cloud as the pages show them.</summary>
public sealed record CloudSettings(bool PublicCatalog, int CacheMaxGigabytes, string DesktopChannel, string? GitHubToken, InstallRoute InstallRoute = InstallRoute.CloudCache);

/// <summary>
/// The settings of the cloud (table <c>Setting</c>): whether anonymous visitors see the catalog, the size of the download cache, the way devices
/// try first for apps of the package managers (cloud cache or winget/Chocolatey/Homebrew), the channel the desktop packages come from, the GitHub
/// token of the resolvers. Read often, so kept in memory until they change.
/// </summary>
public static class SettingService
{
    public const string PublicCatalogKey = "PublicCatalog";
    public const string CacheMaxKey = "CacheMaxGigabytes";
    public const string DesktopChannelKey = "DesktopChannel";
    public const string GitHubTokenKey = "GitHubToken";
    public const string InstallRouteKey = "InstallRoute";
    public const string SeededKey = "Seeded";

    public const int DefaultCacheGigabytes = 20;

    private static CloudSettings? _cached;

    /// <summary>The channel of this build: release and nightly keep theirs, a local build takes the release channel.</summary>
    public static string OwnChannel => AppInfo.Channel == "nightly" ? "nightly" : "release";

    public static async Task<CloudSettings> GetAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (_cached is { } cached)
        {
            return cached;
        }

        var all = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var settings = new CloudSettings(
            !all.TryGetValue(PublicCatalogKey, out var pub) || pub != "false",
            all.TryGetValue(CacheMaxKey, out var max) && int.TryParse(max, CultureInfo.InvariantCulture, out var gb) && gb > 0 ? gb : DefaultCacheGigabytes,
            all.TryGetValue(DesktopChannelKey, out var channel) && channel is "release" or "nightly" ? channel : OwnChannel,
            all.TryGetValue(GitHubTokenKey, out var token) && token.Length > 0 ? token : null,
            all.TryGetValue(InstallRouteKey, out var route) && Enum.TryParse<InstallRoute>(route, out var parsed) && Enum.IsDefined(parsed) ? parsed : InstallRoute.CloudCache);
        _cached = settings;
        return settings;
    }

    /// <summary>The tab "Allgemein"; <paramref name="installRoute"/> is the way devices try first (null = keep the current one).</summary>
    public static async Task<Outcome> SaveGeneralAsync(AppDbContext db, Actor actor, bool publicCatalog, int cacheMaxGigabytes, InstallRoute? installRoute = null)
    {
        UserService.RequireAdmin(actor);
        if (cacheMaxGigabytes is < 1 or > 10_000)
        {
            return Outcome.Fail("Die Größe des Caches liegt zwischen 1 und 10.000 GB.", "CacheMaxGigabytes");
        }

        if (installRoute is { } unknown && !Enum.IsDefined(unknown))
        {
            return Outcome.Fail("Bitte einen Weg wählen.", "InstallRoute");
        }

        await SetAsync(db, PublicCatalogKey, publicCatalog ? "true" : "false");
        await SetAsync(db, CacheMaxKey, cacheMaxGigabytes.ToString(CultureInfo.InvariantCulture));
        if (installRoute is { } route)
        {
            await SetAsync(db, InstallRouteKey, route.ToString());
        }
        return Outcome.Success();
    }

    public static async Task<Outcome> SaveChannelAsync(AppDbContext db, Actor actor, string channel)
    {
        UserService.RequireAdmin(actor);
        if (channel is not ("release" or "nightly"))
        {
            return Outcome.Fail("Bitte Release oder Nightly wählen.", "DesktopChannel");
        }

        await SetAsync(db, DesktopChannelKey, channel);
        return Outcome.Success();
    }

    /// <summary>The GitHub token of the resolvers (empty = none). <paramref name="keep"/>: the field was left empty, the token stays.</summary>
    public static async Task<Outcome> SaveGitHubTokenAsync(AppDbContext db, Actor actor, string? token, bool remove)
    {
        UserService.RequireAdmin(actor);
        var value = (token ?? string.Empty).Trim();
        if (remove)
        {
            await SetAsync(db, GitHubTokenKey, string.Empty);
            return Outcome.Success();
        }

        if (value.Length == 0)
        {
            return Outcome.Success();
        }

        if (value.Length > 200 || value.Any(char.IsWhiteSpace))
        {
            return Outcome.Fail("Das sieht nicht wie ein GitHub-Token aus.", "GitHubToken");
        }

        await SetAsync(db, GitHubTokenKey, value);
        return Outcome.Success();
    }

    public static async Task<string?> GetRawAsync(AppDbContext db, string key, CancellationToken ct = default)
        => await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);

    public static async Task SetAsync(AppDbContext db, string key, string value, CancellationToken ct = default)
    {
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            db.Settings.Add(new Setting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }

        await db.SaveChangesAsync(ct);
        _cached = null;
    }

    /// <summary>Tests start with a fresh database.</summary>
    internal static void Forget() => _cached = null;
}
