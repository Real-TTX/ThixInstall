using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Controls;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Pages.Profiles;

/// <summary>
/// A profile with its areas as tabs: Allgemein (name, description), Inhalte (its apps and scripts), Geräte (who has it, an action for all of them),
/// Registrierung (the enrollment key, the silent installation, the download that enrolls with it).
/// </summary>
public sealed class EditModel : AppPageModel
{
    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Tab { get; set; } = "general";

    [BindProperty] public string Name { get; set; } = string.Empty;
    [BindProperty] public string Description { get; set; } = string.Empty;
    [BindProperty] public string Item { get; set; } = string.Empty;

    public bool IsNew => Id is null;

    public ProfileView? Profile { get; private set; }

    public IReadOnlyList<ProfileItemView> Items { get; private set; } = [];

    public IReadOnlyList<Choice> ItemChoices { get; private set; } = [];

    public IReadOnlyList<DeviceListItem> Devices { get; private set; } = [];

    public string CloudUrl => $"{Request.Scheme}://{Request.Host}{Request.PathBase}";

    public async Task<IActionResult> OnGetAsync()
    {
        Tab = IsNew ? "general" : Tab is "items" or "devices" or "enroll" ? Tab : "general";
        return IsNew || await LoadAsync(fill: true) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await ProfileService.SaveAsync(Db, Actor, Id, Name, Description);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            AddError(outcome.Field, outcome.Error);
            if (!IsNew)
            {
                await LoadAsync(fill: false);
            }

            return Page();
        }

        Flash(IsNew ? "Profil angelegt. Jetzt Apps und Skripte hinzufügen." : "Gespeichert.");
        return Redirect($"/Profiles/Edit?id={outcome.Value}{(IsNew ? "&tab=items" : string.Empty)}");
    }

    public async Task<IActionResult> OnPostAddAsync()
    {
        long? package = Item.StartsWith("p", StringComparison.Ordinal) && long.TryParse(Item[1..], CultureInfo.InvariantCulture, out var p) ? p : null;
        long? script = Item.StartsWith("s", StringComparison.Ordinal) && long.TryParse(Item[1..], CultureInfo.InvariantCulture, out var s) ? s : null;
        var outcome = await ProfileService.AddItemAsync(Db, Actor, Id ?? 0, package, script);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            Tab = "items";
            AddError(outcome.Field, outcome.Error);
            await LoadAsync(fill: true);
            return Page();
        }

        Flash(outcome.Value == 0 ? "Hinzugefügt." : $"Hinzugefügt – als Auftrag an {outcome.Value} Gerät(e) gesendet.");
        return Redirect($"/Profiles/Edit?id={Id}&tab=items");
    }

    public async Task<IActionResult> OnPostRemoveAsync(long itemId)
    {
        await ProfileService.RemoveItemAsync(Db, Actor, Id ?? 0, itemId);
        Flash("Aus dem Profil entfernt. Installierte Apps bleiben auf den Geräten.");
        return Redirect($"/Profiles/Edit?id={Id}&tab=items");
    }

    public async Task<IActionResult> OnPostMoveAsync(long itemId, int direction)
    {
        await ProfileService.MoveItemAsync(Db, Actor, Id ?? 0, itemId, direction);
        return Redirect($"/Profiles/Edit?id={Id}&tab=items");
    }

    public async Task<IActionResult> OnPostRegenerateAsync()
    {
        var outcome = await ProfileService.RegenerateKeyAsync(Db, Actor, Id ?? 0);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        Flash("Neuer Registrierungsschlüssel erstellt. Der alte meldet keine Geräte mehr an.");
        return Redirect($"/Profiles/Edit?id={Id}&tab=enroll");
    }

    public async Task<IActionResult> OnPostDownloadAsync(string os)
    {
        var target = ApiContract.ParseOs(os) ?? TargetOs.Windows;
        var outcome = await ProfileService.EnrollmentBundleAsync(Db, Actor, Id ?? 0, target);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            FlashError(outcome.Error!);
            return Redirect($"/Profiles/Edit?id={Id}&tab=enroll");
        }

        return Redirect(ApiContract.BundlePage(outcome.Value!));
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var outcome = await ProfileService.DeleteAsync(Db, Actor, Id ?? 0);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        Flash("Profil gelöscht. Seine Geräte behalten ihre Apps.");
        return Redirect("/Profiles");
    }

    private async Task<bool> LoadAsync(bool fill)
    {
        var outcome = await ProfileService.GetAsync(Db, Actor, Id!.Value);
        if (!outcome.Ok)
        {
            return false;
        }

        Profile = outcome.Value!;
        if (fill)
        {
            Name = Profile.Name;
            Description = Profile.Description ?? string.Empty;
        }

        switch (Tab)
        {
            case "items":
                Items = (await ProfileService.ItemsAsync(Db, Actor, Id!.Value)).Value ?? [];
                var (winPackages, winScripts) = await CatalogService.PickerAsync(Db, Actor, TargetOs.Windows);
                var (macPackages, macScripts) = await CatalogService.PickerAsync(Db, Actor, TargetOs.MacOS);
                var packages = winPackages.Concat(macPackages).DistinctBy(p => p.Id).Where(p => Items.All(i => i.PackageId != p.Id)).OrderBy(p => p.Name, StringComparer.CurrentCulture);
                var scripts = winScripts.Concat(macScripts).Where(s => Items.All(i => i.ScriptId != s.Id)).OrderBy(s => s.Name, StringComparer.CurrentCulture);
                ItemChoices =
                [
                    new Choice(string.Empty, "App oder Skript wählen"),
                    .. packages.Select(p => new Choice("p" + p.Id.ToString(CultureInfo.InvariantCulture), p.Name, "App · " + p.Category)),
                    .. scripts.Select(s => new Choice("s" + s.Id.ToString(CultureInfo.InvariantCulture), s.Name, "Skript · " + s.Category)),
                ];
                break;
            case "devices":
                Devices = (await DeviceService.ListAsync(Db, Actor, new DeviceQuery(null, null, null, Id, null, "name", false, 1, 500))).Items;
                break;
        }

        return true;
    }
}
