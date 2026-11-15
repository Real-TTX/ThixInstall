using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Web;

namespace ThixInstall.Cloud.Pages.Account;

/// <summary>The setup assistant: on a fresh installation it creates the first administrator and signs them in. Afterwards it is gone.</summary>
[EnableRateLimiting("signin")]
public sealed class SetupModel(SetupState setup) : AppPageModel
{
    [BindProperty]
    public string UserName { get; set; } = string.Empty;

    [BindProperty]
    public string DisplayName { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public string Repeat { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync() => await setup.NeedsSetupAsync(Db) ? Page() : Redirect("/Devices");

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await UserService.SetupAsync(Db, UserName, DisplayName, Password, Repeat);
        if (!outcome.Ok)
        {
            if (!await setup.NeedsSetupAsync(Db))
            {
                return Redirect("/Devices");
            }

            AddError(outcome.Field, outcome.Error);
            return Page();
        }

        setup.Done();
        await CookieSignIn.SignInAsync(HttpContext, Db, outcome.Value!, remember: true);
        Flash("ThixInstall.Cloud ist eingerichtet. Der Katalog ist mit beliebten Apps gefüllt – stell dir dein erstes Paket zusammen.");
        return Redirect("/");
    }
}
