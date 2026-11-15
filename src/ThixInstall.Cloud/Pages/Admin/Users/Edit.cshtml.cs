using Microsoft.AspNetCore.Mvc;
using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Controls;

namespace ThixInstall.Cloud.Pages.Admin.Users;

/// <summary>An account: create, change, delete (administrators only; the business layer checks it again).</summary>
public sealed class EditModel : AppPageModel
{
    public static readonly IReadOnlyList<Choice> Roles = [new("user", "Benutzer"), new("admin", "Administrator", "Pflegt Katalog, Profile, Benutzer und Einstellungen und sieht alle Geräte")];

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public string UserName { get; set; } = string.Empty;

    [BindProperty]
    public string DisplayName { get; set; } = string.Empty;

    [BindProperty]
    public string Role { get; set; } = "user";

    [BindProperty]
    public bool IsActive { get; set; } = true;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    public bool IsNew => Id is null;

    public bool IsSelf => Id == Me.Snapshot?.Id;

    public async Task<IActionResult> OnGetAsync()
    {
        if (IsNew)
        {
            return Page();
        }

        var outcome = await UserService.GetAsync(Db, Actor, Id!.Value);
        if (!outcome.Ok)
        {
            return NotFound();
        }

        var user = outcome.Value!;
        UserName = user.UserName;
        DisplayName = user.DisplayName;
        Role = user.IsAdmin ? "admin" : "user";
        IsActive = user.IsActive;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await UserService.SaveAsync(Db, Actor, new UserInput(Id, UserName, DisplayName, Role == "admin", IsActive, Password));
        if (outcome.NotFound)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            AddError(outcome.Field, outcome.Error);
            return Page();
        }

        Flash(IsNew ? "Benutzer angelegt. Das Passwort muss bei der ersten Anmeldung geändert werden." : "Gespeichert.");
        return Redirect("/Admin/Users");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (IsNew)
        {
            return NotFound();
        }

        var outcome = await UserService.DeleteAsync(Db, Actor, Id!.Value);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            FlashError(outcome.Error!);
            return RedirectToPage(new { id = Id });
        }

        Flash("Benutzer gelöscht.");
        return Redirect("/Admin/Users");
    }
}
