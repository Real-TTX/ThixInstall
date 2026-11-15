using Microsoft.AspNetCore.Identity;
using ThixInstall.Cloud.Data;

namespace ThixInstall.Cloud.Business;

/// <summary>Passwords: PBKDF2 through ASP.NET's <see cref="PasswordHasher{TUser}"/>, and what a new password has to be.</summary>
public static class Passwords
{
    public const int MinLength = 8;
    public const int MaxLength = 200;

    private static readonly PasswordHasher<User> Hasher = new();

    public static string Hash(User user, string password) => Hasher.HashPassword(user, password);

    public static bool Verify(User user, string password)
    {
        if (string.IsNullOrEmpty(user.PasswordHash) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        return Hasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;
    }

    /// <summary>What is wrong with a new password, or null when it is fine.</summary>
    public static string? Problem(string password, string? repeat = null)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "Bitte ein Passwort eingeben.";
        }

        if (password.Length < MinLength)
        {
            return $"Das Passwort braucht mindestens {MinLength} Zeichen.";
        }

        if (password.Length > MaxLength)
        {
            return $"Das Passwort darf höchstens {MaxLength} Zeichen haben.";
        }

        return repeat is not null && repeat != password ? "Die beiden Passwörter stimmen nicht überein." : null;
    }

    /// <summary>A hash to compare against when the user name is unknown, so that a wrong name takes as long as a wrong password.</summary>
    internal static readonly string DummyHash = Hasher.HashPassword(new User(), Guid.NewGuid().ToString("N"));
}
