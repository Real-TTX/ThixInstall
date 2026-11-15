using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Controls;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Pages.Devices;

/// <summary>
/// "Auftrag senden": install, update or uninstall an app, run a script, update everything – for one device (<c>?id=</c>) or, for administrators, for
/// every device of a profile (<c>?profile=</c>). Only the apps and scripts of the device's system are offered.
/// </summary>
public sealed class PushModel : AppPageModel
{
    public static readonly IReadOnlyList<Choice> Kinds =
    [
        new("Install", "App installieren"),
        new("Update", "App aktualisieren"),
        new("Uninstall", "App deinstallieren"),
        new("RunScript", "Skript ausführen"),
        new("UpdateAll", "Alle Apps aktualisieren", "Alles, wofür der Katalog eine neuere Version kennt"),
        new("Refresh", "Inventar neu melden"),
    ];

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty(SupportsGet = true, Name = "profile")]
    public long? ProfileId { get; set; }

    [BindProperty]
    public string Kind { get; set; } = "Install";

    [BindProperty(SupportsGet = true)]
    public string PackageId { get; set; } = string.Empty;

    [BindProperty]
    public string ScriptId { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true, Name = "os")]
    public string Os { get; set; } = "windows";

    public string TargetName { get; private set; } = string.Empty;

    public TargetOs TargetOs { get; private set; }

    public bool ForProfile => ProfileId is not null;

    public IReadOnlyList<Choice> Packages { get; private set; } = [];

    public IReadOnlyList<Choice> Scripts { get; private set; } = [];

    public string BackUrl => ForProfile ? $"/Profiles/Edit?id={ProfileId}&tab=devices" : $"/Devices/Detail?id={Id}";

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : NotFound();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (!Enum.TryParse<ActionKind>(Kind, out var kind))
        {
            AddError(nameof(Kind), "Bitte eine Art wählen.");
            return Page();
        }

        var request = new PushRequest(kind,
            long.TryParse(PackageId, CultureInfo.InvariantCulture, out var package) ? package : null,
            long.TryParse(ScriptId, CultureInfo.InvariantCulture, out var script) ? script : null);
        if (ForProfile)
        {
            var sent = await ActionService.PushToProfileAsync(Db, Actor, ProfileId!.Value, request);
            if (!sent.Ok)
            {
                AddError(null, sent.Error);
                return Page();
            }

            Flash(sent.Value == 1 ? "Auftrag an 1 Gerät gesendet." : $"Auftrag an {sent.Value} Geräte gesendet.");
            return Redirect(BackUrl);
        }

        var outcome = await ActionService.PushAsync(Db, Actor, Id!.Value, request);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            AddError(outcome.Field, outcome.Error);
            return Page();
        }

        Flash("Auftrag gesendet. Das Gerät führt ihn aus, sobald es online ist.");
        return Redirect($"/Actions/Detail?id={outcome.Value}");
    }

    private async Task<bool> LoadAsync()
    {
        if (ForProfile)
        {
            if (!Me.IsAdmin)
            {
                return false;
            }

            var profile = await ProfileService.GetAsync(Db, Actor, ProfileId!.Value);
            if (!profile.Ok)
            {
                return false;
            }

            TargetName = "Profil " + profile.Value!.Name;
            TargetOs = ApiContract.ParseOs(Os) ?? TargetOs.Windows;
        }
        else
        {
            var device = Id is { } id ? await DeviceService.GetAsync(Db, Actor, id) : null;
            if (device is null || !device.Ok)
            {
                return false;
            }

            TargetName = device.Value!.Name;
            TargetOs = device.Value.Os;
        }

        var (packages, scripts) = await CatalogService.PickerAsync(Db, Actor, TargetOs);
        Packages = [new Choice(string.Empty, "App wählen"), .. packages.Select(p => new Choice(p.Id.ToString(CultureInfo.InvariantCulture), p.Name, p.Category))];
        Scripts = [new Choice(string.Empty, "Skript wählen"), .. scripts.Select(s => new Choice(s.Id.ToString(CultureInfo.InvariantCulture), s.Name, s.Note))];
        return true;
    }
}
