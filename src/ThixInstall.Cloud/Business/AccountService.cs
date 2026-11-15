using Microsoft.EntityFrameworkCore;
using ThixInstall.Cloud.Data;

namespace ThixInstall.Cloud.Business;

/// <summary>The account as its owner sees it.</summary>
public sealed record AccountView(string UserName, string DisplayName, string? Email, string Theme, bool IsAdmin, DateTime CreateDate);

/// <summary>What a person changes about their own account: the name, the e-mail address, the theme, the password.</summary>
public static class AccountService
{
    public static async Task<Outcome<AccountView>> GetAsync(AppDbContext db, Actor actor)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == actor.UserId);
        return user is null
            ? Outcome<AccountView>.Missing()
            : Outcome<AccountView>.Success(new AccountView(user.UserName, user.DisplayName, user.Email, user.Theme, user.IsAdmin, user.CreateDate));
    }

    /// <summary>The tab "Allgemein": the name and the e-mail address.</summary>
    public static async Task<Outcome> SaveGeneralAsync(AppDbContext db, Actor actor, string displayName, string? email)
    {
        var name = (displayName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return Outcome.Fail("Bitte einen Namen eingeben.", "DisplayName");
        }

        if (name.Length > 120)
        {
            return Outcome.Fail("Der Name hat höchstens 120 Zeichen.", "DisplayName");
        }

        var mail = Text.Clean(email, 200);
        if (mail is not null && (!mail.Contains('@') || mail.Contains(' ')))
        {
            return Outcome.Fail("Das ist keine gültige E-Mail-Adresse.", "Email");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == actor.UserId);
        if (user is null)
        {
            return Outcome.Missing();
        }

        user.DisplayName = name;
        user.Email = mail;
        await db.SaveChangesAsync();
        SessionService.Invalidate();
        return Outcome.Success();
    }

    public static async Task SetThemeAsync(AppDbContext db, Actor actor, string theme)
    {
        var value = theme is "light" or "dark" ? theme : "system";
        await db.Users.Where(u => u.Id == actor.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Theme, value));
        SessionService.Invalidate();
    }

    /// <summary>A new password: the current one is checked; the other sessions of the browser end, the devices stay enrolled.</summary>
    public static async Task<Outcome> ChangePasswordAsync(AppDbContext db, Actor actor, string current, string password, string repeat, long? currentSessionId)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == actor.UserId);
        if (user is null)
        {
            return Outcome.Missing();
        }

        if (!Passwords.Verify(user, current))
        {
            return Outcome.Fail("Das aktuelle Passwort stimmt nicht.", "Current");
        }

        if (Passwords.Problem(password, repeat) is { } problem)
        {
            return Outcome.Fail(problem, problem.Contains("überein", StringComparison.Ordinal) ? "Repeat" : "New");
        }

        if (password == current)
        {
            return Outcome.Fail("Das neue Passwort muss sich vom aktuellen unterscheiden.", "New");
        }

        user.PasswordHash = Passwords.Hash(user, password);
        user.MustChangePassword = false;
        await db.SaveChangesAsync();
        await SessionService.RevokeAllAsync(db, user.Id, currentSessionId);
        return Outcome.Success();
    }
}
