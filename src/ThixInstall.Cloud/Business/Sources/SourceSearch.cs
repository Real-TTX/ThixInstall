using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ThixInstall.Cloud.Data;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Business;

/// <summary>The sources "App hinzufügen" searches: three for Windows, two for macOS.</summary>
public enum SourceKind
{
    Winget = 1,
    Store = 2,
    Chocolatey = 3,
    Cask = 4,
    Formula = 5,
}

/// <summary>One package a source knows (positional: what every source has; the rest only some).</summary>
public sealed record SourceHit(SourceKind Kind, string Id, string Name, string? Publisher, string? Description, string? Homepage, string? Version)
{
    public string? IconUrl { get; init; }

    /// <summary>Store apps (MSIX): their package family, how a device recognises them.</summary>
    public string? PackageFamilyName { get; init; }

    public string? Tags { get; init; }

    /// <summary>winget: the short name ("firefox").</summary>
    public string? Moniker { get; init; }

    public long Downloads { get; init; }

    /// <summary>How well the hit fits the query: 0 is the exact name, higher is worse.</summary>
    public double Score { get; init; }

    /// <summary>The app of the catalog that already has this source (it is linked instead of added twice).</summary>
    public long? ExistingPackageId { get; init; }

    public string? ExistingName { get; init; }

    public TargetOs Os => Kind is SourceKind.Cask or SourceKind.Formula ? TargetOs.MacOS : TargetOs.Windows;

    /// <summary>The value of the choice in the form: "Winget:Mozilla.Firefox".</summary>
    public string Value => $"{Kind}:{Id}";
}

/// <summary>A source that could not answer (shown above the results; the others still answer).</summary>
public sealed record SourceProblem(SourceKind Kind, string Message);

/// <summary>The answer of the search: the hits per system, what the search suggests to take, and a category.</summary>
public sealed record SourceSearchResult(string Query, IReadOnlyList<SourceHit> Windows, IReadOnlyList<SourceHit> Mac, IReadOnlyList<SourceProblem> Problems,
    string? SuggestedWindows, string? SuggestedMac, string? SuggestedCategory);

/// <summary>What "App hinzufügen" posts: at most one source per system and the category.</summary>
public sealed record QuickAddInput(string? WindowsPick, string? MacPick, string? Category, bool IsPublic = true);

/// <summary>
/// "App hinzufügen": one search over every source (winget, Microsoft Store, Chocolatey for Windows; Homebrew casks and formulae for macOS), in
/// parallel and each with its own time limit, so a slow source never holds up the others. The administrator takes one hit per system; the cloud
/// then creates the app with what the sources say about it (name, publisher, description, homepage, icon), sets the sources and resolves them.
/// </summary>
public static partial class SourceSearch
{
    public const int MinQueryLength = 2;

    private const int PerSource = 5;
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan KeepResults = TimeSpan.FromMinutes(5);
    private static readonly Lock CacheGate = new();
    private static readonly Dictionary<string, (DateTime At, SourceSearchResult Result)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The searches of the sources (tests put their own in).</summary>
    internal static Func<string, CancellationToken, Task<IReadOnlyList<SourceHit>>> SearchWinget { get; set; } = (q, ct) => WingetIndex.SearchAsync(q, PerSource + 2, ct);

    internal static Func<string, CancellationToken, Task<IReadOnlyList<SourceHit>>> SearchStore { get; set; } = (q, ct) => StoreSource.SearchAsync(q, PerSource, ct);

    internal static Func<string, CancellationToken, Task<IReadOnlyList<SourceHit>>> SearchChocolatey { get; set; } = (q, ct) => ChocolateySource.SearchAsync(q, PerSource, ct);

    internal static Func<string, CancellationToken, Task<IReadOnlyList<SourceHit>>> SearchCasks { get; set; } = (q, ct) => BrewIndex.SearchAsync(q, true, PerSource, ct);

    internal static Func<string, CancellationToken, Task<IReadOnlyList<SourceHit>>> SearchFormulae { get; set; } = (q, ct) => BrewIndex.SearchAsync(q, false, PerSource, ct);

    /// <summary>The details of the hit that is added (tests put their own in).</summary>
    internal static Func<SourceKind, string, CancellationToken, Task<SourceHit?>> Details { get; set; } = DefaultDetails;

    /// <summary>Starts fetching the lists the search reads (when the page opens: the first search is then quick).</summary>
    public static void Warm()
    {
        _ = SearchWinget("warm", CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
        _ = SearchCasks("warm", CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
        _ = SearchFormulae("warm", CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>The search (administrators). The hits that are in the catalog already carry the app they belong to.</summary>
    public static async Task<SourceSearchResult> SearchAsync(AppDbContext db, Actor actor, string? query, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var q = Text.Clean(query, 100) ?? string.Empty;
        if (q.Length < MinQueryLength)
        {
            return new SourceSearchResult(q, [], [], [], null, null, null);
        }

        SourceSearchResult? result = null;
        lock (CacheGate)
        {
            if (Cache.TryGetValue(q, out var hit) && DateTime.UtcNow - hit.At < KeepResults)
            {
                result = hit.Result;
            }
        }

        if (result is null)
        {
            result = await SearchSourcesAsync(q, ct);
            if (result.Problems.Count == 0)
            {
                lock (CacheGate)
                {
                    if (Cache.Count > 100)
                    {
                        Cache.Clear();
                    }

                    Cache[q] = (DateTime.UtcNow, result);
                }
            }
        }

        return await MarkExistingAsync(db, result, ct);
    }

    /// <summary>Forgets the cached answers (tests, a new app).</summary>
    public static void Forget()
    {
        lock (CacheGate)
        {
            Cache.Clear();
        }
    }

    private static async Task<SourceSearchResult> SearchSourcesAsync(string q, CancellationToken ct)
    {
        var problems = new List<SourceProblem>();
        var searches = new (SourceKind Kind, Func<string, CancellationToken, Task<IReadOnlyList<SourceHit>>> Search)[]
        {
            (SourceKind.Winget, SearchWinget), (SourceKind.Store, SearchStore), (SourceKind.Chocolatey, SearchChocolatey),
            (SourceKind.Cask, SearchCasks), (SourceKind.Formula, SearchFormulae),
        };
        var answers = await Task.WhenAll(searches.Select(async s =>
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(SourceTimeout);
            try
            {
                return (IReadOnlyList<SourceHit>)(await s.Search(q, timer.Token).WaitAsync(timer.Token)).Take(PerSource).ToList();
            }
            catch (Exception ex) when (ex is ResolveException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException
                                           or IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException && !ct.IsCancellationRequested)
            {
                var message = ex switch
                {
                    ResolveException => ex.Message,
                    OperationCanceledException => "Die Quelle antwortet gerade nicht rechtzeitig (beim ersten Mal lädt die Cloud ihre Liste – gleich noch einmal suchen).",
                    _ => "Die Quelle ist gerade nicht erreichbar.",
                };
                lock (problems)
                {
                    problems.Add(new SourceProblem(s.Kind, message));
                }

                return [];
            }
        }));

        var windows = answers[0].Concat(answers[1]).Concat(answers[2]).ToList();
        var mac = answers[3].Concat(answers[4]).ToList();
        var bestWindows = windows.OrderBy(h => h.Score + Preference(h.Kind)).FirstOrDefault(h => h.Score < 3);
        var bestMac = BestMatch(bestWindows, mac);
        var category = bestWindows is null && bestMac is null
            ? GuessCategory(windows.Concat(mac).OrderBy(h => h.Score).FirstOrDefault())
            : GuessCategory(bestWindows, bestMac);
        return new SourceSearchResult(q, windows, mac, problems.OrderBy(p => p.Kind).ToList(), bestWindows?.Value, bestMac?.Value, category);
    }

    /// <summary>winget first (the cloud caches its installers), then the Store, then Chocolatey; on macOS casks (apps) before formulae.</summary>
    private static double Preference(SourceKind kind) => kind switch
    {
        SourceKind.Store => 0.3,
        SourceKind.Chocolatey => 0.6,
        SourceKind.Formula => 0.4,
        _ => 0,
    };

    /// <summary>The macOS hit that is the same app as the Windows hit (same name, homepage or short name), else the best one for the query.</summary>
    public static SourceHit? BestMatch(SourceHit? windows, IReadOnlyList<SourceHit> mac)
    {
        if (windows is not null)
        {
            var same = mac.FirstOrDefault(m => Normalize(m.Name) == Normalize(windows.Name)
                                               || (windows.Moniker is not null && string.Equals(m.Id, windows.Moniker, StringComparison.OrdinalIgnoreCase))
                                               || (HostOf(m.Homepage) is { } host && host == HostOf(windows.Homepage)));
            if (same is not null)
            {
                return same;
            }
        }

        return mac.OrderBy(h => h.Score + Preference(h.Kind)).FirstOrDefault(h => h.Score < 3);
    }

    private static async Task<SourceSearchResult> MarkExistingAsync(AppDbContext db, SourceSearchResult result, CancellationToken ct)
    {
        if (result.Windows.Count == 0 && result.Mac.Count == 0)
        {
            return result;
        }

        var packages = await db.Packages.AsNoTracking()
            .Select(p => new { p.Id, p.Name, p.WingetId, p.StoreId, p.ChocoId, p.BrewName, p.MacMethod })
            .ToListAsync(ct);
        SourceHit Mark(SourceHit hit)
        {
            var match = packages.FirstOrDefault(p => hit.Kind switch
            {
                SourceKind.Winget => string.Equals(p.WingetId, hit.Id, StringComparison.OrdinalIgnoreCase),
                SourceKind.Store => string.Equals(p.StoreId, hit.Id, StringComparison.OrdinalIgnoreCase),
                SourceKind.Chocolatey => string.Equals(p.ChocoId, hit.Id, StringComparison.OrdinalIgnoreCase),
                SourceKind.Cask => p.MacMethod == InstallMethod.HomebrewCask && string.Equals(p.BrewName, hit.Id, StringComparison.OrdinalIgnoreCase),
                SourceKind.Formula => p.MacMethod == InstallMethod.Homebrew && string.Equals(p.BrewName, hit.Id, StringComparison.OrdinalIgnoreCase),
                _ => false,
            });
            return match is null ? hit : hit with { ExistingPackageId = match.Id, ExistingName = match.Name };
        }

        var windows = result.Windows.Select(Mark).ToList();
        var mac = result.Mac.Select(Mark).ToList();
        // what is in the catalog already is not suggested again
        var suggestedWindows = windows.FirstOrDefault(h => h.Value == result.SuggestedWindows && h.ExistingPackageId is null)?.Value;
        var suggestedMac = mac.FirstOrDefault(h => h.Value == result.SuggestedMac && h.ExistingPackageId is null)?.Value;
        return result with { Windows = windows, Mac = mac, SuggestedWindows = suggestedWindows, SuggestedMac = suggestedMac };
    }

    // ---- adding ----------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Creates the app from the chosen hits (administrators): name, publisher, description and homepage from the sources, the sources set, an icon
    /// (Chocolatey's, else the homepage's), the sources resolved. Returns the new app and the message for the person.
    /// </summary>
    public static async Task<Outcome<(long Id, string Message)>> AddAsync(AppDbContext db, Actor actor, QuickAddInput input, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var windowsPick = ParsePick(input.WindowsPick);
        var macPick = ParsePick(input.MacPick);
        if (windowsPick is { } w && w.Kind is SourceKind.Cask or SourceKind.Formula || macPick is { } m && m.Kind is not (SourceKind.Cask or SourceKind.Formula))
        {
            return Outcome<(long, string)>.Fail("Diese Quelle passt nicht zum System.", "WindowsPick");
        }

        if (windowsPick is null && macPick is null)
        {
            return Outcome<(long, string)>.Fail("Bitte für Windows oder macOS einen Treffer wählen.", "WindowsPick");
        }

        var category = Text.Clean(input.Category, 60);
        if (category is null)
        {
            return Outcome<(long, string)>.Fail("Bitte eine Kategorie angeben.", "Category");
        }

        SourceHit? windows = null;
        SourceHit? mac = null;
        try
        {
            windows = windowsPick is { } wp ? await Details(wp.Kind, wp.Id, ct) ?? throw new ResolveException($"„{wp.Id}“ gibt es bei {KindName(wp.Kind)} nicht (mehr).") : null;
            mac = macPick is { } mp ? await Details(mp.Kind, mp.Id, ct) ?? throw new ResolveException($"„{mp.Id}“ gibt es bei {KindName(mp.Kind)} nicht (mehr).") : null;
        }
        catch (Exception ex) when (ex is ResolveException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Outcome<(long, string)>.Fail(ex is ResolveException ? ex.Message : "Die Quelle ist gerade nicht erreichbar. Bitte noch einmal versuchen.", "WindowsPick");
        }

        var marked = await MarkExistingAsync(db, new SourceSearchResult(string.Empty, windows is null ? [] : [windows], mac is null ? [] : [mac], [], null, null, null), ct);
        if (marked.Windows.Concat(marked.Mac).FirstOrDefault(h => h.ExistingPackageId is not null) is { } existing)
        {
            return Outcome<(long, string)>.Fail($"{KindName(existing.Kind)} „{existing.Id}“ hat schon die App „{existing.ExistingName}“ im Katalog.", "WindowsPick");
        }

        var main = windows ?? mac!;
        var name = Text.Clean(main.Name, 120)!;
        var homepage = new[] { windows?.Homepage, mac?.Homepage }.FirstOrDefault(Text.IsHttpUrl);
        var package = new Package
        {
            Key = await FreeKeyAsync(db, KeyCandidate(windows, mac, name), ct),
            Name = name,
            Publisher = Text.Clean(windows?.Publisher ?? mac?.Publisher, 120),
            Description = Text.Clean(windows?.Description ?? mac?.Description, 1000),
            Category = category,
            Homepage = Text.Clean(homepage, 500),
            IsPublic = input.IsPublic,
        };
        switch (windows?.Kind)
        {
            case SourceKind.Winget:
                package.WindowsMethod = InstallMethod.Winget;
                package.WingetId = windows.Id;
                break;
            case SourceKind.Store:
                package.WindowsMethod = InstallMethod.MicrosoftStore;
                package.StoreId = windows.Id;
                package.StorePackageFamilyName = Text.Clean(windows.PackageFamilyName, 200);
                break;
            case SourceKind.Chocolatey:
                package.WindowsMethod = InstallMethod.Chocolatey;
                package.ChocoId = windows.Id;
                break;
        }

        if (mac is not null)
        {
            package.MacMethod = mac.Kind == SourceKind.Cask ? InstallMethod.HomebrewCask : InstallMethod.Homebrew;
            package.BrewName = mac.Id;
        }

        db.Packages.Add(package);
        await db.SaveChangesAsync(ct);
        Forget();

        await IconAsync(db, package, windows, mac, ct);
        var errors = await ReleaseService.ResolveAsync(db, package.Id, ct);
        var message = errors.Count == 0 ? $"{name} ist im Katalog, die Quellen sind aufgelöst." : $"{name} ist im Katalog. " + string.Join(" ", errors.Distinct());
        return Outcome<(long, string)>.Success((package.Id, message));
    }

    /// <summary>"Winget:Mozilla.Firefox" → (Winget, Mozilla.Firefox); null for nothing or nonsense.</summary>
    public static (SourceKind Kind, string Id)? ParsePick(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !Enum.TryParse<SourceKind>(value[..colon], out var kind) || !Enum.IsDefined(kind))
        {
            return null;
        }

        var id = value[(colon + 1)..].Trim();
        var valid = kind switch
        {
            SourceKind.Winget => WingetResolver.ManifestPath(id) is not null,
            SourceKind.Store => StoreSource.IsValidId(id),
            SourceKind.Chocolatey => ChocolateySource.IsValidId(id),
            _ => BrewResolver.IsValidName(id),
        };
        return valid ? (kind, id) : null;
    }

    /// <summary>What the source says about one package (null when it does not know it).</summary>
    internal static async Task<SourceHit?> DefaultDetails(SourceKind kind, string id, CancellationToken ct)
    {
        switch (kind)
        {
            case SourceKind.Winget:
                return await WingetIndex.FindAsync(id, ct) is { } hit ? await WingetIndex.DetailsAsync(hit, ct) : null;
            case SourceKind.Store:
                return await StoreSource.ManifestAsync(id, ct) is { } manifest
                    ? new SourceHit(SourceKind.Store, manifest.Id, manifest.Name, manifest.Publisher, manifest.Description, manifest.Homepage, manifest.Version)
                    {
                        PackageFamilyName = manifest.PackageFamilyName,
                    }
                    : null;
            case SourceKind.Chocolatey:
                return await ChocolateySource.FindAsync(id, ct);
            case SourceKind.Cask:
                return await BrewIndex.FindAsync(id, true, ct);
            case SourceKind.Formula:
                return await BrewIndex.FindAsync(id, false, ct);
            default:
                return null;
        }
    }

    /// <summary>An icon for the new app: Chocolatey's (also when Chocolatey has the app under the same short name), else the homepage's.</summary>
    private static async Task IconAsync(AppDbContext db, Package package, SourceHit? windows, SourceHit? mac, CancellationToken ct)
    {
        var iconUrl = windows?.IconUrl;
        if (iconUrl is null)
        {
            foreach (var guess in new[] { windows?.Moniker, mac?.Id, package.Key }.Where(g => ChocolateySource.IsValidId(g)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (await ChocolateySource.FindAsync(guess!, ct) is { IconUrl: { } url } choco && NamesMatch(choco.Name, package.Name))
                    {
                        iconUrl = url;
                        break;
                    }
                }
                catch (Exception ex) when (ex is ResolveException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        if (iconUrl is not null && await IconFetcher.DownloadAsync(iconUrl, ct) is { } icon)
        {
            await CatalogService.StoreIconAsync(db, package.Id, icon.Data, icon.Type, ct);
            await db.Packages.Where(p => p.Id == package.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.IconFetchDate, DateTime.UtcNow), ct);
            return;
        }

        // the homepage: what the background job would do later, now
        await IconFetcher.RunForAsync(db, package.Id, ct);
    }

    private static bool NamesMatch(string a, string b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        return x.Length > 0 && y.Length > 0 && (x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal));
    }

    private static string KeyCandidate(SourceHit? windows, SourceHit? mac, string name)
    {
        var candidates = new[] { windows?.Moniker, mac?.Kind == SourceKind.Cask ? mac.Id : null, windows?.Kind == SourceKind.Chocolatey ? windows.Id : null, Text.Slug(name) };
        return candidates.Select(c => c is null ? null : Text.Slug(c)).FirstOrDefault(Text.IsKey) ?? "app";
    }

    private static async Task<string> FreeKeyAsync(AppDbContext db, string key, CancellationToken ct)
    {
        var candidate = key;
        for (var n = 2; await db.Packages.AnyAsync(p => p.Key == candidate, ct); n++)
        {
            var suffix = "-" + n;
            candidate = key[..Math.Min(key.Length, 64 - suffix.Length)] + suffix;
        }

        return candidate;
    }

    // ---- matching and wording --------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// How well a hit fits the query (lower is better): 0 the exact short name, 1 the exact name, 2 the start of the name, 3 inside the name or id,
    /// 4 elsewhere (tags, description); a little more for longer names.
    /// </summary>
    public static double Score(string query, string name, string? token, string? lastSegment, string id)
    {
        var q = query.Trim();
        double score;
        if (string.Equals(token, q, StringComparison.OrdinalIgnoreCase) || string.Equals(lastSegment, q, StringComparison.OrdinalIgnoreCase))
        {
            score = 0;
        }
        else if (Normalize(name) == Normalize(q))
        {
            score = 1;
        }
        else if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase) || (token?.StartsWith(q, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            score = 2;
        }
        else if (name.Contains(q, StringComparison.OrdinalIgnoreCase) || id.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            score = 3;
        }
        else
        {
            score = 4;
        }

        return score + Math.Min(0.4, Math.Max(0, name.Length - q.Length) / 100.0);
    }

    /// <summary>
    /// "Mozilla Firefox (en-US)" → "Mozilla Firefox", "IrfanView (Install)" → "IrfanView": the language of the installer and the kind of a Chocolatey
    /// package are not part of the name (and would keep a device from recognising the app).
    /// </summary>
    public static string CleanName(string name) => NameSuffix().Replace(name.Trim(), string.Empty);

    /// <summary>The category the hits (one app) most likely belong to (from their tags, names and descriptions); "Werkzeuge" when nothing fits.</summary>
    public static string? GuessCategory(params SourceHit?[] hits)
    {
        if (hits.All(h => h is null))
        {
            return null;
        }

        var text = " " + string.Join(' ', hits.Where(h => h is not null).Select(h => $"{h!.Tags} {h.Name} {h.Description}")).ToLowerInvariant() + " ";
        foreach (var (category, words) in CategoryWords)
        {
            if (words.Any(w => text.Contains(w, StringComparison.Ordinal)))
            {
                return category;
            }
        }

        return "Werkzeuge";
    }

    private static readonly (string Category, string[] Words)[] CategoryWords =
    [
        ("Browser", [" browser", "web-browser", "webbrowser"]),
        ("Kommunikation", ["messenger", " messag", " chat", "video call", "videoconferenc", " meeting", " email", " e-mail", " mail client", "voip", "whatsapp",
            "telegram", " slack", " teams", " zoom", "discord"]),
        ("Entwicklung", [" developer", " development", " ide ", " code editor", "programming", " git ", " sdk", " compiler", " terminal"]),
        ("Sicherheit", [" password", " vpn", "antivirus", " security", " encryption", " firewall"]),
        ("Cloud-Speicher", ["cloud storage", " sync ", " backup", "file hosting", "dropbox", "onedrive", "nextcloud"]),
        ("Office", [" office", " pdf", " document", " spreadsheet", " word processor", " notes", "presentation"]),
        ("Medien", [" media player", " video", " audio", " music", " player", " streaming", " podcast"]),
        ("Bilder", [" image", " photo", " graphics", " drawing", " screenshot", " paint"]),
        ("Spiele", [" game", " gaming"]),
        ("Laufzeitumgebungen", [" runtime", " java ", " jdk", " .net", " python", " node.js", " redistributable"]),
        ("System", [" driver", " system ", " disk ", " hardware", " monitor"]),
    ];

    public static string KindName(SourceKind kind) => kind switch
    {
        SourceKind.Winget => "winget",
        SourceKind.Store => "Microsoft Store",
        SourceKind.Chocolatey => "Chocolatey",
        SourceKind.Cask => "Homebrew-Cask",
        SourceKind.Formula => "Homebrew-Formel",
        _ => kind.ToString(),
    };

    private static string Normalize(string? text) => new((text ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string? HostOf(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant().Replace("www.", string.Empty, StringComparison.Ordinal) : null;

    [GeneratedRegex(@"\s*\((?:[a-z]{2,3}(?:-[A-Za-z]{2,8})?|[Ii]nstall|[Ii]nstaller|[Pp]ortable)\)$")]
    private static partial Regex NameSuffix();
}
