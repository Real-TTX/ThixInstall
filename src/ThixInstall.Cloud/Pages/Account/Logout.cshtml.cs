using Microsoft.AspNetCore.Mvc;
using ThixInstall.Cloud.Web;

namespace ThixInstall.Cloud.Pages.Account;

/// <summary>Signs the browser out (only by a form, so another site cannot do it with a link).</summary>
public sealed class LogoutModel : AppPageModel
{
    public IActionResult OnGet() => Redirect("/Devices");

    public async Task<IActionResult> OnPostAsync()
    {
        await CookieSignIn.SignOutAsync(HttpContext, Db);
        return Redirect("/Account/Login?out=true");
    }
}
