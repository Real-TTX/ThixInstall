using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Web;

namespace ThixInstall.Cloud.Pages.Account;

/// <summary>The sign-in of the browser (the same account as in the desktop app).</summary>
[EnableRateLimiting("signin")]
public sealed class LoginModel : AppPageModel
{
    [BindProperty]
    public string UserName { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public bool Remember { get; set; } = true;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true, Name = "out")]
    public bool SignedOut { get; set; }

    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true ? Back(ReturnUrl, "/Devices") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await UserService.SignInAsync(Db, UserName, Password, HttpContext.RequestAborted);
        if (!outcome.Ok)
        {
            AddError(nameof(Password), outcome.Error);
            return Page();
        }

        await CookieSignIn.SignInAsync(HttpContext, Db, outcome.Value!, Remember);
        return Back(ReturnUrl, "/Devices");
    }
}
