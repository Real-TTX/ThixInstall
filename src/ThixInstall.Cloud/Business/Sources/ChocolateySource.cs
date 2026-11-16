using System.Xml;
using System.Xml.Linq;

namespace ThixInstall.Cloud.Business;

/// <summary>
/// The Chocolatey community repository (OData feed of community.chocolatey.org): the search of "App hinzufügen" and the latest version of a
/// package for the resolver. Chocolatey itself stays the installer on the device (a package is a script that fetches the vendor's setup).
/// </summary>
public static class ChocolateySource
{
    public const string ApiBase = "https://community.chocolatey.org/api/v2";

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace D = "http://schemas.microsoft.com/ado/2007/08/dataservices";
    private static readonly XNamespace M = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";

    public static bool IsValidId(string? id) => id is { Length: > 0 and <= 200 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    public static async Task<IReadOnlyList<SourceHit>> SearchAsync(string query, int max, CancellationToken ct)
    {
        var term = query.Trim().Replace("'", "''", StringComparison.Ordinal);
        var url = $"{ApiBase}/Search()?$filter=IsLatestVersion&searchTerm='{Uri.EscapeDataString(term)}'&targetFramework=''&includePrerelease=false&$top={Math.Clamp(max, 1, 50)}";
        return Parse(await GetAsync(url, ct), query);
    }

    /// <summary>The latest version of a package; null when the repository does not know it.</summary>
    public static async Task<SourceHit?> FindAsync(string id, CancellationToken ct)
    {
        if (!IsValidId(id))
        {
            throw new ResolveException($"„{id}“ ist keine gültige Chocolatey-Id.");
        }

        var filter = Uri.EscapeDataString($"Id eq '{id}' and IsLatestVersion");
        return Parse(await GetAsync($"{ApiBase}/Packages()?$filter={filter}", ct), id).FirstOrDefault(h => string.Equals(h.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The packages of an Atom feed of the repository (pure, for tests).</summary>
    public static IReadOnlyList<SourceHit> Parse(string xml, string query)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            throw new ResolveException("Chocolatey antwortet nicht verständlich.");
        }

        var hits = new List<SourceHit>();
        foreach (var entry in document.Root?.Elements(Atom + "entry") ?? [])
        {
            var id = entry.Element(Atom + "title")?.Value.Trim();
            var properties = entry.Element(M + "properties");
            if (!IsValidId(id) || properties is null)
            {
                continue;
            }

            string? P(string name) => properties.Element(D + name)?.Value is { Length: > 0 } v ? v.Trim() : null;
            var title = SourceSearch.CleanName(P("Title") ?? id!);
            var homepage = P("ProjectUrl");
            var icon = P("IconUrl");
            hits.Add(new SourceHit(SourceKind.Chocolatey, id!, title, entry.Element(Atom + "author")?.Element(Atom + "name")?.Value.Trim(),
                entry.Element(Atom + "summary")?.Value is { Length: > 0 } summary ? summary.Trim() : FirstLine(P("Description")),
                Text.IsHttpUrl(homepage) ? homepage : null, P("Version"))
            {
                IconUrl = Text.IsHttpUrl(icon) ? icon : null,
                Tags = P("Tags"),
                Downloads = long.TryParse(P("DownloadCount"), out var downloads) ? downloads : 0,
                Score = SourceSearch.Score(query, title, id, null, id!),
            });
        }

        // the repository orders by its own relevance; the score puts the exact names first, downloads decide the rest
        return hits.OrderBy(h => h.Score).ThenByDescending(h => h.Downloads).ToList();
    }

    private static string? FirstLine(string? text)
        => text is null ? null : Text.Clip(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(), 300);

    private static async Task<string> GetAsync(string url, CancellationToken ct)
    {
        using var response = await Net.GetAsync(url, TimeSpan.FromSeconds(15), ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new ResolveException($"Chocolatey antwortet mit {(int)response.StatusCode}.");
        }

        return await response.Content.ReadAsStringAsync(ct);
    }
}
