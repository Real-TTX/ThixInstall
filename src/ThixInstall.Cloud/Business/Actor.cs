namespace ThixInstall.Cloud.Business;

/// <summary>
/// Who asks the business layer to do something: a person signed in in the browser, or a desktop device (then <see cref="DeviceId"/> is set).
/// Every function of the business layer takes it and decides with it what is allowed.
/// </summary>
public sealed record Actor(long UserId, bool IsAdmin, long? DeviceId = null, string? DeviceName = null)
{
    /// <summary>The name a clip created by this actor is marked with: the device, or "Web".</summary>
    public string Source => DeviceName ?? WebSource;

    public const string WebSource = "Web";
}

/// <summary>The answer of a change in the business layer: what came out of it, or what is wrong (already worded for the person, in German).</summary>
public sealed record Outcome<T>(T? Value, string? Error, string? Field = null, bool NotFound = false)
{
    public bool Ok => Error is null && !NotFound;

    public static Outcome<T> Success(T value) => new(value, null);

    public static Outcome<T> Fail(string error, string? field = null) => new(default, error, field);

    /// <summary>The record does not exist or the actor may not see it: both answer the same (404), so nothing can be probed.</summary>
    public static Outcome<T> Missing() => new(default, null, null, true);
}

/// <summary>An outcome without a value.</summary>
public sealed record Outcome(string? Error, string? Field = null, bool NotFound = false)
{
    public bool Ok => Error is null && !NotFound;

    public static Outcome Success() => new((string?)null);

    public static Outcome Fail(string error, string? field = null) => new(error, field);

    public static Outcome Missing() => new(null, null, true);
}

/// <summary>Raised when an actor asks for something only an administrator may do (the pages are guarded too; this is the check of the business layer).</summary>
public sealed class NotAllowedException(string message) : Exception(message);
