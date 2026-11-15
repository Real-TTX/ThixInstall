using Microsoft.AspNetCore.Mvc;
using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Web;

namespace ThixInstall.Cloud.Pages.Admin.Cache;

/// <summary>The download cache: the installers the cloud keeps, how much room they take; one file or all can go.</summary>
public sealed class IndexModel : AppPageModel
{
    public const int PageSize = 50;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<CacheListItem> Items { get; private set; } = [];

    public CacheSummary Summary { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var sort = ListQuery.Choice(Request, "sort", "used", "size", "name") ?? "used";
        var descending = ListQuery.Descending(Request, byDefault: sort != "name");
        var page = ListQuery.Page(Request);
        var (items, total, matching, summary) = await DownloadCache.ListAsync(Db, Actor, search, sort, descending, page, PageSize);
        Items = items;
        Summary = summary;
        Toolbar = new ToolbarModel
        {
            Id = "cache",
            Action = "/Admin/Cache",
            Label = "Download-Cache",
            Search = search,
            SearchPlaceholder = "Dateien, Quellen, SHA-256 durchsuchen",
            SortOptions = [new SortOption("used", "Zuletzt benutzt"), new SortOption("size", "Größe"), new SortOption("name", "Dateiname")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "used",
            DefaultDescending = true,
            Total = total,
            Matching = matching,
            Noun = "Dateien",
            NounOne = "Datei",
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = matching, Url = Toolbar.UrlWithPage };
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        await DownloadCache.DeleteAsync(Db, Actor, id);
        Flash("Datei aus dem Cache entfernt. Der nächste Download holt sie neu.");
        return Redirect("/Admin/Cache");
    }

    public async Task<IActionResult> OnPostClearAsync()
    {
        var count = await DownloadCache.ClearAsync(Db, Actor);
        Flash(count == 0 ? "Der Cache war leer." : $"Cache geleert ({count} Dateien).");
        return Redirect("/Admin/Cache");
    }
}
