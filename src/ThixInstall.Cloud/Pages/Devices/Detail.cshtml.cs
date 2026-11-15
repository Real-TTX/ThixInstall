using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Controls;
using ThixInstall.Cloud.Web;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Pages.Devices;

/// <summary>One device with its areas as tabs: Übersicht, Software (the inventory with the updates), Aufträge, Einstellungen (profile, remove).</summary>
public sealed class DetailModel : AppPageModel
{
    public const int ActionsPageSize = 25;

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Tab { get; set; } = "overview";

    [BindProperty]
    public string ProfileId { get; set; } = string.Empty;

    public DeviceView Device { get; private set; } = null!;

    public IReadOnlyList<DeviceAppView> Apps { get; private set; } = [];

    public IReadOnlyList<ActionListItem> Actions { get; private set; } = [];

    public IReadOnlyList<Choice> Profiles { get; private set; } = [];

    public ToolbarModel? Toolbar { get; private set; }

    public PagerModel? Pager { get; private set; }

    public int AppsTotal { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        Tab = Tab is "software" or "actions" or "settings" ? Tab : "overview";
        return await LoadAsync() ? Page() : NotFound();
    }

    /// <summary>"Einstellungen": the profile of the device.</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        long? profile = long.TryParse(ProfileId, CultureInfo.InvariantCulture, out var id) ? id : null;
        var outcome = await DeviceService.AssignProfileAsync(Db, Actor, Id, profile);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            AddError(outcome.Field, outcome.Error);
            Tab = "settings";
            await LoadAsync();
            return Page();
        }

        Flash(profile is null ? "Profil entfernt." : "Profil zugewiesen. Seine Apps und Skripte gehen als Aufträge an das Gerät (mit Sync).");
        return Redirect($"/Devices/Detail?id={Id}&tab=settings");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var outcome = await DeviceService.RemoveAsync(Db, Actor, Id);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        Flash("Gerät entfernt. ThixInstall auf dem Rechner ist abgemeldet.");
        return Redirect("/Devices");
    }

    /// <summary>"Alles aktualisieren" from the tab Software.</summary>
    public async Task<IActionResult> OnPostUpdateAllAsync()
    {
        var outcome = await ActionService.PushAsync(Db, Actor, Id, new PushRequest(ActionKind.UpdateAll, null, null));
        if (outcome.NotFound)
        {
            return NotFound();
        }

        if (outcome.Ok)
        {
            Flash("Auftrag gesendet: alle Apps aktualisieren.");
        }
        else
        {
            FlashError(outcome.Error!);
        }

        return Redirect($"/Devices/Detail?id={Id}&tab=software");
    }

    /// <summary>The button "Aktualisieren" of one app of the inventory.</summary>
    public async Task<IActionResult> OnPostUpdateAsync(long packageId)
    {
        var outcome = await ActionService.PushAsync(Db, Actor, Id, new PushRequest(ActionKind.Update, packageId, null));
        if (outcome.NotFound)
        {
            return NotFound();
        }

        if (outcome.Ok)
        {
            Flash("Auftrag gesendet.");
        }
        else
        {
            FlashError(outcome.Error!);
        }

        return Redirect($"/Devices/Detail?id={Id}&tab=software");
    }

    private async Task<bool> LoadAsync()
    {
        var device = await DeviceService.GetAsync(Db, Actor, Id);
        if (!device.Ok)
        {
            return false;
        }

        Device = device.Value!;
        ProfileId = Device.ProfileId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        switch (Tab)
        {
            case "software":
                var search = ListQuery.Text(Request, "q");
                var updates = ListQuery.Choice(Request, "show", "updates") == "updates";
                var apps = await DeviceService.AppsAsync(Db, Actor, Id, search, updates);
                Apps = apps.Value ?? [];
                AppsTotal = Device.Apps;
                Toolbar = new ToolbarModel
                {
                    Id = "apps",
                    Action = "/Devices/Detail",
                    Label = "Software",
                    Search = search,
                    SearchPlaceholder = "Software durchsuchen",
                    Filters = [new FilterField("show", "Anzeigen", [new("updates", "Nur mit Update")], updates ? "updates" : null)],
                    Total = AppsTotal,
                    Matching = Apps.Count,
                    Noun = "Apps",
                    NounOne = "App",
                    Keep = new Dictionary<string, string?> { ["id"] = Id.ToString(CultureInfo.InvariantCulture), ["tab"] = "software" },
                };
                break;
            case "actions":
                var page = ListQuery.Page(Request);
                var (items, total, matching) = await ActionService.ListAsync(Db, Actor, new ActionQuery(null, null, null, Id, "created", true, page, ActionsPageSize));
                Actions = items;
                Pager = new PagerModel { Page = page, PageSize = ActionsPageSize, Total = matching, Url = p => $"/Devices/Detail?id={Id}&tab=actions" + (p > 1 ? $"&page={p}" : string.Empty) };
                break;
            case "settings":
                Profiles = [new Choice(string.Empty, "Kein Profil"), .. (await ProfileService.ChoicesAsync(Db)).Select(p => new Choice(p.Id.ToString(CultureInfo.InvariantCulture), p.Name))];
                break;
        }

        return true;
    }
}
