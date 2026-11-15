using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Web;

namespace ThixInstall.Cloud.Pages.Actions;

/// <summary>"Aufträge": the actions of every device a person may see, newest first; filters by state and origin.</summary>
public sealed class IndexModel : AppPageModel
{
    public const int PageSize = 50;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<ActionListItem> Items { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var state = ListQuery.Choice(Request, "state", "open", "succeeded", "failed", "cancelled");
        var origin = ListQuery.Choice(Request, "origin", "push", "profile", "device");
        var sort = ListQuery.Choice(Request, "sort", "created", "device") ?? "created";
        var descending = ListQuery.Descending(Request, byDefault: sort == "created");
        var page = ListQuery.Page(Request);

        var (items, total, matching) = await ActionService.ListAsync(Db, Actor, new ActionQuery(search, state, origin, null, sort, descending, page, PageSize));
        Items = items;
        Toolbar = new ToolbarModel
        {
            Id = "actions",
            Action = "/Actions",
            Label = "Aufträge",
            Search = search,
            SearchPlaceholder = "Aufträge und Geräte durchsuchen",
            Filters =
            [
                new FilterField("state", "Status", [new("open", "Offen"), new("succeeded", "Erfolgreich"), new("failed", "Fehlgeschlagen"), new("cancelled", "Abgebrochen")], state),
                new FilterField("origin", "Herkunft", [new("push", "Aus der Cloud"), new("profile", "Profil"), new("device", "Am Gerät")], origin),
            ],
            SortOptions = [new SortOption("created", "Angelegt"), new SortOption("device", "Gerät")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "created",
            DefaultDescending = true,
            Total = total,
            Matching = matching,
            Noun = "Aufträge",
            NounOne = "Auftrag",
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = matching, Url = Toolbar.UrlWithPage };
    }
}
