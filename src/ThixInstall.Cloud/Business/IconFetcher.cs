using Microsoft.EntityFrameworkCore;
using ThixInstall.Cloud.Data;

namespace ThixInstall.Cloud.Business;

/// <summary>
/// Looks once for the icon of a package without one on its homepage: <c>/apple-touch-icon.png</c> (large), then <c>/favicon.ico</c>. Only a real
/// image is kept (its first bytes decide, see <see cref="CatalogService.IconType"/>); every failure is ignored. An administrator can upload an icon instead.
/// </summary>
public static class IconFetcher
{
    private static readonly string[] Candidates = ["/apple-touch-icon.png", "/apple-touch-icon-precomposed.png", "/favicon.ico"];

    /// <summary>Tries the packages whose homepage was not looked at yet; returns how many got an icon.</summary>
    public static async Task<int> RunAsync(AppDbContext db, int max, CancellationToken ct)
    {
        var due = await db.Packages.AsNoTracking()
            .Where(p => p.IconFetchDate == null && p.Homepage != null && !db.PackageIcons.Any(i => i.PackageId == p.Id))
            .OrderBy(p => p.Id).Take(max).Select(p => new { p.Id, p.Homepage }).ToListAsync(ct);
        var found = 0;
        foreach (var package in due)
        {
            if (await FetchAsync(package.Homepage!, ct) is { } icon)
            {
                await CatalogService.StoreIconAsync(db, package.Id, icon.Data, icon.Type, ct);
                found++;
            }

            await db.Packages.Where(p => p.Id == package.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.IconFetchDate, DateTime.UtcNow), ct);
        }

        return found;
    }

    /// <summary>Looks on the homepage of one package now (a new app of "App hinzufügen"); true when it got an icon.</summary>
    public static async Task<bool> RunForAsync(AppDbContext db, long packageId, CancellationToken ct)
    {
        var homepage = await db.Packages.AsNoTracking().Where(p => p.Id == packageId).Select(p => p.Homepage).FirstOrDefaultAsync(ct);
        var icon = homepage is null ? null : await FetchAsync(homepage, ct);
        if (icon is not null)
        {
            await CatalogService.StoreIconAsync(db, packageId, icon.Value.Data, icon.Value.Type, ct);
        }

        await db.Packages.Where(p => p.Id == packageId).ExecuteUpdateAsync(s => s.SetProperty(p => p.IconFetchDate, DateTime.UtcNow), ct);
        return icon is not null;
    }

    /// <summary>An icon at an address a source names (Chocolatey's icons are often SVG); null when it is no image or too large.</summary>
    public static async Task<(byte[] Data, string Type)?> DownloadAsync(string url, CancellationToken ct)
    {
        if (!Text.IsHttpUrl(url))
        {
            return null;
        }

        try
        {
            using var response = await Net.GetAsync(url, TimeSpan.FromSeconds(10), ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > CatalogService.MaxIconBytes)
            {
                return null;
            }

            var data = await response.Content.ReadAsByteArrayAsync(ct);
            return data.Length <= CatalogService.MaxIconBytes && CatalogService.IconType(data) is { } type ? (data, type) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<(byte[] Data, string Type)?> FetchAsync(string homepage, CancellationToken ct)
    {
        if (!Uri.TryCreate(homepage, UriKind.Absolute, out var uri))
        {
            return null;
        }

        foreach (var path in Candidates)
        {
            try
            {
                using var response = await Net.GetAsync(new Uri(uri, path).ToString(), TimeSpan.FromSeconds(10), ct);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > CatalogService.MaxIconBytes)
                {
                    continue;
                }

                var data = await response.Content.ReadAsByteArrayAsync(ct);
                if (data.Length <= CatalogService.MaxIconBytes && CatalogService.IconType(data) is { } type and not "image/svg+xml")
                {
                    return (data, type);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // try the next one
            }
        }

        return null;
    }
}
