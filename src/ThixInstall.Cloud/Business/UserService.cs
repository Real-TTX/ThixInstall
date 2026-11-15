using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ThixInstall.Cloud.Data;

namespace ThixInstall.Cloud.Business;

/// <summary>One account in the list of the administration.</summary>
public sealed record UserListItem(long Id, string UserName, string DisplayName, bool IsAdmin, bool IsActive, bool IsLocked, DateTime? LastLoginDate, int Devices, int Bundles);

/// <summary>What the edit page of an account shows and posts.</summary>
public sealed record UserInput(long? Id, string UserName, string DisplayName, bool IsAdmin, bool IsActive, string Password);

/// <summary>The query of the list of accounts.</summary>
public sealed record UserQuery(string? Search, string? Role, string? Status, string Sort, bool Descending, int Page, int PageSize);

/// <summary>
/// The accounts: the setup assistant (the first administrator), the sign-in (browser and desktop) and what an administrator does with
/// accounts. Everything but the sign-in and the setup needs an administrator (<see cref="RequireAdmin"/>).
/// </summary>
public static partial class UserService
{
    public const int MaxFailedLogins = 10;
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5);

    [GeneratedRegex("^[a-z0-9][a-z0-9._@-]{1,63}$")]
    private static partial Regex UserNameRegex();

    public static string NormalizeUserName(string? userName) => (userName ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>True while there is no account at all: then every page leads to the setup assistant.</summary>
    public static async Task<bool> NeedsSetupAsync(AppDbContext db, CancellationToken ct = default) => !await db.Users.IgnoreQueryFilters().AnyAsync(ct);

    /// <summary>The setup assistant: creates the first administrator, only while there is no account at all.</summary>
    public static async Task<Outcome<User>> SetupAsync(AppDbContext db, string userName, string displayName, string password, string repeat)
    {
        if (!await NeedsSetupAsync(db))
        {
            return Outcome<User>.Fail("ThixInstall ist schon eingerichtet.");
        }

        var name = NormalizeUserName(userName);
        if (UserNameProblem(name) is { } nameProblem)
        {
            return Outcome<User>.Fail(nameProblem, "UserName");
        }

        if (Passwords.Problem(password, repeat) is { } passwordProblem)
        {
            return Outcome<User>.Fail(passwordProblem, "Password");
        }

        var user = new User { UserName = name, DisplayName = CleanDisplayName(displayName, name), IsAdmin = true, IsActive = true, LastLoginDate = DateTime.UtcNow };
        user.PasswordHash = Passwords.Hash(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return Outcome<User>.Success(user);
    }

    /// <summary>
    /// Checks a user name and a password (browser and desktop). Ten wrong passwords in a row lock the account for five minutes. An unknown name takes as long
    /// as a wrong password and gets the same answer.
    /// </summary>
    public static async Task<Outcome<User>> SignInAsync(AppDbContext db, string userName, string password, CancellationToken ct = default)
    {
        const string wrong = "Benutzername oder Passwort ist falsch.";
        var name = NormalizeUserName(userName);
        if (name.Length == 0 || string.IsNullOrEmpty(password))
        {
            return Outcome<User>.Fail("Bitte Benutzername und Passwort eingeben.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == name, ct);
        if (user is null)
        {
            Passwords.Verify(new User { PasswordHash = Passwords.DummyHash }, password);
            return Outcome<User>.Fail(wrong);
        }

        var now = DateTime.UtcNow;
        if (user.LockoutUntil > now)
        {
            return Outcome<User>.Fail("Zu viele Fehlversuche. Bitte in ein paar Minuten noch einmal versuchen.");
        }

        if (!Passwords.Verify(user, password))
        {
            user.FailedLogins++;
            if (user.FailedLogins >= MaxFailedLogins)
            {
                user.FailedLogins = 0;
                user.LockoutUntil = now + Lockout;
            }

            await db.SaveChangesAsync(ct);
            return Outcome<User>.Fail(wrong);
        }

        if (!user.IsActive)
        {
            return Outcome<User>.Fail("Dieses Konto ist deaktiviert. Bitte an die Administration wenden.");
        }

        user.FailedLogins = 0;
        user.LockoutUntil = null;
        user.LastLoginDate = now;
        await db.SaveChangesAsync(ct);
        return Outcome<User>.Success(user);
    }

    public static void RequireAdmin(Actor actor)
    {
        if (!actor.IsAdmin)
        {
            throw new NotAllowedException("Nur für die Administration.");
        }
    }

    public static async Task<(IReadOnlyList<UserListItem> Items, int Total, int Matching)> ListAsync(AppDbContext db, Actor actor, UserQuery query)
    {
        RequireAdmin(actor);
        var now = DateTime.UtcNow;
        var all = db.Users.AsNoTracking();
        var total = await all.CountAsync();

        var filtered = all;
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = "%" + EscapeLike(query.Search.Trim()) + "%";
            filtered = filtered.Where(u => EF.Functions.ILike(u.UserName, pattern, "\\") || EF.Functions.ILike(u.DisplayName, pattern, "\\"));
        }

        filtered = query.Role switch
        {
            "admin" => filtered.Where(u => u.IsAdmin),
            "user" => filtered.Where(u => !u.IsAdmin),
            _ => filtered,
        };
        filtered = query.Status switch
        {
            "active" => filtered.Where(u => u.IsActive),
            "inactive" => filtered.Where(u => !u.IsActive),
            _ => filtered,
        };

        var matching = await filtered.CountAsync();
        filtered = (query.Sort, query.Descending) switch
        {
            ("login", false) => filtered.OrderBy(u => u.LastLoginDate).ThenBy(u => u.UserName),
            ("login", true) => filtered.OrderByDescending(u => u.LastLoginDate).ThenBy(u => u.UserName),
            ("created", false) => filtered.OrderBy(u => u.CreateDate),
            ("created", true) => filtered.OrderByDescending(u => u.CreateDate),
            (_, true) => filtered.OrderByDescending(u => u.UserName),
            _ => filtered.OrderBy(u => u.UserName),
        };

        var items = await filtered
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(u => new UserListItem(
                u.Id, u.UserName, u.DisplayName, u.IsAdmin, u.IsActive, u.LockoutUntil > now, u.LastLoginDate,
                db.Devices.Count(d => d.UserId == u.Id),
                db.Bundles.Count(b => b.OwnerUserId == u.Id)))
            .ToListAsync();
        return (items, total, matching);
    }

    public static async Task<Outcome<UserInput>> GetAsync(AppDbContext db, Actor actor, long id)
    {
        RequireAdmin(actor);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
        return user is null
            ? Outcome<UserInput>.Missing()
            : Outcome<UserInput>.Success(new UserInput(user.Id, user.UserName, user.DisplayName, user.IsAdmin, user.IsActive, string.Empty));
    }

    /// <summary>Creates or changes an account. A password set here has to be changed by the person at the next sign-in.</summary>
    public static async Task<Outcome<long>> SaveAsync(AppDbContext db, Actor actor, UserInput input)
    {
        RequireAdmin(actor);
        var name = NormalizeUserName(input.UserName);
        if (UserNameProblem(name) is { } nameProblem)
        {
            return Outcome<long>.Fail(nameProblem, nameof(UserInput.UserName));
        }

        var taken = await db.Users.AnyAsync(u => u.UserName == name && u.Id != (input.Id ?? 0));
        if (taken)
        {
            return Outcome<long>.Fail("Diesen Benutzernamen gibt es schon.", nameof(UserInput.UserName));
        }

        var isNew = input.Id is null;
        if (isNew || input.Password.Length > 0)
        {
            if (Passwords.Problem(input.Password) is { } passwordProblem)
            {
                return Outcome<long>.Fail(passwordProblem, nameof(UserInput.Password));
            }
        }

        User user;
        if (isNew)
        {
            user = new User();
            db.Users.Add(user);
        }
        else
        {
            var found = await db.Users.FirstOrDefaultAsync(u => u.Id == input.Id);
            if (found is null)
            {
                return Outcome<long>.Missing();
            }

            user = found;
            var self = user.Id == actor.UserId;
            if (self && (!input.IsAdmin || !input.IsActive))
            {
                return Outcome<long>.Fail("Das eigene Konto kann nicht deaktiviert oder herabgestuft werden.", nameof(UserInput.IsAdmin));
            }
        }

        user.UserName = name;
        user.DisplayName = CleanDisplayName(input.DisplayName, name);
        user.IsAdmin = input.IsAdmin;
        user.IsActive = input.IsActive;
        if (input.Password.Length > 0)
        {
            user.PasswordHash = Passwords.Hash(user, input.Password);
            user.MustChangePassword = user.Id != actor.UserId;
            user.FailedLogins = 0;
            user.LockoutUntil = null;
        }

        await db.SaveChangesAsync();
        SessionService.Invalidate();
        if (!isNew && (!user.IsActive || input.Password.Length > 0))
        {
            // A deactivated account or a new password ends the sessions in the browser; a deactivated account also loses its devices.
            await SessionService.RevokeAllAsync(db, user.Id);
            if (!user.IsActive)
            {
                await DeviceService.RemoveAllAsync(db, user.Id);
            }
        }

        return Outcome<long>.Success(user.Id);
    }

    /// <summary>Deletes an account with everything it has: its bundles go, its devices and sessions end.</summary>
    public static async Task<Outcome> DeleteAsync(AppDbContext db, Actor actor, long id)
    {
        RequireAdmin(actor);
        if (id == actor.UserId)
        {
            return Outcome.Fail("Das eigene Konto kann hier nicht gelöscht werden.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null)
        {
            return Outcome.Missing();
        }

        await db.Bundles.Where(b => b.OwnerUserId == id).ExecuteDeleteAsync();
        await SessionService.RevokeAllAsync(db, id);
        await DeviceService.RemoveAllAsync(db, id);
        db.Users.Remove(user);
        await db.SaveChangesAsync();
        return Outcome.Success();
    }

    public static string? UserNameProblem(string normalizedName)
    {
        if (normalizedName.Length == 0)
        {
            return "Bitte einen Benutzernamen eingeben.";
        }

        return UserNameRegex().IsMatch(normalizedName)
            ? null
            : "Der Benutzername hat 2 bis 64 Zeichen: Buchstaben a-z, Ziffern und . _ - @ (am Anfang ein Buchstabe oder eine Ziffer).";
    }

    private static string CleanDisplayName(string? displayName, string fallback)
    {
        var name = (displayName ?? string.Empty).Trim();
        if (name.Length > 120)
        {
            name = name[..120];
        }

        return name.Length == 0 ? fallback : name;
    }

    internal static string EscapeLike(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
