using System.Text.Json;

namespace ThixInstall.Cloud.Business;

/// <summary>
/// The search of Homebrew: the complete lists of casks and formulae of formulae.brew.sh (<c>cask.json</c>, <c>formula.json</c>), kept in
/// <c>/data/sources</c>, fetched again once a day, and held in memory as a compact list (name, names, description, homepage, version).
/// </summary>
public static class BrewIndex
{
    private const long MaxListBytes = 300L * 1024 * 1024;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<bool, (DateTime Written, IReadOnlyList<BrewEntry> Entries)> Loaded = [];

    /// <summary>One cask or formula.</summary>
    public sealed record BrewEntry(string Token, string Name, IReadOnlyList<string> Names, string? Description, string? Homepage, string? Version);

    public static async Task<IReadOnlyList<SourceHit>> SearchAsync(string query, bool cask, int max, CancellationToken ct)
        => Search(await EntriesAsync(cask, ct), query, cask, max);

    /// <summary>A cask or formula by its name; null when Homebrew does not have it.</summary>
    public static async Task<SourceHit?> FindAsync(string token, bool cask, CancellationToken ct)
    {
        var entry = (await EntriesAsync(cask, ct)).FirstOrDefault(e => string.Equals(e.Token, token, StringComparison.OrdinalIgnoreCase));
        return entry is null ? null : ToHit(entry, cask, 0);
    }

    /// <summary>The search in a list (pure, for tests).</summary>
    public static IReadOnlyList<SourceHit> Search(IReadOnlyList<BrewEntry> entries, string query, bool cask, int max)
    {
        var q = query.Trim();
        if (q.Length == 0)
        {
            return [];
        }

        return entries
            .Where(e => e.Token.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Names.Any(n => n.Contains(q, StringComparison.OrdinalIgnoreCase))
                        || (e.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(e => (Entry: e, Score: SourceSearch.Score(q, e.Name, e.Token, null, e.Token) + (e.Token.Contains('@') ? 0.5 : 0)))
            .OrderBy(x => x.Score)
            .ThenBy(x => x.Entry.Token.Length)
            .Take(max)
            .Select(x => ToHit(x.Entry, cask, x.Score))
            .ToList();
    }

    /// <summary>The compact list of <c>cask.json</c> or <c>formula.json</c> (pure, for tests).</summary>
    public static IReadOnlyList<BrewEntry> ParseList(Stream json, bool cask)
    {
        using var document = JsonDocument.Parse(json);
        var entries = new List<BrewEntry>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var token = Str(item, cask ? "token" : "name");
            if (token is null || !BrewResolver.IsValidName(token))
            {
                continue;
            }

            if (item.TryGetProperty("deprecated", out var deprecated) && deprecated.ValueKind == JsonValueKind.True
                || item.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            var names = new List<string>();
            if (cask && item.TryGetProperty("name", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                names.AddRange(list.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!).Where(n => n.Length > 0));
            }

            var version = cask
                ? Str(item, "version")
                : item.TryGetProperty("versions", out var versions) ? Str(versions, "stable") : null;
            var homepage = Str(item, "homepage");
            entries.Add(new BrewEntry(token, names.FirstOrDefault() ?? token, names, Str(item, "desc"), Text.IsHttpUrl(homepage) ? homepage : null,
                version is "latest" ? null : version));
        }

        return entries;
    }

    private static SourceHit ToHit(BrewEntry e, bool cask, double score)
        => new(cask ? SourceKind.Cask : SourceKind.Formula, e.Token, e.Name, null, e.Description, e.Homepage, e.Version) { Score = score };

    private static async Task<IReadOnlyList<BrewEntry>> EntriesAsync(bool cask, CancellationToken ct)
    {
        var name = cask ? "brew-cask.json" : "brew-formula.json";
        var path = await SourceFiles.GetAsync(name, (target, token) =>
            SourceFiles.DownloadAsync($"{BrewResolver.ApiBase}/{(cask ? "cask" : "formula")}.json", target, MaxListBytes, TimeSpan.FromMinutes(5), token), ct)
            ?? throw new ResolveException("Die Liste von Homebrew ist nicht verfügbar.");
        var written = File.GetLastWriteTimeUtc(path);
        await Gate.WaitAsync(ct);
        try
        {
            if (Loaded.TryGetValue(cask, out var hit) && hit.Written == written)
            {
                return hit.Entries;
            }

            IReadOnlyList<BrewEntry> entries;
            await using (var stream = File.OpenRead(path))
            {
                entries = ParseList(stream, cask);
            }

            Loaded[cask] = (written, entries);
            return entries;
        }
        catch (JsonException)
        {
            File.Delete(path);
            throw new ResolveException("Die Liste von Homebrew war beschädigt; sie wird neu geladen.");
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
