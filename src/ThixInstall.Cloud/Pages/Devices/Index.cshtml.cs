using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Web;

namespace ThixInstall.Cloud.Pages.Devices;

/// <summary>The devices: a person's own, every device for an administrator (with "Meine" to narrow it).</summary>
public sealed class IndexModel : AppPageModel
{
    public const int PageSize = 50;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<DeviceListItem> Items { get; private set; } = [];

    public bool ShowOwner => Me.IsAdmin;

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var os = ListQuery.Choice(Request, "os", "windows", "macos");
        var sync = ListQuery.Choice(Request, "sync", "on", "off");
        var scope = Me.IsAdmin ? ListQuery.Choice(Request, "scope", "mine") : null;
        var profiles = await ProfileService.ChoicesAsync(Db);
        var profileText = ListQuery.Text(Request, "profile");
        long? profileId = long.TryParse(profileText, out var pid) && profiles.Any(p => p.Id == pid) ? pid : null;
        var sort = ListQuery.Choice(Request, "sort", "seen", "name", "created") ?? "seen";
        var descending = ListQuery.Descending(Request, byDefault: sort != "name");
        var page = ListQuery.Page(Request);

        var (items, total, matching) = await DeviceService.ListAsync(Db, Actor, new DeviceQuery(search, os, sync, profileId, scope, sort, descending, page, PageSize));
        Items = items;
        var filters = new List<FilterField>
        {
            new("os", "System", [new("windows", "Windows"), new("macos", "macOS")], os),
            new("sync", "Sync", [new("on", "An"), new("off", "Aus")], sync),
        };
        if (profiles.Count > 0)
        {
            filters.Add(new FilterField("profile", "Profil", profiles.Select(p => new FilterOption(p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), p.Name)).ToList(), profileId?.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (Me.IsAdmin)
        {
            filters.Add(new FilterField("scope", "Besitzer", [new("mine", "Nur meine")], scope));
        }

        Toolbar = new ToolbarModel
        {
            Id = "devices",
            Action = "/Devices",
            Label = "Geräte",
            Search = search,
            SearchPlaceholder = "Geräte durchsuchen",
            Filters = filters,
            SortOptions = [new SortOption("seen", "Zuletzt gesehen"), new SortOption("name", "Name"), new SortOption("created", "Angemeldet seit")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "seen",
            DefaultDescending = true,
            Total = total,
            Matching = matching,
            Noun = "Geräte",
            NounOne = "Gerät",
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = matching, Url = Toolbar.UrlWithPage };
    }
}
