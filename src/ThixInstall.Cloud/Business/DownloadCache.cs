using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ThixInstall.Cloud.Data;

namespace ThixInstall.Cloud.Business;

/// <summary>A file of the cache that is ready to be sent.</summary>
public sealed record CachedFile(string Path, string FileName, string ContentType, long Size, string Sha256);

/// <summary>A file of the cache in the administration.</summary>
public sealed record CacheListItem(long Id, string FileName, string SourceUrl, string Sha256, long Size, DateTime CreateDate, DateTime LastUsedDate, int UseCount);

/// <summary>The size of the cache.</summary>
public sealed record CacheSummary(int Files, long Bytes, long MaxBytes);

/// <summary>
/// The download cache: every installer (and every desktop package of ThixInstall) is fetched once from its source, checked against its SHA-256 and
/// kept under <c>/data/cache/&lt;sha256&gt;</c>; afterwards the cloud sends it itself. One download per source at a time (a lock per address);
/// above the configured size the files used longest ago go first.
/// </summary>
public static class DownloadCache
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);
    private static string _directory = "/data/cache";

    /// <summary>The longest a fetch of one file may take.</summary>
    public static TimeSpan FetchTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>The largest file the cache takes (8 GB).</summary>
    public const long MaxFileBytes = 8L * 1024 * 1024 * 1024;

    public static string Directory => _directory;

    public static void UseDirectory(string directory)
    {
        _directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    /// <summary>The installer of a release, from the cache or fetched now; null when the release has none.</summary>
    public static async Task<Outcome<CachedFile>> ReleaseAsync(AppDbContext db, long releaseId, CancellationToken ct)
    {
        var release = await db.Releases.AsNoTracking().FirstOrDefaultAsync(r => r.Id == releaseId, ct);
        if (release?.InstallerUrl is null || release.InstallerKind is null || !await db.Packages.AnyAsync(p => p.Id == release.PackageId, ct))
        {
            return Outcome<CachedFile>.Missing();
        }

        // an uploaded installer lives outside the cache (its clean-up never removes it)
        if (PackageUploads.ShaOf(release.InstallerUrl) is { } sha)
        {
            return PackageUploads.Find(sha, release.FileName) is { } upload
                ? Outcome<CachedFile>.Success(upload)
                : Outcome<CachedFile>.Fail("Die hochgeladene Datei fehlt in der Cloud. Bitte sie neu hochladen.");
        }

        var outcome = await GetAsync(db, release.InstallerUrl, release.Sha256, release.FileName, ct);
        if (outcome.Ok && release.Size != outcome.Value!.Size)
        {
            await db.Releases.Where(r => r.Id == releaseId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Size, outcome.Value.Size), ct);
        }

        return outcome;
    }

    /// <summary>A file by its source address (and, when known, its SHA-256), from the cache or fetched now.</summary>
    public static async Task<Outcome<CachedFile>> GetAsync(AppDbContext db, string sourceUrl, string? sha256, string? fileName, CancellationToken ct)
    {
        if (!Text.IsHttpUrl(sourceUrl))
        {
            return Outcome<CachedFile>.Fail("Die Quelle ist keine http(s)-Adresse.");
        }

        var expected = Text.IsSha256(sha256) ? sha256!.ToLowerInvariant() : null;
        if (await FindAsync(db, sourceUrl, expected, ct) is { } hit)
        {
            return Outcome<CachedFile>.Success(hit);
        }

        var gate = Locks.GetOrAdd(sourceUrl, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // another request may have fetched it meanwhile
            if (await FindAsync(db, sourceUrl, expected, ct) is { } fetched)
            {
                return Outcome<CachedFile>.Success(fetched);
            }

            return await FetchAsync(db, sourceUrl, expected, fileName, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<CachedFile?> FindAsync(AppDbContext db, string sourceUrl, string? expected, CancellationToken ct)
    {
        var rows = await db.CacheFiles.Where(c => c.SourceUrl == sourceUrl || (expected != null && c.Sha256 == expected)).ToListAsync(ct);
        foreach (var row in rows.OrderByDescending(r => r.SourceUrl == sourceUrl))
        {
            if (expected is not null && row.Sha256 != expected)
            {
                continue; // the source now has another file: fetch again
            }

            var path = PathOf(row.Sha256);
            if (!File.Exists(path))
            {
                db.CacheFiles.Remove(row);
                await db.SaveChangesAsync(ct);
                continue;
            }

            await db.CacheFiles.Where(c => c.Id == row.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastUsedDate, DateTime.UtcNow).SetProperty(c => c.UseCount, c => c.UseCount + 1), ct);
            return new CachedFile(path, row.FileName, row.ContentType, row.Size, row.Sha256);
        }

        return null;
    }

    private static async Task<Outcome<CachedFile>> FetchAsync(AppDbContext db, string sourceUrl, string? expected, string? fileName, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(_directory);
        var temp = Path.Combine(_directory, "tmp-" + Guid.NewGuid().ToString("N"));
        string sha;
        long size;
        string contentType;
        string name;
        try
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(FetchTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, sourceUrl);
            using var response = await Net.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timer.Token);
            if (!response.IsSuccessStatusCode)
            {
                return Outcome<CachedFile>.Fail($"Die Quelle antwortet mit {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength > MaxFileBytes)
            {
                return Outcome<CachedFile>.Fail("Die Datei ist zu groß für den Cache.");
            }

            contentType = response.Content.Headers.ContentType?.MediaType is { } media && media.Length <= 100 && !media.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                ? media : "application/octet-stream";
            name = fileName
                   ?? Text.Clean(response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'), 200)
                   ?? Text.FileNameOf(response.RequestMessage?.RequestUri?.ToString() ?? sourceUrl)
                   ?? "download.bin";
            await using (var source = await response.Content.ReadAsStreamAsync(timer.Token))
            await using (var target = File.Create(temp))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                size = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, timer.Token)) > 0)
                {
                    size += read;
                    if (size > MaxFileBytes)
                    {
                        return Outcome<CachedFile>.Fail("Die Datei ist zu groß für den Cache.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), timer.Token);
                }

                sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            if (expected is not null && sha != expected)
            {
                return Outcome<CachedFile>.Fail("Die heruntergeladene Datei hat nicht die erwartete SHA-256. Sie wurde verworfen.");
            }

            var path = PathOf(sha);
            if (File.Exists(path))
            {
                File.Delete(temp);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return Outcome<CachedFile>.Fail("Die Quelle ist nicht erreichbar: " + ex.Message);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        var now = DateTime.UtcNow;
        db.CacheFiles.Add(new CacheFile { Sha256 = sha, SourceUrl = sourceUrl, FileName = name, ContentType = contentType, Size = size, LastUsedDate = now, UseCount = 1 });
        await db.SaveChangesAsync(ct);
        await EvictAsync(db, keep: sha, ct);
        return Outcome<CachedFile>.Success(new CachedFile(PathOf(sha), name, contentType, size, sha));
    }

    /// <summary>Removes the files used longest ago until the cache fits its size again (never <paramref name="keep"/>).</summary>
    public static async Task<int> EvictAsync(AppDbContext db, string? keep, CancellationToken ct = default)
    {
        var max = (long)(await SettingService.GetAsync(db, ct)).CacheMaxGigabytes * 1024 * 1024 * 1024;
        var rows = await db.CacheFiles.OrderBy(c => c.LastUsedDate).ToListAsync(ct);
        var total = rows.GroupBy(r => r.Sha256).Sum(g => g.First().Size);
        var removed = 0;
        foreach (var group in rows.GroupBy(r => r.Sha256).OrderBy(g => g.Max(r => r.LastUsedDate)))
        {
            if (total <= max)
            {
                break;
            }

            if (group.Key == keep)
            {
                continue;
            }

            DeleteFile(group.Key);
            db.CacheFiles.RemoveRange(group);
            total -= group.First().Size;
            removed++;
        }

        await db.SaveChangesAsync(ct);
        return removed;
    }

    public static async Task<(IReadOnlyList<CacheListItem> Items, int Total, int Matching, CacheSummary Summary)> ListAsync(AppDbContext db, Actor actor, string? search,
        string sort, bool descending, int page, int pageSize, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var all = db.CacheFiles.AsNoTracking();
        var total = await all.CountAsync(ct);
        var bytes = await all.SumAsync(c => (long?)c.Size, ct) ?? 0;
        var filtered = all;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + UserService.EscapeLike(search.Trim()) + "%";
            filtered = filtered.Where(c => EF.Functions.ILike(c.FileName, pattern, "\\") || EF.Functions.ILike(c.SourceUrl, pattern, "\\") || EF.Functions.ILike(c.Sha256, pattern, "\\"));
        }

        var matching = await filtered.CountAsync(ct);
        filtered = (sort, descending) switch
        {
            ("size", false) => filtered.OrderBy(c => c.Size),
            ("size", true) => filtered.OrderByDescending(c => c.Size),
            ("name", false) => filtered.OrderBy(c => c.FileName),
            ("name", true) => filtered.OrderByDescending(c => c.FileName),
            (_, false) => filtered.OrderBy(c => c.LastUsedDate),
            _ => filtered.OrderByDescending(c => c.LastUsedDate),
        };
        var items = await filtered.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(c => new CacheListItem(c.Id, c.FileName, c.SourceUrl, c.Sha256, c.Size, c.CreateDate, c.LastUsedDate, c.UseCount))
            .ToListAsync(ct);
        var max = (long)(await SettingService.GetAsync(db, ct)).CacheMaxGigabytes * 1024 * 1024 * 1024;
        return (items, total, matching, new CacheSummary(total, bytes, max));
    }

    public static async Task<Outcome> DeleteAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var row = await db.CacheFiles.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is null)
        {
            return Outcome.Missing();
        }

        db.CacheFiles.Remove(row);
        await db.SaveChangesAsync(ct);
        if (!await db.CacheFiles.AnyAsync(c => c.Sha256 == row.Sha256, ct))
        {
            DeleteFile(row.Sha256);
        }

        return Outcome.Success();
    }

    /// <summary>"Cache leeren": every file goes; the next download fetches it again.</summary>
    public static async Task<int> ClearAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var shas = await db.CacheFiles.Select(c => c.Sha256).Distinct().ToListAsync(ct);
        var count = await db.CacheFiles.ExecuteDeleteAsync(ct);
        foreach (var sha in shas)
        {
            DeleteFile(sha);
        }

        return count;
    }

    private static string PathOf(string sha256) => Path.Combine(_directory, sha256);

    private static void DeleteFile(string sha256)
    {
        try
        {
            File.Delete(PathOf(sha256));
        }
        catch (IOException)
        {
            // in use by a download right now: the next clean-up takes it
        }
    }
}
