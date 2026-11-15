using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Web;

namespace ThixInstall.Cloud.Pages.Bundles;

/// <summary>"Pakete": the bundles a person made (an administrator also every one, and the anonymous ones of the start page).</summary>
public sealed class IndexModel : AppPageModel
{
    public const int PageSize = 50;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<BundleListItem> Items { get; private set; } = [];

    public bool ShowOwner { get; private set; }

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var os = ListQuery.Choice(Request, "os", "windows", "macos");
        var scope = Me.IsAdmin ? ListQuery.Choice(Request, "scope", "all", "anonymous") : null;
        var sort = ListQuery.Choice(Request, "sort", "created", "name", "downloads") ?? "created";
        var descending = ListQuery.Descending(Request, byDefault: sort != "name");
        var page = ListQuery.Page(Request);
        ShowOwner = scope is not null;

        var (items, total, matching) = await BundleService.ListAsync(Db, Actor, search, os, scope, sort, descending, page, PageSize);
        Items = items;
        var filters = new List<FilterField> { new("os", "System", [new("windows", "Windows"), new("macos", "macOS")], os) };
        if (Me.IsAdmin)
        {
            filters.Add(new FilterField("scope", "Von", [new("all", "Allen"), new("anonymous", "Besuchern ohne Konto")], scope));
        }

        Toolbar = new ToolbarModel
        {
            Id = "bundles",
            Action = "/Bundles",
            Label = "Pakete",
            Search = search,
            SearchPlaceholder = "Pakete durchsuchen",
            Filters = filters,
            SortOptions = [new SortOption("created", "Angelegt"), new SortOption("name", "Name"), new SortOption("downloads", "Downloads")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "created",
            DefaultDescending = true,
            Total = total,
            Matching = matching,
            Noun = "Pakete",
            NounOne = "Paket",
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = matching, Url = Toolbar.UrlWithPage };
    }
}
