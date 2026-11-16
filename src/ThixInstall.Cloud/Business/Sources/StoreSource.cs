using System.Net;
using System.Text;
using System.Text.Json;

namespace ThixInstall.Cloud.Business;

/// <summary>What the manifest of a Store app says (for the resolver and the details of "App hinzufügen").</summary>
public sealed record StoreManifest(string Id, string Name, string? Publisher, string? Description, string? Homepage, string? Version, string? PackageFamilyName);

/// <summary>
/// The Microsoft Store as winget sees it ("msstore"): the search (<c>manifestSearch</c>) and the manifest of an app (<c>packageManifests/{id}</c>)
/// of Microsoft's Store service. Store ids: "9NKSQGP7F2NH" (an MSIX app, recognised by its package family) or "XP…" (a classic program, recognised
/// by its name under "Installierte Apps"). The device installs with "winget install --id &lt;id&gt; --source msstore" as the signed-in user.
/// </summary>
public static class StoreSource
{
    public const string ApiBase = "https://storeedgefd.dsx.mp.microsoft.com/v9.0";

    public static bool IsValidId(string? id) => id is { Length: >= 9 and <= 40 } && id.All(char.IsAsciiLetterOrDigit);

    public static async Task<IReadOnlyList<SourceHit>> SearchAsync(string query, int max, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { Query = new { KeyWord = query.Trim(), MatchType = "Substring" }, MaximumResults = Math.Clamp(max, 1, 50) });
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + "/manifestSearch") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var response = await Net.Client.SendAsync(request, timer.Token);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ResolveException($"Der Microsoft Store antwortet mit {(int)response.StatusCode}.");
        }

        return ParseSearch(await response.Content.ReadAsStringAsync(timer.Token), query);
    }

    /// <summary>The hits of an answer of <c>manifestSearch</c> (pure, for tests).</summary>
    public static IReadOnlyList<SourceHit> ParseSearch(string json, string query)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var hits = new List<SourceHit>();
        foreach (var item in data.EnumerateArray())
        {
            var id = Str(item, "PackageIdentifier");
            var name = Str(item, "PackageName");
            if (!IsValidId(id) || name is null)
            {
                continue;
            }

            string? version = null;
            string? family = null;
            if (item.TryGetProperty("Versions", out var versions) && versions.ValueKind == JsonValueKind.Array && versions.GetArrayLength() > 0)
            {
                var first = versions[0];
                version = CleanVersion(Str(first, "PackageVersion"));
                if (first.TryGetProperty("PackageFamilyNames", out var families) && families.ValueKind == JsonValueKind.Array && families.GetArrayLength() > 0)
                {
                    family = families[0].GetString();
                }
            }

            hits.Add(new SourceHit(SourceKind.Store, id!, name, Str(item, "Publisher"), null, null, version)
            {
                PackageFamilyName = family,
                Score = SourceSearch.Score(query, name, null, null, id!),
            });
        }

        return hits.OrderBy(h => h.Score).ToList();
    }

    /// <summary>The manifest of a Store app; null when the Store does not know the id.</summary>
    public static async Task<StoreManifest?> ManifestAsync(string id, CancellationToken ct)
    {
        if (!IsValidId(id))
        {
            throw new ResolveException($"„{id}“ ist keine gültige Store-Id (z. B. 9NKSQGP7F2NH).");
        }

        using var response = await Net.GetAsync($"{ApiBase}/packageManifests/{Uri.EscapeDataString(id)}?Market=DE", TimeSpan.FromSeconds(20), ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ResolveException($"Der Microsoft Store antwortet mit {(int)response.StatusCode}.");
        }

        return ParseManifest(await response.Content.ReadAsStringAsync(ct), id);
    }

    /// <summary>The manifest of an answer of <c>packageManifests</c> (pure, for tests).</summary>
    public static StoreManifest? ParseManifest(string json, string id)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Data", out var data) || !data.TryGetProperty("Versions", out var versions)
            || versions.ValueKind != JsonValueKind.Array || versions.GetArrayLength() == 0)
        {
            return null;
        }

        var version = versions[0];
        var locale = version.TryGetProperty("DefaultLocale", out var l) ? l : default;
        string? family = null;
        if (version.TryGetProperty("Installers", out var installers) && installers.ValueKind == JsonValueKind.Array)
        {
            family = installers.EnumerateArray().Select(i => Str(i, "PackageFamilyName")).FirstOrDefault(f => !string.IsNullOrWhiteSpace(f));
        }

        var name = locale.ValueKind == JsonValueKind.Object ? Str(locale, "PackageName") : null;
        var homepage = locale.ValueKind == JsonValueKind.Object ? Str(locale, "PublisherUrl") : null;
        return new StoreManifest(
            id,
            name ?? id,
            locale.ValueKind == JsonValueKind.Object ? Str(locale, "Publisher") : null,
            locale.ValueKind == JsonValueKind.Object ? Str(locale, "ShortDescription") ?? Str(locale, "Description") : null,
            Text.IsHttpUrl(homepage) ? homepage : null,
            CleanVersion(Str(version, "PackageVersion")),
            family);
    }

    /// <summary>The Store says "Unknown" for apps that update themselves: no version, no updates by ThixInstall.</summary>
    private static string? CleanVersion(string? version)
        => string.IsNullOrWhiteSpace(version) || version.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? null : version.Trim();

    private static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
