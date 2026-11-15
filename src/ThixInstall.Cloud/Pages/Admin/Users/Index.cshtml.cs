using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Web;

namespace ThixInstall.Cloud.Pages.Admin.Users;

/// <summary>The accounts (administrators only; the business layer checks it again).</summary>
public sealed class IndexModel : AppPageModel
{
    public const int PageSize = 50;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<UserListItem> Items { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var role = ListQuery.Choice(Request, "role", "admin", "user");
        var status = ListQuery.Choice(Request, "status", "active", "inactive");
        var sort = ListQuery.Choice(Request, "sort", "name", "login", "created") ?? "name";
        var descending = ListQuery.Descending(Request, byDefault: sort != "name");
        var page = ListQuery.Page(Request);

        var (items, total, matching) = await UserService.ListAsync(Db, Actor, new UserQuery(search, role, status, sort, descending, page, PageSize));
        Items = items;
        Toolbar = new ToolbarModel
        {
            Id = "users",
            Action = "/Admin/Users",
            Label = "Benutzer",
            Search = search,
            SearchPlaceholder = "Benutzer durchsuchen",
            Filters =
            [
                new FilterField("role", "Rolle", [new("admin", "Administrator"), new("user", "Benutzer")], role),
                new FilterField("status", "Status", [new("active", "Aktiv"), new("inactive", "Deaktiviert")], status),
            ],
            SortOptions = [new SortOption("name", "Benutzername"), new SortOption("login", "Letzte Anmeldung"), new SortOption("created", "Angelegt")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "name",
            DefaultDescending = false,
            Total = total,
            Matching = matching,
            Noun = "Benutzer",
            NounOne = "Benutzer",
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = matching, Url = Toolbar.UrlWithPage };
    }
}
