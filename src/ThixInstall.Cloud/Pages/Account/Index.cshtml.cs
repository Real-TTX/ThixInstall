using Microsoft.AspNetCore.Mvc;
using ThixInstall.Cloud.Business;

namespace ThixInstall.Cloud.Pages.Account;

/// <summary>The tab "Allgemein" of the account: the name and the e-mail address. Also saves the theme of the menu.</summary>
public sealed class IndexModel : AppPageModel
{
    [BindProperty]
    public string DisplayName { get; set; } = string.Empty;

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    public AccountView Account { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync()
    {
        var outcome = await AccountService.GetAsync(Db, Actor);
        if (!outcome.Ok)
        {
            return NotFound();
        }

        Account = outcome.Value!;
        DisplayName = Account.DisplayName;
        Email = Account.Email ?? string.Empty;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await AccountService.SaveGeneralAsync(Db, Actor, DisplayName, Email);
        if (!outcome.Ok)
        {
            AddError(outcome.Field, outcome.Error);
            Account = (await AccountService.GetAsync(Db, Actor)).Value!;
            return Page();
        }

        Flash("Gespeichert.");
        return RedirectToPage();
    }

    /// <summary>The theme button of the menu posts here (theme=system|light|dark).</summary>
    public async Task<IActionResult> OnPostThemeAsync(string theme)
    {
        await AccountService.SetThemeAsync(Db, Actor, theme);
        return new NoContentResult();
    }
}
