using System.Web;

namespace ThixInstall.Cloud.Web;

public sealed record FilterOption(string Value, string Label);

/// <summary>One filter of the filter dialog: up to four choices are options to tap, more are a select picker; both with "Alle".</summary>
public sealed record FilterField(string Name, string Label, IReadOnlyList<FilterOption> Options, string? Selected);

public sealed record SortOption(string Value, string Label);

/// <summary>
/// Everything the toolbar partial (<c>_Toolbar</c>) needs: the search as a field in the toolbar, Filter and Sort as buttons that open a dialog
/// at its right end (MatDesign LIST-02, LIST-03). All fields belong to ONE GET form, so every dialog sends the whole state. Several toolbars on one
/// page work: every id starts with <see cref="Id"/>.
/// </summary>
public sealed class ToolbarModel
{
    public required string Id { get; init; }

    public required string Action { get; init; }

    /// <summary>Names the list for screen readers ("Verlauf").</summary>
    public string Label { get; init; } = "Liste";

    public string? Search { get; init; }

    public string SearchPlaceholder { get; init; } = "Suchen";

    public IReadOnlyList<FilterField> Filters { get; init; } = [];

    public IReadOnlyList<SortOption> SortOptions { get; init; } = [];

    public string? Sort { get; init; }

    public bool Descending { get; init; }

    public string DefaultSort { get; init; } = string.Empty;

    public bool DefaultDescending { get; init; }

    public int Total { get; init; }

    public int Matching { get; init; }

    /// <summary>The noun in the plural ("Einträge") and for one ("Eintrag").</summary>
    public string Noun { get; init; } = "Einträge";

    public string NounOne { get; init; } = "Eintrag";

    /// <summary>Query values that are neither search, filter nor sort and must survive an "Anwenden".</summary>
    public IReadOnlyDictionary<string, string?> Keep { get; init; } = new Dictionary<string, string?>();

    public string Counted(int n) => n == 1 ? $"1 {NounOne}" : $"{Ui.Number(n)} {Noun}";

    public string CountText => Matching == Total ? Counted(Total) : $"{Ui.Number(Matching)} von {Counted(Total)}";

    public bool SearchActive => !string.IsNullOrWhiteSpace(Search);

    public int ActiveFilters => Filters.Count(f => !string.IsNullOrEmpty(f.Selected));

    public bool SortActive => !string.IsNullOrEmpty(Sort) && (Sort != DefaultSort || Descending != DefaultDescending);

    public bool AnyActive => SearchActive || ActiveFilters > 0;

    public string SortLabel => SortOptions.FirstOrDefault(o => o.Value == (Sort ?? DefaultSort))?.Label ?? string.Empty;

    /// <summary>The address of a page of the list, with every tool in effect.</summary>
    public string UrlWithPage(int page)
    {
        var url = UrlWithout(string.Empty);
        return page <= 1 ? url : url + (url.Contains('?') ? "&" : "?") + "page=" + page;
    }

    /// <summary>The address of the list with no tool in effect (what the list is about, <see cref="Keep"/>, stays).</summary>
    public string UrlReset()
    {
        var query = string.Join("&", Keep.Where(p => !string.IsNullOrEmpty(p.Value)).Select(p => $"{HttpUtility.UrlEncode(p.Key)}={HttpUtility.UrlEncode(p.Value)}"));
        return query.Length == 0 ? Action : $"{Action}?{query}";
    }

    /// <summary>The address of the list with every tool in effect except <paramref name="remove"/>.</summary>
    public string UrlWithout(string remove)
    {
        var pairs = new List<(string, string?)>();
        foreach (var (k, v) in Keep)
        {
            pairs.Add((k, v));
        }

        if (remove != "q" && SearchActive)
        {
            pairs.Add(("q", Search));
        }

        foreach (var f in Filters.Where(f => !string.IsNullOrEmpty(f.Selected) && f.Name != remove))
        {
            pairs.Add((f.Name, f.Selected));
        }

        if (remove != "sort" && SortActive)
        {
            pairs.Add(("sort", Sort));
            pairs.Add(("dir", Descending ? "desc" : "asc"));
        }

        var query = string.Join("&", pairs.Where(p => !string.IsNullOrEmpty(p.Item2)).Select(p => $"{HttpUtility.UrlEncode(p.Item1)}={HttpUtility.UrlEncode(p.Item2)}"));
        return query.Length == 0 ? Action : $"{Action}?{query}";
    }
}

/// <summary>Pagination (MatDesign LIST-06): directly under the list. The page number is a query value, never a route value.</summary>
public sealed class PagerModel
{
    public required int Page { get; init; }

    public required int PageSize { get; init; }

    public required int Total { get; init; }

    public required Func<int, string> Url { get; init; }

    public int Pages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));

    public int From => Total == 0 ? 0 : (Page - 1) * PageSize + 1;

    public int To => Math.Min(Total, Page * PageSize);

    /// <summary>The page numbers to show: first, last and two on each side of the current one; <c>0</c> stands for a gap.</summary>
    public IReadOnlyList<int> Window()
    {
        var shown = new SortedSet<int> { 1, Pages };
        for (var p = Page - 2; p <= Page + 2; p++)
        {
            if (p >= 1 && p <= Pages)
            {
                shown.Add(p);
            }
        }

        var result = new List<int>();
        var last = 0;
        foreach (var p in shown)
        {
            if (last != 0 && p - last > 1)
            {
                result.Add(0);
            }

            result.Add(p);
            last = p;
        }

        return result;
    }
}

/// <summary>The empty state of a list (MatDesign LIST-11): the first item to create, or the tools to reset.</summary>
public sealed record EmptyStateModel(string Icon, string Title, string Text, bool First, string? ActionUrl = null, string? ActionLabel = null, string? ResetUrl = null);

/// <summary>Reading the common list parameters from a query string.</summary>
public static class ListQuery
{
    public static int Page(HttpRequest request) => int.TryParse(request.Query["page"], out var p) && p > 0 ? p : 1;

    public static string? Text(HttpRequest request, string name)
    {
        var value = request.Query[name].ToString().Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>One of the allowed values of a query value, or null.</summary>
    public static string? Choice(HttpRequest request, string name, params string[] allowed)
    {
        var value = Text(request, name);
        return value is not null && allowed.Contains(value, StringComparer.Ordinal) ? value : null;
    }

    public static bool Descending(HttpRequest request, bool byDefault = false)
        => request.Query["dir"].ToString() switch
        {
            var d when string.Equals(d, "desc", StringComparison.OrdinalIgnoreCase) => true,
            var d when string.Equals(d, "asc", StringComparison.OrdinalIgnoreCase) => false,
            _ => byDefault,
        };
}
