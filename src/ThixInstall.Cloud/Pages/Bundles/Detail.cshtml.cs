using Microsoft.AspNetCore.Mvc;
using ThixInstall.Cloud.Business;

namespace ThixInstall.Cloud.Pages.Bundles;

/// <summary>A bundle in the signed-in area: its content, its link and download page, how often it was downloaded; it can be deleted.</summary>
public sealed class DetailModel : AppPageModel
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    public BundleView Bundle { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync()
    {
        var outcome = await BundleService.GetAsync(Db, Actor, Id);
        if (!outcome.Ok)
        {
            return NotFound();
        }

        Bundle = outcome.Value!;
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var outcome = await BundleService.DeleteAsync(Db, Actor, Id);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        Flash("Paket gelöscht. Sein Link funktioniert nicht mehr.");
        return Redirect("/Bundles");
    }
}
