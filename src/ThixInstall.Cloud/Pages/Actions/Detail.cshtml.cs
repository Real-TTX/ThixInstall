using Microsoft.AspNetCore.Mvc;
using ThixInstall.Cloud.Business;

namespace ThixInstall.Cloud.Pages.Actions;

/// <summary>One action: what, for which device, its state and the output the device sent. A waiting action can be cancelled.</summary>
public sealed class DetailModel : AppPageModel
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    public ActionView Action { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync()
    {
        var outcome = await ActionService.GetAsync(Db, Actor, Id);
        if (!outcome.Ok)
        {
            return NotFound();
        }

        Action = outcome.Value!;
        return Page();
    }

    public async Task<IActionResult> OnPostCancelAsync()
    {
        var outcome = await ActionService.CancelAsync(Db, Actor, Id);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        if (outcome.Ok)
        {
            Flash("Auftrag abgebrochen.");
        }
        else
        {
            FlashError(outcome.Error!);
        }

        return RedirectToPage(new { id = Id });
    }
}
