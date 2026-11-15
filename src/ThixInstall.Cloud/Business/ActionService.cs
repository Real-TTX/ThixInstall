using Microsoft.EntityFrameworkCore;
using ThixInstall.Cloud.Data;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Business;

/// <summary>An action in the lists.</summary>
public sealed record ActionListItem(long Id, long DeviceId, string DeviceName, TargetOs Os, ActionKind Kind, ActionState State, ActionOrigin Origin, string Title,
    string? RequestedBy, DateTime CreateDate, DateTime? FinishedDate, int? ExitCode);

public sealed record ActionQuery(string? Search, string? State, string? Origin, long? DeviceId, string Sort, bool Descending, int Page, int PageSize);

/// <summary>An action on its page.</summary>
public sealed record ActionView(long Id, long DeviceId, string DeviceName, TargetOs Os, ActionKind Kind, ActionState State, ActionOrigin Origin, string Title,
    long? PackageId, string? PackageName, long? ScriptId, string? ScriptName, string? RequestedBy, string? ProfileName, DateTime CreateDate, DateTime? StartedDate,
    DateTime? FinishedDate, int? ExitCode, string? Output, string? InstalledVersion);

/// <summary>What a person sends to a device (or to every device of a profile).</summary>
public sealed record PushRequest(ActionKind Kind, long? PackageId, long? ScriptId);

/// <summary>
/// The actions of the devices ("Aufträge"): what a person sends (only to devices with "Sync"), what a profile asks for, what a device did on its own.
/// A device waits for its actions with a long poll on the revisions (<see cref="Revisions"/>), starts one, runs it and reports how it ended.
/// </summary>
public static class ActionService
{
    // ---- the API of the devices --------------------------------------------------------------------------------------------------------------------

    /// <summary>GET /api/v1/device/actions/wait: the latest revision as soon as it is above <paramref name="since"/>, or after the timeout.</summary>
    public static async Task<long> WaitAsync(AppDbContext db, long deviceId, long since, TimeSpan timeout, CancellationToken ct)
    {
        var signal = RevisionSignals.Next(deviceId);
        var latest = await Revisions.LatestAsync(db, deviceId, ct);
        if (latest > since)
        {
            return latest;
        }

        await Task.WhenAny(signal, Task.Delay(timeout, ct));
        ct.ThrowIfCancellationRequested();
        return await Revisions.LatestAsync(db, deviceId, ct);
    }

    /// <summary>GET /api/v1/device/actions: the pending actions of the device, oldest first, with the package or script to run.</summary>
    public static async Task<ActionsResponse> OpenAsync(AppDbContext db, DeviceIdentity device, CancellationToken ct = default)
    {
        var latest = await Revisions.LatestAsync(db, device.DeviceId, ct);
        if (!device.SyncEnabled)
        {
            return new ActionsResponse([], latest);
        }

        var rows = await db.Actions.AsNoTracking().Where(a => a.DeviceId == device.DeviceId && a.State == ActionState.Pending)
            .OrderBy(a => a.Revision).Take(100).ToListAsync(ct);
        var packageIds = rows.Where(a => a.PackageId != null).Select(a => a.PackageId!.Value).Distinct().ToList();
        var scriptIds = rows.Where(a => a.ScriptId != null).Select(a => a.ScriptId!.Value).Distinct().ToList();
        var packages = await CatalogService.PackagesForAsync(db, device.Os, device.Architecture, false, packageIds, ct);
        var scripts = await db.Scripts.AsNoTracking().Where(s => scriptIds.Contains(s.Id)).ToListAsync(ct);
        var users = await UserNamesAsync(db, rows.Select(a => a.RequestedByUserId), ct);
        var profileIds = rows.Where(a => a.ProfileId != null).Select(a => a.ProfileId!.Value).Distinct().ToList();
        var profiles = await db.Profiles.AsNoTracking().IgnoreQueryFilters().Where(p => profileIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var actions = rows.Select(a => new ActionDto
        {
            Id = a.Id,
            Kind = a.Kind,
            State = a.State,
            Origin = a.Origin,
            Title = a.Title,
            Package = a.PackageId is { } pid ? packages.FirstOrDefault(p => p.Id == pid) : null,
            Script = a.ScriptId is { } sid && scripts.FirstOrDefault(s => s.Id == sid) is { } script ? CatalogService.ToDto(script) : null,
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(a.CreateDate, DateTimeKind.Utc)),
            RequestedBy = a.RequestedByUserId is { } uid ? users.GetValueOrDefault(uid) : a.ProfileId is { } prid ? "Profil " + profiles.GetValueOrDefault(prid) : null,
            Revision = a.Revision,
        }).ToList();
        return new ActionsResponse(actions, latest);
    }

    /// <summary>POST start: pending → running. A cancelled (or unknown) action answers with an error (409 / 404).</summary>
    public static async Task<Outcome> StartAsync(AppDbContext db, DeviceIdentity device, long actionId, CancellationToken ct = default)
        => await Revisions.ChangeAsync(db, device.DeviceId, async () =>
        {
            var action = await db.Actions.FirstOrDefaultAsync(a => a.Id == actionId && a.DeviceId == device.DeviceId, ct);
            if (action is null)
            {
                return Outcome.Missing();
            }

            if (action.State != ActionState.Pending)
            {
                return Outcome.Fail(action.State == ActionState.Cancelled ? "Dieser Auftrag wurde abgebrochen." : "Dieser Auftrag läuft schon oder ist erledigt.");
            }

            action.State = ActionState.Running;
            action.StartedDate = DateTime.UtcNow;
            action.Revision = await Revisions.NextAsync(db, ct);
            await db.SaveChangesAsync(ct);
            return Outcome.Success();
        }, ct);

    /// <summary>POST finish: how the action ended on the device.</summary>
    public static async Task<Outcome> FinishAsync(AppDbContext db, DeviceIdentity device, long actionId, ActionResult result, CancellationToken ct = default)
        => await Revisions.ChangeAsync(db, device.DeviceId, async () =>
        {
            var action = await db.Actions.FirstOrDefaultAsync(a => a.Id == actionId && a.DeviceId == device.DeviceId, ct);
            if (action is null)
            {
                return Outcome.Missing();
            }

            if (action.State is ActionState.Succeeded or ActionState.Failed)
            {
                return Outcome.Fail("Dieser Auftrag ist schon erledigt.");
            }

            action.State = result.WasCancelled ? ActionState.Cancelled : result.Succeeded ? ActionState.Succeeded : ActionState.Failed;
            action.StartedDate ??= result.StartedAt.UtcDateTime;
            action.FinishedDate = DateTime.UtcNow;
            action.ExitCode = result.ExitCode;
            action.Output = Text.Tail(result.Output, ApiContract.MaxOutputLength);
            action.InstalledVersion = Text.Clean(result.InstalledVersion, 100);
            action.Revision = await Revisions.NextAsync(db, ct);
            await db.SaveChangesAsync(ct);
            return Outcome.Success();
        }, ct);

    /// <summary>POST report: something the device did on its own, kept in its history (finished, never sent back to it).</summary>
    public static async Task<Outcome<long>> ReportAsync(AppDbContext db, DeviceIdentity device, LocalRunReport report, CancellationToken ct = default)
    {
        if (report.Result is null || !Enum.IsDefined(report.Kind))
        {
            return Outcome<long>.Fail("Der Bericht ist unvollständig.");
        }

        var packageId = report.PackageId is { } pid && await db.Packages.AnyAsync(p => p.Id == pid, ct) ? pid : (long?)null;
        var scriptId = report.ScriptId is { } sid && await db.Scripts.AnyAsync(s => s.Id == sid, ct) ? sid : (long?)null;
        return await Revisions.ChangeAsync(db, device.DeviceId, async () =>
        {
            var action = new DeviceAction
            {
                DeviceId = device.DeviceId,
                Kind = report.Kind,
                Origin = report.Origin is ActionOrigin.Device or ActionOrigin.Bundle ? report.Origin : ActionOrigin.Device,
                State = report.Result.WasCancelled ? ActionState.Cancelled : report.Result.Succeeded ? ActionState.Succeeded : ActionState.Failed,
                PackageId = packageId,
                ScriptId = scriptId,
                Title = Text.Clean(report.Title, 200) ?? DefaultTitle(report.Kind, null),
                StartedDate = report.Result.StartedAt.UtcDateTime,
                FinishedDate = report.Result.FinishedAt.UtcDateTime,
                ExitCode = report.Result.ExitCode,
                Output = Text.Tail(report.Result.Output, ApiContract.MaxOutputLength),
                InstalledVersion = Text.Clean(report.Result.InstalledVersion, 100),
                Revision = await Revisions.NextAsync(db, ct),
            };
            db.Actions.Add(action);
            await db.SaveChangesAsync(ct);
            return Outcome<long>.Success(action.Id);
        }, ct);
    }

    // ---- what people do --------------------------------------------------------------------------------------------------------------------------

    /// <summary>"Aktion senden": a person sends an action to one of their devices (an administrator to every device). The device needs "Sync".</summary>
    public static async Task<Outcome<long>> PushAsync(AppDbContext db, Actor actor, long deviceId, PushRequest request, CancellationToken ct = default)
    {
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId && (actor.IsAdmin || d.UserId == actor.UserId), ct);
        if (device is null)
        {
            return Outcome<long>.Missing();
        }

        if (!device.SyncEnabled)
        {
            return Outcome<long>.Fail("Auf diesem Gerät ist Sync ausgeschaltet: es nimmt keine Aufträge aus der Cloud an. Sync lässt sich in ThixInstall auf dem Gerät einschalten.");
        }

        var check = await CheckAsync(db, device.Os, request, ct);
        if (!check.Ok)
        {
            return Outcome<long>.Fail(check.Error!, check.Field);
        }

        return await CreateAsync(db, device.Id, request, check.Value!, ActionOrigin.Push, actor.UserId, null, ct);
    }

    /// <summary>Sends an action to every device of a profile that has "Sync" and the right system (administrators); returns how many got it.</summary>
    public static async Task<Outcome<int>> PushToProfileAsync(AppDbContext db, Actor actor, long profileId, PushRequest request, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var devices = await db.Devices.AsNoTracking().Where(d => d.ProfileId == profileId && d.SyncEnabled).Select(d => new { d.Id, d.Os }).ToListAsync(ct);
        var sent = 0;
        string? lastError = null;
        foreach (var device in devices)
        {
            var check = await CheckAsync(db, device.Os, request, ct);
            if (!check.Ok)
            {
                lastError = check.Error;
                continue;
            }

            await CreateAsync(db, device.Id, request, check.Value!, ActionOrigin.Push, actor.UserId, null, ct);
            sent++;
        }

        return sent == 0 && lastError is not null ? Outcome<int>.Fail(lastError) : Outcome<int>.Success(sent);
    }

    /// <summary>
    /// The profile of a device becomes actions: an app of the profile that the device does not report as installed and has no open action for is
    /// installed; a script of the profile that has not run on the device by the profile yet runs. Only for devices with "Sync".
    /// </summary>
    public static async Task<int> ApplyProfileAsync(AppDbContext db, long deviceId, CancellationToken ct = default)
    {
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device?.ProfileId is not { } profileId || !device.SyncEnabled)
        {
            return 0;
        }

        var items = await db.ProfileItems.AsNoTracking().Where(i => i.ProfileId == profileId).OrderBy(i => i.SortOrder).ToListAsync(ct);
        var installed = await db.DeviceApps.AsNoTracking().Where(a => a.DeviceId == deviceId && a.PackageId != null).Select(a => a.PackageId!.Value).ToListAsync(ct);
        var earlier = await db.Actions.AsNoTracking()
            .Where(a => a.DeviceId == deviceId && (a.State == ActionState.Pending || a.State == ActionState.Running || (a.Origin == ActionOrigin.Profile && a.State == ActionState.Succeeded)))
            .Select(a => new { a.PackageId, a.ScriptId }).ToListAsync(ct);
        var created = 0;
        foreach (var item in items)
        {
            if (item.PackageId is { } packageId && (installed.Contains(packageId) || earlier.Any(e => e.PackageId == packageId)))
            {
                continue;
            }

            if (item.ScriptId is { } scriptId && earlier.Any(e => e.ScriptId == scriptId))
            {
                continue;
            }

            var request = new PushRequest(item.PackageId is not null ? ActionKind.Install : ActionKind.RunScript, item.PackageId, item.ScriptId);
            var check = await CheckAsync(db, device.Os, request, ct);
            if (!check.Ok)
            {
                continue; // an item of the other system
            }

            await CreateAsync(db, deviceId, request, check.Value!, ActionOrigin.Profile, null, profileId, ct);
            created++;
        }

        return created;
    }

    /// <summary>Whether the action fits the system of the device, and its title.</summary>
    private static async Task<Outcome<string>> CheckAsync(AppDbContext db, TargetOs os, PushRequest request, CancellationToken ct)
    {
        switch (request.Kind)
        {
            case ActionKind.Install or ActionKind.Update or ActionKind.Uninstall:
                var package = request.PackageId is { } pid ? await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid, ct) : null;
                if (package is null)
                {
                    return Outcome<string>.Fail("Bitte eine App wählen.", "PackageId");
                }

                if ((os == TargetOs.Windows ? package.WindowsMethod : package.MacMethod) is null)
                {
                    return Outcome<string>.Fail($"{package.Name} gibt es nicht für {CatalogService.OsName(os)}.", "PackageId");
                }

                return Outcome<string>.Success(DefaultTitle(request.Kind, package.Name));
            case ActionKind.RunScript:
                var script = request.ScriptId is { } sid ? await db.Scripts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, ct) : null;
                if (script is null)
                {
                    return Outcome<string>.Fail("Bitte ein Skript wählen.", "ScriptId");
                }

                if (script.Os != os)
                {
                    return Outcome<string>.Fail($"„{script.Name}“ ist ein Skript für {CatalogService.OsName(script.Os)}.", "ScriptId");
                }

                return Outcome<string>.Success(DefaultTitle(request.Kind, script.Name));
            case ActionKind.UpdateAll or ActionKind.Refresh:
                return Outcome<string>.Success(DefaultTitle(request.Kind, null));
            default:
                return Outcome<string>.Fail("Diese Art von Auftrag gibt es nicht.", "Kind");
        }
    }

    private static async Task<Outcome<long>> CreateAsync(AppDbContext db, long deviceId, PushRequest request, string title, ActionOrigin origin, long? userId, long? profileId, CancellationToken ct)
        => await Revisions.ChangeAsync(db, deviceId, async () =>
        {
            var action = new DeviceAction
            {
                DeviceId = deviceId,
                Kind = request.Kind,
                Origin = origin,
                State = ActionState.Pending,
                PackageId = request.Kind is ActionKind.Install or ActionKind.Update or ActionKind.Uninstall ? request.PackageId : null,
                ScriptId = request.Kind == ActionKind.RunScript ? request.ScriptId : null,
                ProfileId = profileId,
                Title = title,
                RequestedByUserId = userId,
                Revision = await Revisions.NextAsync(db, ct),
            };
            db.Actions.Add(action);
            await db.SaveChangesAsync(ct);
            return Outcome<long>.Success(action.Id);
        }, ct);

    public static string DefaultTitle(ActionKind kind, string? name) => kind switch
    {
        ActionKind.Install => $"{name} installieren",
        ActionKind.Update => $"{name} aktualisieren",
        ActionKind.Uninstall => $"{name} deinstallieren",
        ActionKind.RunScript => $"Skript „{name}“ ausführen",
        ActionKind.UpdateAll => "Alle Apps aktualisieren",
        ActionKind.Refresh => "Inventar neu melden",
        _ => kind.ToString(),
    };

    /// <summary>Cancels a pending action (a running one can only be ended on the device).</summary>
    public static async Task<Outcome> CancelAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        var action = await db.Actions.AsNoTracking().Include(a => a.Device).FirstOrDefaultAsync(a => a.Id == id && (actor.IsAdmin || a.Device!.UserId == actor.UserId), ct);
        if (action is null)
        {
            return Outcome.Missing();
        }

        return await Revisions.ChangeAsync(db, action.DeviceId, async () =>
        {
            var row = await db.Actions.FirstAsync(a => a.Id == id, ct);
            if (row.State != ActionState.Pending)
            {
                return Outcome.Fail("Nur ein wartender Auftrag lässt sich abbrechen.");
            }

            row.State = ActionState.Cancelled;
            row.FinishedDate = DateTime.UtcNow;
            row.Revision = await Revisions.NextAsync(db, ct);
            await db.SaveChangesAsync(ct);
            return Outcome.Success();
        }, ct);
    }

    public static async Task<(IReadOnlyList<ActionListItem> Items, int Total, int Matching)> ListAsync(AppDbContext db, Actor actor, ActionQuery query, CancellationToken ct = default)
    {
        var all = db.Actions.AsNoTracking().Where(a => actor.IsAdmin || a.Device!.UserId == actor.UserId);
        if (query.DeviceId is { } deviceId)
        {
            all = all.Where(a => a.DeviceId == deviceId);
        }

        var total = await all.CountAsync(ct);
        var filtered = all;
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = "%" + UserService.EscapeLike(query.Search.Trim()) + "%";
            filtered = filtered.Where(a => EF.Functions.ILike(a.Title, pattern, "\\") || EF.Functions.ILike(a.Device!.Name, pattern, "\\"));
        }

        filtered = query.State switch
        {
            "open" => filtered.Where(a => a.State == ActionState.Pending || a.State == ActionState.Running),
            "succeeded" => filtered.Where(a => a.State == ActionState.Succeeded),
            "failed" => filtered.Where(a => a.State == ActionState.Failed),
            "cancelled" => filtered.Where(a => a.State == ActionState.Cancelled),
            _ => filtered,
        };
        filtered = query.Origin switch
        {
            "push" => filtered.Where(a => a.Origin == ActionOrigin.Push),
            "profile" => filtered.Where(a => a.Origin == ActionOrigin.Profile),
            "device" => filtered.Where(a => a.Origin == ActionOrigin.Device || a.Origin == ActionOrigin.Bundle),
            _ => filtered,
        };
        var matching = await filtered.CountAsync(ct);
        filtered = (query.Sort, query.Descending) switch
        {
            ("device", false) => filtered.OrderBy(a => a.Device!.Name).ThenByDescending(a => a.Id),
            ("device", true) => filtered.OrderByDescending(a => a.Device!.Name).ThenByDescending(a => a.Id),
            (_, false) => filtered.OrderBy(a => a.Id),
            _ => filtered.OrderByDescending(a => a.Id),
        };
        var rows = await filtered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(a => new { a.Id, a.DeviceId, Device = a.Device!.Name, a.Device.Os, a.Kind, a.State, a.Origin, a.Title, a.RequestedByUserId, a.ProfileId, a.CreateDate, a.FinishedDate, a.ExitCode })
            .ToListAsync(ct);
        var users = await UserNamesAsync(db, rows.Select(r => r.RequestedByUserId), ct);
        var items = rows.Select(r => new ActionListItem(r.Id, r.DeviceId, r.Device, r.Os, r.Kind, r.State, r.Origin, r.Title,
            r.RequestedByUserId is { } uid ? users.GetValueOrDefault(uid) : null, r.CreateDate, r.FinishedDate, r.ExitCode)).ToList();
        return (items, total, matching);
    }

    public static async Task<Outcome<ActionView>> GetAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        var a = await db.Actions.AsNoTracking().Where(x => x.Id == id && (actor.IsAdmin || x.Device!.UserId == actor.UserId))
            .Select(x => new
            {
                Action = x, Device = x.Device!.Name, x.Device.Os, Package = x.Package != null ? x.Package.Name : null, Script = x.Script != null ? x.Script.Name : null,
                Profile = db.Profiles.IgnoreQueryFilters().Where(p => p.Id == x.ProfileId).Select(p => p.Name).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);
        if (a is null)
        {
            return Outcome<ActionView>.Missing();
        }

        var users = await UserNamesAsync(db, [a.Action.RequestedByUserId], ct);
        var x = a.Action;
        return Outcome<ActionView>.Success(new ActionView(x.Id, x.DeviceId, a.Device, a.Os, x.Kind, x.State, x.Origin, x.Title, x.PackageId, a.Package, x.ScriptId, a.Script,
            x.RequestedByUserId is { } uid ? users.GetValueOrDefault(uid) : null, a.Profile, x.CreateDate, x.StartedDate, x.FinishedDate, x.ExitCode, x.Output, x.InstalledVersion));
    }

    private static async Task<Dictionary<long, string>> UserNamesAsync(AppDbContext db, IEnumerable<long?> ids, CancellationToken ct)
    {
        var wanted = ids.OfType<long>().Distinct().ToList();
        return wanted.Count == 0
            ? []
            : await db.Users.AsNoTracking().IgnoreQueryFilters().Where(u => wanted.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    public static string StateName(ActionState state) => state switch
    {
        ActionState.Pending => "Wartet",
        ActionState.Running => "Läuft",
        ActionState.Succeeded => "Erfolgreich",
        ActionState.Failed => "Fehlgeschlagen",
        ActionState.Cancelled => "Abgebrochen",
        _ => state.ToString(),
    };

    public static string StatePill(ActionState state) => state switch
    {
        ActionState.Pending => "mat-pill--info",
        ActionState.Running => "mat-pill--busy",
        ActionState.Succeeded => "mat-pill--ok",
        ActionState.Failed => "mat-pill--error",
        _ => "mat-pill--plain",
    };

    public static string OriginName(ActionOrigin origin) => origin switch
    {
        ActionOrigin.Device => "Am Gerät",
        ActionOrigin.Push => "Aus der Cloud",
        ActionOrigin.Profile => "Profil",
        ActionOrigin.Bundle => "Paket",
        _ => origin.ToString(),
    };

    public static string KindName(ActionKind kind) => kind switch
    {
        ActionKind.Install => "Installieren",
        ActionKind.Update => "Aktualisieren",
        ActionKind.Uninstall => "Deinstallieren",
        ActionKind.RunScript => "Skript ausführen",
        ActionKind.UpdateAll => "Alles aktualisieren",
        ActionKind.Refresh => "Inventar melden",
        _ => kind.ToString(),
    };
}
