using System.IO.Compression;
using System.Net;
using Microsoft.Data.Sqlite;
using YamlDotNet.RepresentationModel;

namespace ThixInstall.Cloud.Business;

/// <summary>
/// The search of winget: the official index of the "winget" source (<c>source2.msix</c> of Microsoft's CDN, a zip with the SQLite database
/// <c>Public/index.db</c>: table <c>packages</c> with id, name, moniker and latest version, the tags in <c>tags2</c>/<c>tags2_map</c>). It is kept
/// in <c>/data/sources/winget.db</c> and fetched again once a day. The details of one package (publisher, description, homepage) come from its
/// locale manifest in <c>microsoft/winget-pkgs</c> (raw files, no limit of the GitHub API).
/// </summary>
public static class WingetIndex
{
    public const string IndexUrl = "https://cdn.winget.microsoft.com/cache/source2.msix";

    private const string FileName = "winget.db";
    private const long MaxIndexBytes = 200L * 1024 * 1024;

    /// <summary>The packages whose id, name, moniker or tag fit the query (variants for other languages folded into their main package).</summary>
    public static async Task<IReadOnlyList<SourceHit>> SearchAsync(string query, int max, CancellationToken ct)
    {
        var path = await SourceFiles.GetAsync(FileName, FetchAsync, ct) ?? throw new ResolveException("Der Index von winget ist nicht verfügbar.");
        return Search(path, query, max);
    }

    /// <summary>The search in an index file (pure, for tests).</summary>
    public static IReadOnlyList<SourceHit> Search(string indexFile, string query, int max)
    {
        var q = query.Trim();
        if (q.Length == 0)
        {
            return [];
        }

        var rows = new List<(string Id, string Name, string? Moniker, string Version)>();
        using var connection = Open(indexFile);
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT p.id, p.name, p.moniker, p.latest_version FROM packages p
                WHERE p.id LIKE $like ESCAPE '\' OR p.name LIKE $like ESCAPE '\' OR p.moniker LIKE $like ESCAPE '\'
                   OR p.rowid IN (SELECT m.package FROM tags2_map m JOIN tags2 t ON t.rowid = m.tag WHERE t.tag = $exact)
                LIMIT 2000
                """;
            command.Parameters.AddWithValue("$like", "%" + EscapeLike(q) + "%");
            command.Parameters.AddWithValue("$exact", q.ToLowerInvariant());
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3)));
            }
        }

        // "Mozilla.Firefox.de" is a variant of "Mozilla.Firefox" (same moniker): the list shows the main package only
        var monikers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            monikers.TryAdd(row.Id, row.Moniker);
        }

        return rows
            .Where(r => !IsVariant(r.Id, r.Moniker))
            .Select(r => new
            {
                Row = r,
                Score = SourceSearch.Score(q, SourceSearch.CleanName(r.Name), r.Moniker, r.Id[(r.Id.LastIndexOf('.') + 1)..], r.Id),
            })
            .OrderBy(x => x.Score)
            .ThenBy(x => x.Row.Id.Count(c => c == '.'))
            .ThenBy(x => x.Row.Id.Length)
            .Take(max)
            .Select(x => new SourceHit(SourceKind.Winget, x.Row.Id, SourceSearch.CleanName(x.Row.Name), PublisherOf(x.Row.Id), null, null, x.Row.Version)
            {
                Moniker = x.Row.Moniker,
                Score = x.Score,
                // the tags help to guess the category
                Tags = TagsOf(connection, x.Row.Id),
            })
            .ToList();

        bool IsVariant(string id, string? moniker)
        {
            var dot = id.LastIndexOf('.');
            while (dot > 0)
            {
                var parent = id[..dot];
                if (monikers.TryGetValue(parent, out var parentMoniker) && string.Equals(parentMoniker, moniker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                dot = parent.LastIndexOf('.');
            }

            return false;
        }
    }

    /// <summary>One package of the index by its id; null when the index does not have it.</summary>
    public static async Task<SourceHit?> FindAsync(string id, CancellationToken ct)
    {
        var path = await SourceFiles.GetAsync(FileName, FetchAsync, ct);
        if (path is null)
        {
            return null;
        }

        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT p.id, p.name, p.moniker, p.latest_version FROM packages p WHERE p.id = $id COLLATE NOCASE LIMIT 1";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var hit = new SourceHit(SourceKind.Winget, reader.GetString(0), SourceSearch.CleanName(reader.GetString(1)), PublisherOf(reader.GetString(0)), null, null, reader.GetString(3))
        {
            Moniker = reader.IsDBNull(2) ? null : reader.GetString(2),
        };
        reader.Close();
        return hit with { Tags = TagsOf(connection, hit.Id) };
    }

    private static string TagsOf(SqliteConnection connection, string id)
    {
        using var tags = connection.CreateCommand();
        tags.CommandText = "SELECT t.tag FROM tags2_map m JOIN tags2 t ON t.rowid = m.tag JOIN packages p ON p.rowid = m.package WHERE p.id = $id COLLATE NOCASE";
        tags.Parameters.AddWithValue("$id", id);
        using var reader = tags.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return string.Join(' ', list);
    }

    /// <summary>
    /// The details of a package from its locale manifest (publisher, name, short description, homepage); the hit as it is when the manifest
    /// cannot be read.
    /// </summary>
    public static async Task<SourceHit> DetailsAsync(SourceHit hit, CancellationToken ct)
    {
        var folder = WingetResolver.ManifestPath(hit.Id);
        if (folder is null || hit.Version is null)
        {
            return hit;
        }

        var root = $"https://raw.githubusercontent.com/{WingetResolver.Repository}/master/{folder}/{hit.Version}";
        try
        {
            var version = await RawAsync($"{root}/{hit.Id}.yaml", ct);
            var locale = version is null ? null : WingetResolver.Scalar(Parse(version), "DefaultLocale") ?? "en-US";
            var yaml = locale is null ? null : await RawAsync($"{root}/{hit.Id}.locale.{locale}.yaml", ct);
            if (yaml is null)
            {
                return hit;
            }

            return FromLocale(hit, yaml);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or YamlDotNet.Core.YamlException)
        {
            return hit;
        }
    }

    /// <summary>The details of a locale manifest (pure, for tests).</summary>
    public static SourceHit FromLocale(SourceHit hit, string yaml)
    {
        var node = Parse(yaml);
        var homepage = WingetResolver.Scalar(node, "PackageUrl") ?? WingetResolver.Scalar(node, "PublisherUrl");
        return hit with
        {
            Name = SourceSearch.CleanName(WingetResolver.Scalar(node, "PackageName") ?? hit.Name),
            Publisher = WingetResolver.Scalar(node, "Publisher") ?? hit.Publisher,
            Description = WingetResolver.Scalar(node, "ShortDescription") ?? hit.Description,
            Homepage = Text.IsHttpUrl(homepage) ? homepage : hit.Homepage,
            Tags = node.Children.TryGetValue(new YamlScalarNode("Tags"), out var tags) && tags is YamlSequenceNode list
                ? string.Join(' ', list.Children.OfType<YamlScalarNode>().Select(t => t.Value))
                : hit.Tags,
        };
    }

    /// <summary>The first part of the id stands for the publisher until the manifest names it ("Mozilla.Firefox" → Mozilla).</summary>
    private static string PublisherOf(string id) => id.Split('.')[0];

    private static async Task FetchAsync(string target, CancellationToken ct)
    {
        var msix = target + ".msix";
        try
        {
            await SourceFiles.DownloadAsync(IndexUrl, msix, MaxIndexBytes, TimeSpan.FromMinutes(5), ct);
            using var zip = ZipFile.OpenRead(msix);
            var entry = zip.GetEntry("Public/index.db") ?? throw new InvalidDataException("Das Paket von winget enthält keinen Index.");
            entry.ExtractToFile(target, overwrite: true);
        }
        finally
        {
            File.Delete(msix);
        }
    }

    private static SqliteConnection Open(string path)
    {
        // no pooling: the file is replaced once a day
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static string EscapeLike(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static YamlMappingNode Parse(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return stream.Documents.FirstOrDefault()?.RootNode as YamlMappingNode ?? new YamlMappingNode();
    }

    private static async Task<string?> RawAsync(string url, CancellationToken ct)
    {
        using var response = await Net.GetAsync(url, TimeSpan.FromSeconds(15), ct);
        return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadAsStringAsync(ct) : null;
    }
}
