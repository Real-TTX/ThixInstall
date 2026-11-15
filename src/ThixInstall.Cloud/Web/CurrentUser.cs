using ThixInstall.Cloud.Business;

namespace ThixInstall.Cloud.Web;

/// <summary>The person signed in in the browser for this request, read once from the session (no query of its own).</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor)
{
    public const string ItemKey = "ThixInstall.User";
    public const string SessionItemKey = "ThixInstall.SessionId";

    public UserSnapshot? Snapshot => accessor.HttpContext?.Items[ItemKey] as UserSnapshot;

    public bool IsAuthenticated => Snapshot is not null;

    public long RequiredId => Snapshot?.Id ?? throw new InvalidOperationException("No signed-in user.");

    public bool IsAdmin => Snapshot?.IsAdmin == true;

    public string DisplayName => Snapshot?.DisplayName ?? string.Empty;

    public string RoleName => IsAdmin ? "Administrator" : "Benutzer";

    /// <summary>The person as the business layer knows them.</summary>
    public Actor Actor => Snapshot is { } user ? new Actor(user.Id, user.IsAdmin) : throw new InvalidOperationException("No signed-in user.");

    public long? SessionId => accessor.HttpContext?.Items[SessionItemKey] as long?;
}
