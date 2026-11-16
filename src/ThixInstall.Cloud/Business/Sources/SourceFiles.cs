using System.Collections.Concurrent;

namespace ThixInstall.Cloud.Business;

/// <summary>
/// The lists of the sources the search of "App hinzufügen" reads locally (the index of winget, the casks and formulae of Homebrew), kept in
/// <c>/data/sources</c>. A list is fetched when it is missing and again when it is older than a day; while a newer one is fetched, the old one
/// is still used. One fetch per list at a time.
/// </summary>
public static class SourceFiles
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Task> Refreshing = new(StringComparer.Ordinal);
    private static string _directory = "/data/sources";

    public static string Directory => _directory;

    public static void UseDirectory(string directory)
    {
        _directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public static string PathOf(string name) => Path.Combine(_directory, name);

    /// <summary>
    /// The file <paramref name="name"/>: when it exists it is returned at once (and fetched again in the background when it is older than a day);
    /// when it is missing it is fetched now. <paramref name="fetch"/> writes the new file to the path it gets. Null when there is none.
    /// </summary>
    public static async Task<string?> GetAsync(string name, Func<string, CancellationToken, Task> fetch, CancellationToken ct)
    {
        var path = PathOf(name);
        if (File.Exists(path))
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > MaxAge)
            {
                // the old list serves this search; the new one the next
                _ = Refresh(name, fetch);
            }

            return path;
        }

        // the fetch goes on when this search gives up waiting (the next search finds the list)
        await Refresh(name, fetch).WaitAsync(ct);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Starts fetching a list (once at a time) without waiting.</summary>
    public static Task Refresh(string name, Func<string, CancellationToken, Task> fetch)
        => Refreshing.GetOrAdd(name, key => Task.Run(async () =>
        {
            try
            {
                await FetchAsync(key, fetch, CancellationToken.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidDataException or ResolveException
                                           or UnauthorizedAccessException)
            {
                // the next search tries again
            }
            finally
            {
                Refreshing.TryRemove(name, out _);
            }
        }));

    private static async Task FetchAsync(string name, Func<string, CancellationToken, Task> fetch, CancellationToken ct)
    {
        var gate = Locks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var path = PathOf(name);
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) <= MaxAge)
            {
                return;
            }

            System.IO.Directory.CreateDirectory(_directory);
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await fetch(temp, ct);
                File.Move(temp, path, overwrite: true);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Downloads <paramref name="url"/> into <paramref name="target"/> (at most <paramref name="maxBytes"/>).</summary>
    public static async Task DownloadAsync(string url, string target, long maxBytes, TimeSpan timeout, CancellationToken ct)
    {
        using var response = await Net.GetAsync(url, timeout, ct, completion: HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            throw new ResolveException($"{new Uri(url).Host} antwortet mit {(int)response.StatusCode}.");
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);
        await using var source = await response.Content.ReadAsStreamAsync(timer.Token);
        await using var file = File.Create(target);
        var buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, timer.Token)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new ResolveException("Die Liste der Quelle ist unerwartet groß.");
            }

            await file.WriteAsync(buffer.AsMemory(0, read), timer.Token);
        }
    }
}
