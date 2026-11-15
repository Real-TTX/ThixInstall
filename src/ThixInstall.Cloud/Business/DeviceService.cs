using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using ThixInstall.Cloud.Data;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Business;

/// <summary>A device as the API knows it after its token was checked.</summary>
public sealed record DeviceIdentity(long DeviceId, long UserId, bool IsAdmin, string Name, TargetOs Os, CpuArchitecture Architecture, bool SyncEnabled, DateTime LastSeenDate)
{
    public Actor Actor => new(UserId, IsAdmin, DeviceId, Name);
}

/// <summary>A device in the list.</summary>
public sealed record DeviceListItem(long Id, string Name, TargetOs Os, CpuArchitecture Architecture, string OsVersion, string AgentVersion, string OwnerName,
    string? ProfileName, bool SyncEnabled, DateTime LastSeenDate, int OpenActions, int Updates);

public sealed record DeviceQuery(string? Search, string? Os, string? Sync, long? ProfileId, string? Scope, string Sort, bool Descending, int Page, int PageSize);

/// <summary>A device on its page.</summary>
public sealed record DeviceView(long Id, string Name, TargetOs Os, CpuArchitecture Architecture, string OsVersion, string AgentVersion, string? HardwareId,
    long UserId, string OwnerName, string OwnerUserName, long? ProfileId, string? ProfileName, bool SyncEnabled, DateTime CreateDate, DateTime LastSeenDate,
    string? IpAddress, DateTime? InventoryDate, int Apps, int Updates, int OpenActions);

/// <summary>One installed app of a device, with the version of the catalog.</summary>
public sealed record DeviceAppView(string Name, string? Version, string? Publisher, long? PackageId, string? PackageName, string? LatestVersion, bool UpdateAvailable, string Source);

/// <summary>
/// The devices: enrollment (with the password of an account, the enrollment key of a profile or the token of a bundle), the token check of the API,
/// what a device reports (its facts, "Sync", its inventory) and what a person does with devices (list, profile, remove). A person manages their own
/// devices; an administrator every device.
/// </summary>
public static class DeviceService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<string, (DeviceIdentity Identity, DateTime Cached, int Generation)> Cache = new();
    private static int _generation;

    public static async Task<Outcome<EnrollResponse>> EnrollAsync(AppDbContext db, EnrollRequest request, string? ipAddress, CancellationToken ct = default)
    {
        if (FactsProblem(request.Device) is { } problem)
        {
            return Outcome<EnrollResponse>.Fail(problem, "device");
        }

        var ways = new[] { request.UserName is not null, request.EnrollmentKey is not null, request.BundleToken is not null }.Count(w => w);
        if (ways != 1)
        {
            return Outcome<EnrollResponse>.Fail("Bitte genau eines angeben: Benutzername und Passwort, Registrierungsschlüssel oder Paket-Token.");
        }

        User user;
        long? profileId = null;
        var sync = request.EnableSync;
        if (request.UserName is not null)
        {
            var signIn = await UserService.SignInAsync(db, request.UserName, request.Password ?? string.Empty, ct);
            if (!signIn.Ok)
            {
                return Outcome<EnrollResponse>.Fail(signIn.Error!, "password");
            }

            user = signIn.Value!;
        }
        else if (request.EnrollmentKey is not null)
        {
            var profile = Guid.TryParse(request.EnrollmentKey, out var key) ? await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.EnrollmentKey == key, ct) : null;
            var owner = profile?.CreateUserId is { } ownerId ? await db.Users.FirstOrDefaultAsync(u => u.Id == ownerId && u.IsActive, ct) : null;
            owner ??= profile is null ? null : await db.Users.Where(u => u.IsAdmin && u.IsActive).OrderBy(u => u.Id).FirstOrDefaultAsync(ct);
            if (profile is null || owner is null)
            {
                return Outcome<EnrollResponse>.Fail("Dieser Registrierungsschlüssel ist ungültig.", "password");
            }

            user = owner;
            profileId = profile.Id;
            sync = true;
        }
        else
        {
            var bundle = BundleService.ParseToken(request.BundleToken) is { } bundleToken ? await db.Bundles.AsNoTracking().FirstOrDefaultAsync(b => b.Token == bundleToken, ct) : null;
            var owner = bundle?.OwnerUserId is { } ownerId ? await db.Users.FirstOrDefaultAsync(u => u.Id == ownerId && u.IsActive, ct) : null;
            if (bundle is null || owner is null || !BundleService.CanEnroll(bundle))
            {
                return Outcome<EnrollResponse>.Fail("Mit diesem Paket kann sich kein Gerät (mehr) anmelden. Bitte ein neues Paket herunterladen.", "password");
            }

            user = owner;
            profileId = bundle.ProfileId;
            sync = true;
        }

        db.ActingUserId = user.Id;
        var facts = request.Device;
        var hardwareId = Text.Clean(facts.HardwareId, 100);
        var device = hardwareId is null ? null : await db.Devices.FirstOrDefaultAsync(d => d.UserId == user.Id && d.HardwareId == hardwareId, ct);
        if (device is null)
        {
            device = new Device { UserId = user.Id, HardwareId = hardwareId, CreateUserId = user.Id };
            db.Devices.Add(device);
        }

        var token = Text.NewToken();
        device.TokenHash = Text.Sha256Hex(token);
        Apply(device, facts);
        device.SyncEnabled = sync;
        device.LastSeenDate = DateTime.UtcNow;
        device.IpAddress = Text.Clean(ipAddress, 64);
        var profileChanged = profileId is not null && device.ProfileId != profileId;
        if (profileId is not null)
        {
            device.ProfileId = profileId;
        }

        await db.SaveChangesAsync(ct);
        Invalidate();
        if (device.SyncEnabled && device.ProfileId is not null && (profileChanged || device.CreateDate > DateTime.UtcNow.AddMinutes(-1)))
        {
            await ActionService.ApplyProfileAsync(db, device.Id, ct);
        }

        return Outcome<EnrollResponse>.Success(new EnrollResponse(token, await InfoAsync(db, device.Id, ct)));
    }

    private static string? FactsProblem(DeviceFacts? facts)
    {
        if (facts is null)
        {
            return "Die Angaben zum Gerät fehlen.";
        }

        var name = (facts.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 100)
        {
            return "Der Gerätename fehlt oder ist zu lang (höchstens 100 Zeichen).";
        }

        return Enum.IsDefined(facts.Os) && Enum.IsDefined(facts.Architecture) ? null : "Betriebssystem oder Architektur sind unbekannt.";
    }

    private static void Apply(Device device, DeviceFacts facts)
    {
        device.Name = Text.Clip(facts.Name.Trim(), 100);
        device.Os = facts.Os;
        device.OsVersion = Text.Clip((facts.OsVersion ?? string.Empty).Trim(), 100);
        device.Architecture = facts.Architecture;
        device.AgentVersion = Text.Clip((facts.AgentVersion ?? string.Empty).Trim(), 60);
        if (Text.Clean(facts.HardwareId, 100) is { } hardwareId)
        {
            device.HardwareId = hardwareId;
        }
    }

    /// <summary>GET /api/v1/device.</summary>
    public static async Task<DeviceInfo> InfoAsync(AppDbContext db, long deviceId, CancellationToken ct = default)
    {
        var row = await db.Devices.AsNoTracking().Where(d => d.Id == deviceId)
            .Select(d => new { d.Id, d.Name, d.User!.UserName, d.User.DisplayName, Profile = d.Profile != null ? d.Profile.Name : null, d.SyncEnabled })
            .FirstAsync(ct);
        return new DeviceInfo(row.Id, row.Name, row.UserName, row.DisplayName, row.Profile, row.SyncEnabled, AppInfo.Version);
    }

    /// <summary>PUT /api/v1/device: the device changes "Sync" or tells its facts again (a new version, a new name).</summary>
    public static async Task<Outcome<DeviceInfo>> UpdateSelfAsync(AppDbContext db, DeviceIdentity identity, DeviceUpdate update, CancellationToken ct = default)
    {
        if (update.Facts is not null && FactsProblem(update.Facts) is { } problem)
        {
            return Outcome<DeviceInfo>.Fail(problem, "facts");
        }

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == identity.DeviceId, ct);
        if (device is null)
        {
            return Outcome<DeviceInfo>.Missing();
        }

        var syncTurnedOn = update.SyncEnabled == true && !device.SyncEnabled;
        if (update.Facts is not null)
        {
            Apply(device, update.Facts);
        }

        if (update.SyncEnabled is { } sync)
        {
            device.SyncEnabled = sync;
        }

        await db.SaveChangesAsync(ct);
        Invalidate();
        if (syncTurnedOn && device.ProfileId is not null)
        {
            await ActionService.ApplyProfileAsync(db, device.Id, ct);
        }

        return Outcome<DeviceInfo>.Success(await InfoAsync(db, device.Id, ct));
    }

    /// <summary>PUT /api/v1/device/inventory: replaces the last inventory (only with "Sync").</summary>
    public static async Task<Outcome> ReportInventoryAsync(AppDbContext db, DeviceIdentity identity, InventoryReport report, CancellationToken ct = default)
    {
        if (!identity.SyncEnabled)
        {
            return Outcome.Fail("Sync ist auf diesem Gerät ausgeschaltet: das Inventar wird nicht übertragen.");
        }

        if (report.Apps is null || report.Apps.Count > ApiContract.MaxInventoryApps)
        {
            return Outcome.Fail($"Das Inventar hat höchstens {ApiContract.MaxInventoryApps} Einträge.", "apps");
        }

        var known = await db.Packages.AsNoTracking().Select(p => p.Id).ToListAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.DeviceApps.Where(a => a.DeviceId == identity.DeviceId).ExecuteDeleteAsync(ct);
        foreach (var app in report.Apps.Where(a => !string.IsNullOrWhiteSpace(a.Name)).DistinctBy(a => (a.Name, a.Version, a.Source)))
        {
            db.DeviceApps.Add(new DeviceApp
            {
                DeviceId = identity.DeviceId,
                Name = Text.Clip(app.Name.Trim(), 300),
                Version = Text.Clean(app.Version, 100),
                Publisher = Text.Clean(app.Publisher, 200),
                PackageId = app.PackageId is { } id && known.Contains(id) ? id : null,
                Source = Text.Clip((app.Source ?? string.Empty).Trim(), 20),
            });
        }

        await db.SaveChangesAsync(ct);
        await db.Devices.Where(d => d.Id == identity.DeviceId).ExecuteUpdateAsync(s => s.SetProperty(d => d.InventoryDate, DateTime.UtcNow), ct);
        await transaction.CommitAsync(ct);
        return Outcome.Success();
    }

    /// <summary>The device of a bearer token, or null when the token is unknown, the device removed or the account inactive. Cached for 20 seconds.</summary>
    public static async Task<DeviceIdentity?> AuthenticateAsync(AppDbContext db, string? bearer, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bearer) || bearer.Length > 100)
        {
            return null;
        }

        var hash = Text.Sha256Hex(bearer.Trim());
        var now = DateTime.UtcNow;
        if (Cache.TryGetValue(hash, out var hit) && hit.Generation == _generation && now - hit.Cached < CacheFor)
        {
            return hit.Identity;
        }

        var row = await db.Devices.AsNoTracking()
            .Where(d => d.TokenHash == hash)
            .Select(d => new { d.Id, d.UserId, d.Name, d.Os, d.Architecture, d.SyncEnabled, d.LastSeenDate, d.User!.IsAdmin, d.User.IsActive, d.User.UpdateState })
            .FirstOrDefaultAsync(ct);
        if (row is null || !row.IsActive || row.UpdateState == SoftEntity.Deleted)
        {
            Cache.TryRemove(hash, out _);
            return null;
        }

        var identity = new DeviceIdentity(row.Id, row.UserId, row.IsAdmin, row.Name, row.Os, row.Architecture, row.SyncEnabled, row.LastSeenDate);
        Cache[hash] = (identity, now, _generation);
        return identity;
    }

    /// <summary>Notes that the device was seen (at most every two minutes), with its address and the version of the app.</summary>
    public static async Task TouchAsync(AppDbContext db, DeviceIdentity device, string? ipAddress, string? agentVersion, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (now - device.LastSeenDate < TouchEvery)
        {
            return;
        }

        var ip = Text.Clip(ipAddress, 64);
        var version = Text.Clip((agentVersion ?? string.Empty).Trim(), 60);
        if (version.Length > 0)
        {
            await db.Devices.Where(d => d.Id == device.DeviceId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenDate, now).SetProperty(d => d.IpAddress, ip).SetProperty(d => d.AgentVersion, version), ct);
        }
        else
        {
            await db.Devices.Where(d => d.Id == device.DeviceId).ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenDate, now).SetProperty(d => d.IpAddress, ip), ct);
        }

        foreach (var (token, entry) in Cache.Where(e => e.Value.Identity.DeviceId == device.DeviceId).ToList())
        {
            Cache[token] = (entry.Identity with { LastSeenDate = now }, entry.Cached, entry.Generation);
        }
    }

    /// <summary>The device signs itself out: its token ends (the device stays in the list as removed).</summary>
    public static async Task LogoutAsync(AppDbContext db, DeviceIdentity identity, CancellationToken ct = default)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == identity.DeviceId, ct);
        if (device is not null)
        {
            db.Devices.Remove(device);
            await db.SaveChangesAsync(ct);
        }

        Invalidate();
    }

    private static IQueryable<Device> Visible(AppDbContext db, Actor actor) => db.Devices.AsNoTracking().Where(d => actor.IsAdmin || d.UserId == actor.UserId);

    public static async Task<(IReadOnlyList<DeviceListItem> Items, int Total, int Matching)> ListAsync(AppDbContext db, Actor actor, DeviceQuery query, CancellationToken ct = default)
    {
        var all = Visible(db, actor);
        if (actor.IsAdmin && query.Scope == "mine")
        {
            // an administrator sees every device; "Meine" narrows it to their own
            all = all.Where(d => d.UserId == actor.UserId);
        }

        var total = await all.CountAsync(ct);
        var filtered = all;
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = "%" + UserService.EscapeLike(query.Search.Trim()) + "%";
            filtered = filtered.Where(d => EF.Functions.ILike(d.Name, pattern, "\\") || EF.Functions.ILike(d.OsVersion, pattern, "\\")
                                           || EF.Functions.ILike(d.User!.DisplayName, pattern, "\\") || EF.Functions.ILike(d.User!.UserName, pattern, "\\"));
        }

        filtered = query.Os switch
        {
            "windows" => filtered.Where(d => d.Os == TargetOs.Windows),
            "macos" => filtered.Where(d => d.Os == TargetOs.MacOS),
            _ => filtered,
        };
        filtered = query.Sync switch
        {
            "on" => filtered.Where(d => d.SyncEnabled),
            "off" => filtered.Where(d => !d.SyncEnabled),
            _ => filtered,
        };
        if (query.ProfileId is { } profileId)
        {
            filtered = filtered.Where(d => d.ProfileId == profileId);
        }

        var matching = await filtered.CountAsync(ct);
        filtered = (query.Sort, query.Descending) switch
        {
            ("name", false) => filtered.OrderBy(d => d.Name),
            ("name", true) => filtered.OrderByDescending(d => d.Name),
            ("created", false) => filtered.OrderBy(d => d.CreateDate),
            ("created", true) => filtered.OrderByDescending(d => d.CreateDate),
            (_, false) => filtered.OrderBy(d => d.LastSeenDate),
            _ => filtered.OrderByDescending(d => d.LastSeenDate),
        };
        var rows = await filtered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(d => new
            {
                d.Id, d.Name, d.Os, d.Architecture, d.OsVersion, d.AgentVersion, Owner = d.User!.DisplayName, Profile = d.Profile != null ? d.Profile.Name : null,
                d.SyncEnabled, d.LastSeenDate,
                Open = db.Actions.Count(a => a.DeviceId == d.Id && (a.State == ActionState.Pending || a.State == ActionState.Running)),
            })
            .ToListAsync(ct);
        var updates = await UpdateCountsAsync(db, rows.Select(r => r.Id).ToList(), ct);
        var items = rows.Select(r => new DeviceListItem(r.Id, r.Name, r.Os, r.Architecture, r.OsVersion, r.AgentVersion, r.Owner, r.Profile, r.SyncEnabled, r.LastSeenDate,
            r.Open, updates.GetValueOrDefault(r.Id))).ToList();
        return (items, total, matching);
    }

    public static async Task<Outcome<DeviceView>> GetAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        var row = await Visible(db, actor).Where(d => d.Id == id)
            .Select(d => new
            {
                Device = d, Owner = d.User!.DisplayName, OwnerUserName = d.User.UserName, Profile = d.Profile != null ? d.Profile.Name : null,
                Apps = db.DeviceApps.Count(a => a.DeviceId == d.Id),
                Open = db.Actions.Count(a => a.DeviceId == d.Id && (a.State == ActionState.Pending || a.State == ActionState.Running)),
            })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return Outcome<DeviceView>.Missing();
        }

        var d = row.Device;
        var updates = (await UpdateCountsAsync(db, [d.Id], ct)).GetValueOrDefault(d.Id);
        return Outcome<DeviceView>.Success(new DeviceView(d.Id, d.Name, d.Os, d.Architecture, d.OsVersion, d.AgentVersion, d.HardwareId, d.UserId, row.Owner, row.OwnerUserName,
            d.ProfileId, row.Profile, d.SyncEnabled, d.CreateDate, d.LastSeenDate, d.IpAddress, d.InventoryDate, row.Apps, updates, row.Open));
    }

    /// <summary>The inventory of a device with the newest version of the catalog next to each app it knows.</summary>
    public static async Task<Outcome<IReadOnlyList<DeviceAppView>>> AppsAsync(AppDbContext db, Actor actor, long id, string? search, bool updatesOnly, CancellationToken ct = default)
    {
        var device = await Visible(db, actor).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            return Outcome<IReadOnlyList<DeviceAppView>>.Missing();
        }

        var apps = await db.DeviceApps.AsNoTracking().Where(a => a.DeviceId == id).OrderBy(a => a.Name).ToListAsync(ct);
        var packageIds = apps.Where(a => a.PackageId != null).Select(a => a.PackageId!.Value).Distinct().ToList();
        var names = await db.Packages.AsNoTracking().Where(p => packageIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var latest = await LatestVersionsAsync(db, packageIds, device.Os, device.Architecture, ct);
        var views = apps.Select(a =>
        {
            var newest = a.PackageId is { } pid ? latest.GetValueOrDefault(pid) : null;
            return new DeviceAppView(a.Name, a.Version, a.Publisher, a.PackageId, a.PackageId is { } p ? names.GetValueOrDefault(p) : null, newest,
                AppVersion.IsNewer(newest, a.Version), a.Source);
        });
        if (!string.IsNullOrWhiteSpace(search))
        {
            views = views.Where(v => v.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) || (v.Publisher?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (updatesOnly)
        {
            views = views.Where(v => v.UpdateAvailable);
        }

        return Outcome<IReadOnlyList<DeviceAppView>>.Success(views.OrderByDescending(v => v.UpdateAvailable).ThenByDescending(v => v.PackageId != null).ThenBy(v => v.Name).ToList());
    }

    private static async Task<Dictionary<long, string?>> LatestVersionsAsync(AppDbContext db, IReadOnlyCollection<long> packageIds, TargetOs os, CpuArchitecture architecture, CancellationToken ct)
        => await db.Releases.AsNoTracking().Where(r => packageIds.Contains(r.PackageId) && r.Os == os && r.Architecture == architecture)
            .ToDictionaryAsync(r => r.PackageId, r => r.Version, ct);

    /// <summary>How many catalog apps of each device have a newer version.</summary>
    private static async Task<Dictionary<long, int>> UpdateCountsAsync(AppDbContext db, IReadOnlyCollection<long> deviceIds, CancellationToken ct)
    {
        var rows = await db.DeviceApps.AsNoTracking().Where(a => deviceIds.Contains(a.DeviceId) && a.PackageId != null)
            .Join(db.Devices.AsNoTracking(), a => a.DeviceId, d => d.Id, (a, d) => new { a.DeviceId, a.PackageId, a.Version, d.Os, d.Architecture })
            .ToListAsync(ct);
        var packageIds = rows.Select(r => r.PackageId!.Value).Distinct().ToList();
        var releases = await db.Releases.AsNoTracking().Where(r => packageIds.Contains(r.PackageId)).Select(r => new { r.PackageId, r.Os, r.Architecture, r.Version }).ToListAsync(ct);
        return rows.Where(r => AppVersion.IsNewer(releases.FirstOrDefault(x => x.PackageId == r.PackageId && x.Os == r.Os && x.Architecture == r.Architecture)?.Version, r.Version))
            .GroupBy(r => r.DeviceId).ToDictionary(g => g.Key, g => g.Select(x => x.PackageId).Distinct().Count());
    }

    /// <summary>Gives a device a profile (or none). With "Sync" the apps and scripts of the profile become actions at once.</summary>
    public static async Task<Outcome> AssignProfileAsync(AppDbContext db, Actor actor, long id, long? profileId, CancellationToken ct = default)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id && (actor.IsAdmin || d.UserId == actor.UserId), ct);
        if (device is null)
        {
            return Outcome.Missing();
        }

        if (profileId is { } pid && !await db.Profiles.AnyAsync(p => p.Id == pid, ct))
        {
            return Outcome.Fail("Dieses Profil gibt es nicht (mehr).", "ProfileId");
        }

        var changed = device.ProfileId != profileId;
        device.ProfileId = profileId;
        await db.SaveChangesAsync(ct);
        Invalidate();
        if (changed && profileId is not null && device.SyncEnabled)
        {
            await ActionService.ApplyProfileAsync(db, device.Id, ct);
        }

        return Outcome.Success();
    }

    /// <summary>Removes a device: its token answers 401 from now on; its open actions are cancelled.</summary>
    public static async Task<Outcome> RemoveAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id && (actor.IsAdmin || d.UserId == actor.UserId), ct);
        if (device is null)
        {
            return Outcome.Missing();
        }

        db.Devices.Remove(device);
        await db.SaveChangesAsync(ct);
        await db.Actions.Where(a => a.DeviceId == id && a.State == ActionState.Pending).ExecuteUpdateAsync(s => s.SetProperty(a => a.State, ActionState.Cancelled), ct);
        Invalidate();
        RevisionSignals.Notify(id);
        return Outcome.Success();
    }

    /// <summary>Every device of an account ends (the account was deactivated or deleted).</summary>
    public static async Task RemoveAllAsync(AppDbContext db, long userId)
    {
        await db.Devices.Where(d => d.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.UpdateState, SoftEntity.Deleted).SetProperty(d => d.UpdateDate, DateTime.UtcNow));
        Invalidate();
    }

    /// <summary>The devices a person may send actions to (for the pickers).</summary>
    public static async Task<IReadOnlyList<(long Id, string Name, TargetOs Os)>> ChoicesAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
        => (await Visible(db, actor).OrderBy(d => d.Name).Select(d => new { d.Id, d.Name, d.Os }).ToListAsync(ct)).Select(d => (d.Id, d.Name, d.Os)).ToList();

    public static string OsIcon(TargetOs os) => os == TargetOs.MacOS ? "apple" : "windows";

    public static string ArchitectureName(CpuArchitecture architecture) => architecture == CpuArchitecture.Arm64 ? "ARM64" : "x64";

    internal static void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        Cache.Clear();
    }
}
