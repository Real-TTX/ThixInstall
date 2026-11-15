using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Web;

namespace ThixInstall.Cloud.Pages.Catalog.Scripts;

/// <summary>The scripts ("Makros") of the catalog (administrators).</summary>
public sealed class IndexModel : AppPageModel
{
    public const int PageSize = 50;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<ScriptListItem> Items { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var os = ListQuery.Choice(Request, "os", "windows", "macos");
        var context = ListQuery.Choice(Request, "context", "system", "user");
        var sort = ListQuery.Choice(Request, "sort", "name", "updated") ?? "name";
        var descending = ListQuery.Descending(Request, byDefault: sort == "updated");
        var page = ListQuery.Page(Request);

        var (items, total, matching) = await CatalogService.ListScriptsAsync(Db, Actor, new ScriptQuery(search, os, context, sort, descending, page, PageSize));
        Items = items;
        Toolbar = new ToolbarModel
        {
            Id = "scripts",
            Action = "/Catalog/Scripts",
            Label = "Skripte",
            Search = search,
            SearchPlaceholder = "Skripte durchsuchen",
            Filters =
            [
                new FilterField("os", "System", [new("windows", "Windows"), new("macos", "macOS")], os),
                new FilterField("context", "Ausführen als", [new("system", "System"), new("user", "Benutzer")], context),
            ],
            SortOptions = [new SortOption("name", "Name"), new SortOption("updated", "Geändert")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "name",
            DefaultDescending = false,
            Total = total,
            Matching = matching,
            Noun = "Skripte",
            NounOne = "Skript",
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = matching, Url = Toolbar.UrlWithPage };
    }
}
