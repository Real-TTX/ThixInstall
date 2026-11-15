using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Data;
using ThixInstall.Cloud.Web;

namespace ThixInstall.Cloud.Pages;

/// <summary>
/// What every page behind the sign-in needs: the person (as the business layer knows them), the database context to hand to the business layer
/// (a page never queries it itself), the messages after a redirect.
/// </summary>
public abstract class AppPageModel : PageModel
{
    protected AppDbContext Db => HttpContext.RequestServices.GetRequiredService<AppDbContext>();

    protected CurrentUser Me => HttpContext.RequestServices.GetRequiredService<CurrentUser>();

    protected Actor Actor => Me.Actor;

    protected void Flash(string message) => TempData["Flash"] = message;

    protected void FlashError(string message) => TempData["FlashError"] = message;

    /// <summary>Back to where the person came from, only to an address of this app.</summary>
    protected IActionResult Back(string? returnUrl, string fallback)
        => !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : LocalRedirect(fallback);

    /// <summary>Writes the error of an outcome at its field (or at the top of the form).</summary>
    protected void AddError(string? field, string? message) => ModelState.AddModelError(field ?? string.Empty, message ?? "Das hat nicht geklappt.");
}
