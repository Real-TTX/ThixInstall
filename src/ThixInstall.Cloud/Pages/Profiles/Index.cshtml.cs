using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Web;

namespace ThixInstall.Cloud.Pages.Profiles;

/// <summary>The profiles (administrators).</summary>
public sealed class IndexModel : AppPageModel
{
    public const int PageSize = 50;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<ProfileListItem> Items { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var sort = ListQuery.Choice(Request, "sort", "name", "updated") ?? "name";
        var descending = ListQuery.Descending(Request, byDefault: sort == "updated");
        var page = ListQuery.Page(Request);
        var (items, total, matching) = await ProfileService.ListAsync(Db, Actor, search, sort, descending, page, PageSize);
        Items = items;
        Toolbar = new ToolbarModel
        {
            Id = "profiles",
            Action = "/Profiles",
            Label = "Profile",
            Search = search,
            SearchPlaceholder = "Profile durchsuchen",
            SortOptions = [new SortOption("name", "Name"), new SortOption("updated", "Geändert")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "name",
            DefaultDescending = false,
            Total = total,
            Matching = matching,
            Noun = "Profile",
            NounOne = "Profil",
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = matching, Url = Toolbar.UrlWithPage };
    }
}
