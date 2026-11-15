using Microsoft.AspNetCore.Mvc;
using ThixInstall.Cloud.Business;

namespace ThixInstall.Cloud.Pages.Account;

/// <summary>The tab "Sicherheit": a new password. After a password set by an administrator the person comes here first (force=1).</summary>
public sealed class SecurityModel : AppPageModel
{
    [BindProperty]
    public string Current { get; set; } = string.Empty;

    [BindProperty]
    public string New { get; set; } = string.Empty;

    [BindProperty]
    public string Repeat { get; set; } = string.Empty;

    public bool MustChange => Me.Snapshot?.MustChangePassword == true;

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await AccountService.ChangePasswordAsync(Db, Actor, Current, New, Repeat, Me.SessionId);
        if (!outcome.Ok)
        {
            AddError(outcome.Field, outcome.Error);
            return Page();
        }

        Flash("Das Passwort wurde geändert. Andere Browser sind abgemeldet; die Geräte bleiben angemeldet.");
        return RedirectToPage();
    }
}
