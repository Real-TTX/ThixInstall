using Microsoft.EntityFrameworkCore;
using ThixInstall.Cloud.Data;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Business;

public sealed record ProfileListItem(long Id, string Name, string? Description, int Apps, int Scripts, int Devices, DateTime UpdateDate);

public sealed record ProfileView(long Id, string Name, string? Description, string EnrollmentKey, int Devices);

/// <summary>An app or script of a profile.</summary>
public sealed record ProfileItemView(long Id, long? PackageId, long? ScriptId, string Name, string Category, string Systems, bool HasIcon, int SortOrder);

/// <summary>
/// The profiles: apps and scripts for devices, and the key for the auto-enrollment (a device that enrolls with it belongs to the creator of the profile
/// and gets the profile at once). Administrators keep them; every signed-in person may give one of them to their own devices and bundles.
/// </summary>
public static class ProfileService
{
    public static async Task<IReadOnlyList<(long Id, string Name)>> ChoicesAsync(AppDbContext db, CancellationToken ct = default)
        => (await db.Profiles.AsNoTracking().OrderBy(p => p.Name).Select(p => new { p.Id, p.Name }).ToListAsync(ct)).Select(p => (p.Id, p.Name)).ToList();

    public static async Task<(IReadOnlyList<ProfileListItem> Items, int Total, int Matching)> ListAsync(AppDbContext db, Actor actor, string? search, string sort, bool descending,
        int page, int pageSize, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var all = db.Profiles.AsNoTracking();
        var total = await all.CountAsync(ct);
        var filtered = all;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + UserService.EscapeLike(search.Trim()) + "%";
            filtered = filtered.Where(p => EF.Functions.ILike(p.Name, pattern, "\\") || EF.Functions.ILike(p.Description ?? "", pattern, "\\"));
        }

        var matching = await filtered.CountAsync(ct);
        filtered = (sort, descending) switch
        {
            ("updated", false) => filtered.OrderBy(p => p.UpdateDate),
            ("updated", true) => filtered.OrderByDescending(p => p.UpdateDate),
            (_, true) => filtered.OrderByDescending(p => p.Name),
            _ => filtered.OrderBy(p => p.Name),
        };
        var items = await filtered.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new ProfileListItem(p.Id, p.Name, p.Description,
                db.ProfileItems.Count(i => i.ProfileId == p.Id && i.PackageId != null),
                db.ProfileItems.Count(i => i.ProfileId == p.Id && i.ScriptId != null),
                db.Devices.Count(d => d.ProfileId == p.Id), p.UpdateDate))
            .ToListAsync(ct);
        return (items, total, matching);
    }

    public static async Task<Outcome<ProfileView>> GetAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var p = await db.Profiles.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new ProfileView(x.Id, x.Name, x.Description, x.EnrollmentKey.ToString(), db.Devices.Count(d => d.ProfileId == x.Id)))
            .FirstOrDefaultAsync(ct);
        return p is null ? Outcome<ProfileView>.Missing() : Outcome<ProfileView>.Success(p);
    }

    public static async Task<Outcome<long>> SaveAsync(AppDbContext db, Actor actor, long? id, string name, string? description, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var clean = Text.Clean(name, 120);
        if (clean is null)
        {
            return Outcome<long>.Fail("Bitte einen Namen eingeben.", "Name");
        }

        if (await db.Profiles.AnyAsync(p => p.Name == clean && p.Id != (id ?? 0), ct))
        {
            return Outcome<long>.Fail("Ein Profil mit diesem Namen gibt es schon.", "Name");
        }

        Profile profile;
        if (id is null)
        {
            profile = new Profile { EnrollmentKey = Guid.NewGuid() };
            db.Profiles.Add(profile);
        }
        else
        {
            var found = await db.Profiles.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (found is null)
            {
                return Outcome<long>.Missing();
            }

            profile = found;
        }

        profile.Name = clean;
        profile.Description = Text.Clean(description, 1000);
        await db.SaveChangesAsync(ct);
        return Outcome<long>.Success(profile.Id);
    }

    /// <summary>Deletes a profile: its devices keep their apps and lose the profile; its key no longer enrolls.</summary>
    public static async Task<Outcome> DeleteAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var profile = await db.Profiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null)
        {
            return Outcome.Missing();
        }

        await db.Devices.Where(d => d.ProfileId == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.ProfileId, (long?)null), ct);
        await db.Bundles.Where(b => b.ProfileId == id).ExecuteUpdateAsync(s => s.SetProperty(b => b.ProfileId, (long?)null), ct);
        await db.ProfileItems.Where(i => i.ProfileId == id).ExecuteDeleteAsync(ct);
        db.Profiles.Remove(profile);
        await db.SaveChangesAsync(ct);
        DeviceService.Invalidate();
        return Outcome.Success();
    }

    /// <summary>A new enrollment key: the old one no longer enrolls devices (enrolled devices stay).</summary>
    public static async Task<Outcome> RegenerateKeyAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var profile = await db.Profiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null)
        {
            return Outcome.Missing();
        }

        profile.EnrollmentKey = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        return Outcome.Success();
    }

    public static async Task<Outcome<IReadOnlyList<ProfileItemView>>> ItemsAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        if (!await db.Profiles.AnyAsync(p => p.Id == id, ct))
        {
            return Outcome<IReadOnlyList<ProfileItemView>>.Missing();
        }

        var rows = await db.ProfileItems.AsNoTracking().Where(i => i.ProfileId == id).OrderBy(i => i.SortOrder)
            .Select(i => new
            {
                i.Id, i.PackageId, i.ScriptId, i.SortOrder,
                Package = i.Package, Script = i.Script,
                HasIcon = i.PackageId != null && db.PackageIcons.Any(x => x.PackageId == i.PackageId),
            })
            .ToListAsync(ct);
        var items = rows.Where(r => r.Package is not null || r.Script is not null).Select(r => r.Package is { } p
            ? new ProfileItemView(r.Id, r.PackageId, null, p.Name, p.Category, Systems(p), r.HasIcon, r.SortOrder)
            : new ProfileItemView(r.Id, null, r.ScriptId, r.Script!.Name, r.Script.Category, CatalogService.OsName(r.Script.Os), false, r.SortOrder)).ToList();
        return Outcome<IReadOnlyList<ProfileItemView>>.Success(items);
    }

    public static string Systems(Package p) => (p.WindowsMethod, p.MacMethod) switch
    {
        (not null, not null) => "Windows, macOS",
        (not null, null) => "Windows",
        (null, not null) => "macOS",
        _ => "–",
    };

    /// <summary>Adds an app or a script; the devices of the profile with "Sync" get it as an action at once. Returns how many devices got it.</summary>
    public static async Task<Outcome<int>> AddItemAsync(AppDbContext db, Actor actor, long id, long? packageId, long? scriptId, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        if (!await db.Profiles.AnyAsync(p => p.Id == id, ct))
        {
            return Outcome<int>.Missing();
        }

        if ((packageId is null) == (scriptId is null))
        {
            return Outcome<int>.Fail("Bitte eine App oder ein Skript wählen.", "Item");
        }

        if (packageId is { } pid && !await db.Packages.AnyAsync(p => p.Id == pid, ct) || scriptId is { } sid && !await db.Scripts.AnyAsync(s => s.Id == sid, ct))
        {
            return Outcome<int>.Fail("Diesen Eintrag gibt es nicht (mehr).", "Item");
        }

        if (await db.ProfileItems.AnyAsync(i => i.ProfileId == id && i.PackageId == packageId && i.ScriptId == scriptId, ct))
        {
            return Outcome<int>.Fail("Das ist schon im Profil.", "Item");
        }

        var order = await db.ProfileItems.Where(i => i.ProfileId == id).MaxAsync(i => (int?)i.SortOrder, ct) ?? -1;
        db.ProfileItems.Add(new ProfileItem { ProfileId = id, PackageId = packageId, ScriptId = scriptId, SortOrder = order + 1 });
        await db.SaveChangesAsync(ct);

        var devices = await db.Devices.AsNoTracking().Where(d => d.ProfileId == id && d.SyncEnabled).Select(d => d.Id).ToListAsync(ct);
        var reached = 0;
        foreach (var deviceId in devices)
        {
            if (await ActionService.ApplyProfileAsync(db, deviceId, ct) > 0)
            {
                reached++;
            }
        }

        return Outcome<int>.Success(reached);
    }

    public static async Task<Outcome> RemoveItemAsync(AppDbContext db, Actor actor, long id, long itemId, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var removed = await db.ProfileItems.Where(i => i.Id == itemId && i.ProfileId == id).ExecuteDeleteAsync(ct);
        return removed == 0 ? Outcome.Missing() : Outcome.Success();
    }

    /// <summary>Moves an item one place up or down.</summary>
    public static async Task<Outcome> MoveItemAsync(AppDbContext db, Actor actor, long id, long itemId, int direction, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var items = await db.ProfileItems.Where(i => i.ProfileId == id).OrderBy(i => i.SortOrder).ThenBy(i => i.Id).ToListAsync(ct);
        var index = items.FindIndex(i => i.Id == itemId);
        if (index < 0)
        {
            return Outcome.Missing();
        }

        var other = index + Math.Sign(direction);
        if (other >= 0 && other < items.Count)
        {
            (items[index], items[other]) = (items[other], items[index]);
        }

        for (var i = 0; i < items.Count; i++)
        {
            items[i].SortOrder = i;
        }

        await db.SaveChangesAsync(ct);
        return Outcome.Success();
    }

    /// <summary>The bundle that installs ThixInstall with this profile (for the download button of "Registrierung"); made once and reused.</summary>
    public static async Task<Outcome<string>> EnrollmentBundleAsync(AppDbContext db, Actor actor, long id, TargetOs os, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null)
        {
            return Outcome<string>.Missing();
        }

        var limit = DateTime.UtcNow.AddDays(-(ApiContract.BundleEnrollmentDays - 1));
        var existing = await db.Bundles.AsNoTracking()
            .Where(b => b.ProfileId == id && b.Os == os && b.IncludesAgent && b.OwnerUserId == actor.UserId && b.CreateDate > limit && !db.BundleItems.Any(i => i.BundleId == b.Id))
            .OrderByDescending(b => b.CreateDate).Select(b => b.Token).FirstOrDefaultAsync(ct);
        if (existing != Guid.Empty)
        {
            return Outcome<string>.Success(existing.ToString("N"));
        }

        return await BundleService.CreateAsync(db, actor, new BundleRequest(os, [], [], $"Registrierung {profile.Name}", true, id), ct);
    }
}
