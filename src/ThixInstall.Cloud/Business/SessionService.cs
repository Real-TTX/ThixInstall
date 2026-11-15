using Microsoft.EntityFrameworkCore;
using ThixInstall.Cloud.Data;

namespace ThixInstall.Cloud.Business;

/// <summary>What the pages need to know about the signed-in person, read once per request from the session.</summary>
public sealed record UserSnapshot(long Id, string UserName, string DisplayName, bool IsAdmin, string Theme, bool MustChangePassword);

/// <summary>A valid session of the browser.</summary>
public sealed record SessionInfo(long SessionId, UserSnapshot User, DateTime ExpiresDate, DateTime LastSeenDate, bool Remember);

/// <summary>
/// The sessions of the browser. A session is a row in <c>UserSession</c>; the cookie carries only its token, so a session survives a restart and
/// can be ended (sign-out, a new password, a deactivated account).
/// </summary>
public static class SessionService
{
    public static readonly TimeSpan ShortSession = TimeSpan.FromHours(12);
    public static readonly TimeSpan LongSession = TimeSpan.FromDays(60);
    public static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(5);

    private static int _generation;

    /// <summary>Changes whenever a session or an account changed: caches of sessions keyed by it are stale.</summary>
    public static int Generation => _generation;

    public static void Invalidate() => Interlocked.Increment(ref _generation);

    public static UserSnapshot Snapshot(User user) => new(user.Id, user.UserName, user.DisplayName, user.IsAdmin, user.Theme, user.MustChangePassword);

    public static async Task<UserSession> CreateAsync(AppDbContext db, long userId, bool remember, string? userAgent, string? ipAddress)
    {
        var now = DateTime.UtcNow;
        var session = new UserSession
        {
            UserId = userId,
            Token = Guid.NewGuid(),
            LastSeenDate = now,
            ExpiresDate = now + (remember ? LongSession : ShortSession),
            CreateUserId = userId,
            UserAgent = Trim(userAgent, 300),
            IpAddress = Trim(ipAddress, 64),
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    /// <summary>The session of a token while it is valid and its account active; null otherwise.</summary>
    public static async Task<SessionInfo?> LoadAsync(AppDbContext db, Guid token)
    {
        var row = await db.Sessions.AsNoTracking()
            .Where(s => s.Token == token)
            .Select(s => new { s.Id, s.IsRevoked, s.ExpiresDate, s.LastSeenDate, s.User })
            .FirstOrDefaultAsync();
        if (row is null || row.IsRevoked || row.User is null || !row.User.IsActive || row.ExpiresDate < DateTime.UtcNow)
        {
            return null;
        }

        var remember = row.ExpiresDate - row.LastSeenDate > ShortSession + TimeSpan.FromMinutes(1);
        return new SessionInfo(row.Id, Snapshot(row.User), row.ExpiresDate, row.LastSeenDate, remember);
    }

    /// <summary>Keeps a session alive while it is used (at most every <see cref="TouchEvery"/>).</summary>
    public static async Task<SessionInfo> TouchAsync(AppDbContext db, SessionInfo session)
    {
        var now = DateTime.UtcNow;
        if (now - session.LastSeenDate < TouchEvery)
        {
            return session;
        }

        var expires = now + (session.Remember ? LongSession : ShortSession);
        await db.Sessions.Where(s => s.Id == session.SessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenDate, now).SetProperty(x => x.ExpiresDate, expires));
        return session with { LastSeenDate = now, ExpiresDate = expires };
    }

    public static async Task RevokeAsync(AppDbContext db, Guid token)
    {
        await db.Sessions.Where(s => s.Token == token).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRevoked, true));
        Invalidate();
    }

    /// <summary>Ends every session of an account, except one (the current one, after a change of the password).</summary>
    public static async Task RevokeAllAsync(AppDbContext db, long userId, long? exceptSessionId = null)
    {
        await db.Sessions.Where(s => s.UserId == userId && s.Id != exceptSessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRevoked, true));
        Invalidate();
    }

    /// <summary>Removes ended and expired sessions (the housekeeping).</summary>
    public static async Task<int> CleanUpAsync(AppDbContext db, CancellationToken ct = default)
    {
        var limit = DateTime.UtcNow.AddDays(-1);
        return await db.Sessions.Where(s => s.IsRevoked || s.ExpiresDate < limit).ExecuteDeleteAsync(ct);
    }

    private static string? Trim(string? text, int max) => string.IsNullOrEmpty(text) ? null : text.Length <= max ? text : text[..max];
}
