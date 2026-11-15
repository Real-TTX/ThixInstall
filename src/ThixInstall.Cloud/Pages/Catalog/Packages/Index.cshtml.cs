using Microsoft.AspNetCore.Mvc;
using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Web;

namespace ThixInstall.Cloud.Pages.Catalog.Packages;

/// <summary>The apps of the catalog (administrators). "Alle auflösen" resolves every source now.</summary>
public sealed class IndexModel : AppPageModel
{
    public const int PageSize = 50;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<PackageListItem> Items { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var categories = await CatalogService.CategoriesAsync(Db);
        var category = ListQuery.Text(Request, "category") is { } c && categories.Contains(c) ? c : null;
        var os = ListQuery.Choice(Request, "os", "windows", "macos");
        var visibility = ListQuery.Choice(Request, "show", "public", "private", "featured", "error");
        var sort = ListQuery.Choice(Request, "sort", "name", "category", "updated") ?? "name";
        var descending = ListQuery.Descending(Request, byDefault: sort == "updated");
        var page = ListQuery.Page(Request);

        var (items, total, matching) = await CatalogService.ListPackagesAsync(Db, Actor, new PackageQuery(search, category, os, visibility, sort, descending, page, PageSize));
        Items = items;
        Toolbar = new ToolbarModel
        {
            Id = "packages",
            Action = "/Catalog/Packages",
            Label = "Apps",
            Search = search,
            SearchPlaceholder = "Apps, winget-Ids, Homebrew-Namen durchsuchen",
            Filters =
            [
                new FilterField("category", "Kategorie", categories.Select(x => new FilterOption(x, x)).ToList(), category),
                new FilterField("os", "System", [new("windows", "Windows"), new("macos", "macOS")], os),
                new FilterField("show", "Anzeigen", [new("public", "Öffentlich"), new("private", "Nur angemeldet"), new("featured", "Beliebt"), new("error", "Mit Fehler")], visibility),
            ],
            SortOptions = [new SortOption("name", "Name"), new SortOption("category", "Kategorie"), new SortOption("updated", "Geändert")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "name",
            DefaultDescending = false,
            Total = total,
            Matching = matching,
            Noun = "Apps",
            NounOne = "App",
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = matching, Url = Toolbar.UrlWithPage };
    }

    /// <summary>"Alle auflösen": marks every package as due; the resolver takes them within a minute, one after the other.</summary>
    public async Task<IActionResult> OnPostResolveAllAsync()
    {
        var count = await CatalogService.MarkAllDueAsync(Db, Actor);
        Flash($"{count} Apps werden im Hintergrund neu aufgelöst. Das dauert ein paar Minuten.");
        return Redirect("/Catalog/Packages");
    }
}
