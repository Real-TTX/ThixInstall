using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using ThixInstall.Cloud.Data;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Business;

/// <summary>
/// What the start page posts. <see cref="Choices"/>: the install options picked for Thix apps, by package id (SETUP-08; an app without options
/// ignores them, what is not given takes the default of the app).
/// </summary>
public sealed record BundleRequest(TargetOs Os, IReadOnlyList<long> PackageIds, IReadOnlyList<long> ScriptIds, string? Name, bool IncludesAgent, long? ProfileId,
    IReadOnlyDictionary<long, InstallChoices>? Choices = null);

/// <summary>A bundle as its page shows it.</summary>
public sealed record BundleView(long Id, string Token, string Name, TargetOs Os, bool IncludesAgent, string? ProfileName, string? OwnerName, long? OwnerUserId,
    IReadOnlyList<PickerItem> Packages, IReadOnlyList<PickerItem> Scripts, int DownloadCount, DateTime CreateDate, DateTime? LastDownloadDate, bool CanEnroll);

/// <summary>A bundle in the list.</summary>
public sealed record BundleListItem(long Id, string Token, string Name, TargetOs Os, bool IncludesAgent, string? ProfileName, string? OwnerName, int Items,
    int DownloadCount, DateTime CreateDate, DateTime? LastDownloadDate);

/// <summary>The program of a bundle to send: a file and what is appended to it, or a file built for this bundle.</summary>
public sealed record BundleDownload(string Path, string FileName, string ContentType, byte[]? Trailer, bool DeleteAfterSend = false);

/// <summary>
/// The bundles: what somebody picked on the start page, kept under a secret token. Anybody may create one from the public catalog; a signed-in person
/// may add every item, ThixInstall itself (then the bundle enrolls the devices it installs, for <see cref="ApiContract.BundleEnrollmentDays"/> days) and a
/// profile. The download is the ThixInstall program with a <see cref="BundleTicket"/>: appended to the Windows setup, as a file next to the Mac app.
/// </summary>
public static class BundleService
{
    public const int MaxItems = 100;

    public static async Task<Outcome<string>> CreateAsync(AppDbContext db, Actor? actor, BundleRequest request, CancellationToken ct = default)
    {
        var anonymous = actor is null;
        if (anonymous && !await CatalogService.IsPublicAsync(db, ct))
        {
            return Outcome<string>.Fail("Der Katalog ist nur nach der Anmeldung verfügbar.");
        }

        var packageIds = request.PackageIds.Distinct().ToList();
        var scriptIds = request.ScriptIds.Distinct().ToList();
        if (packageIds.Count + scriptIds.Count == 0 && !request.IncludesAgent)
        {
            return Outcome<string>.Fail("Bitte mindestens eine App oder ein Skript auswählen.");
        }

        if (packageIds.Count + scriptIds.Count > MaxItems)
        {
            return Outcome<string>.Fail($"Ein Paket hat höchstens {MaxItems} Einträge.");
        }

        if (anonymous && (request.IncludesAgent || request.ProfileId is not null))
        {
            return Outcome<string>.Fail("ThixInstall mitinstallieren und Profile gibt es nur mit einem Konto.");
        }

        var architecture = request.Os == TargetOs.MacOS ? CpuArchitecture.Arm64 : CpuArchitecture.X64;
        var packages = await CatalogService.PackagesForAsync(db, request.Os, architecture, anonymous, packageIds, ct);
        if (packages.Count != packageIds.Count)
        {
            return Outcome<string>.Fail("Eine der Apps gibt es für dieses System nicht (mehr).");
        }

        var scriptCount = await db.Scripts.CountAsync(s => scriptIds.Contains(s.Id) && s.Os == request.Os && (!anonymous || s.IsPublic), ct);
        if (scriptCount != scriptIds.Count)
        {
            return Outcome<string>.Fail("Eines der Skripte gibt es für dieses System nicht (mehr).");
        }

        if (request.ProfileId is { } profileId && !await db.Profiles.AnyAsync(p => p.Id == profileId, ct))
        {
            return Outcome<string>.Fail("Dieses Profil gibt es nicht (mehr).", "ProfileId");
        }

        var count = packageIds.Count + scriptIds.Count;
        var name = Text.Clean(request.Name, 120) ?? (count == 0 ? "ThixInstall" : count == 1 ? "1 App" : $"{count} Apps und Skripte");
        if (scriptIds.Count == 0 && count > 1)
        {
            name = Text.Clean(request.Name, 120) ?? $"{count} Apps";
        }

        var bundle = new Bundle
        {
            Token = Guid.NewGuid(),
            Name = name,
            Os = request.Os,
            IncludesAgent = request.IncludesAgent && !anonymous,
            ProfileId = anonymous ? null : request.ProfileId,
            OwnerUserId = actor?.UserId,
        };
        db.Bundles.Add(bundle);
        await db.SaveChangesAsync(ct);
        var order = 0;
        foreach (var id in packageIds)
        {
            db.BundleItems.Add(new BundleItem { BundleId = bundle.Id, PackageId = id, SortOrder = order++, InstallChoices = ChoicesJson(packages.First(p => p.Id == id), request.Choices) });
        }

        foreach (var id in scriptIds)
        {
            db.BundleItems.Add(new BundleItem { BundleId = bundle.Id, ScriptId = id, SortOrder = order++ });
        }

        await db.SaveChangesAsync(ct);
        return Outcome<string>.Success(bundle.Token.ToString("N"));
    }

    public static Guid? ParseToken(string? token) => Guid.TryParseExact(token, "N", out var guid) ? guid : null;

    /// <summary>GET /api/v1/bundles/{token}: the bundle for the program that installs it.</summary>
    public static async Task<BundleDto?> DtoAsync(AppDbContext db, string token, CpuArchitecture architecture, CancellationToken ct = default)
    {
        if (ParseToken(token) is not { } guid)
        {
            return null;
        }

        var bundle = await db.Bundles.AsNoTracking().Include(b => b.Profile).FirstOrDefaultAsync(b => b.Token == guid, ct);
        if (bundle is null)
        {
            return null;
        }

        var items = await db.BundleItems.AsNoTracking().Where(i => i.BundleId == bundle.Id).OrderBy(i => i.SortOrder).ToListAsync(ct);
        var packageIds = items.Where(i => i.PackageId != null).Select(i => i.PackageId!.Value).ToList();
        var scriptIds = items.Where(i => i.ScriptId != null).Select(i => i.ScriptId!.Value).ToList();
        var packages = await CatalogService.PackagesForAsync(db, bundle.Os, architecture, false, packageIds, ct);
        var scripts = await db.Scripts.AsNoTracking().Where(s => scriptIds.Contains(s.Id)).ToListAsync(ct);
        return new BundleDto(
            bundle.Token.ToString("N"),
            bundle.Name,
            bundle.Os,
            bundle.IncludesAgent,
            CanEnroll(bundle),
            bundle.Profile?.Name,
            packageIds.Select(id => packages.FirstOrDefault(p => p.Id == id)).OfType<PackageDto>().Select(p => WithChoices(p, items.First(i => i.PackageId == p.Id))).ToList(),
            scriptIds.Select(id => scripts.FirstOrDefault(s => s.Id == id)).OfType<Script>().Select(CatalogService.ToDto).ToList(),
            new DateTimeOffset(DateTime.SpecifyKind(bundle.CreateDate, DateTimeKind.Utc)));
    }

    /// <summary>
    /// The install options of a Thix app in a bundle, as stored: only what the app offers, the default where nothing was picked (<see cref="ThixAppSetup.Effective"/>).
    /// Null for every other app.
    /// </summary>
    public static string? ChoicesJson(PackageDto package, IReadOnlyDictionary<long, InstallChoices>? choices)
    {
        if (package.Installer is not { IsEmpty: false } config || choices?.GetValueOrDefault(package.Id) is not { } picked)
        {
            return null;
        }

        return System.Text.Json.JsonSerializer.Serialize(ThixAppSetup.Effective(config, picked), ApiContract.Json);
    }

    /// <summary>The plan of a Thix app carries the options picked for it (the device passes them to the setup).</summary>
    private static PackageDto WithChoices(PackageDto package, BundleItem item)
    {
        var choices = ParseChoices(item.InstallChoices);
        return choices is null || package.Install is null || package.Installer is null ? package : package with { Install = package.Install with { Choices = choices } };
    }

    public static InstallChoices? ParseChoices(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<InstallChoices>(json, ApiContract.Json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The picked options in words for the page of a bundle: "Desktop-Verknüpfung · Autostart · ohne automatische Updates".</summary>
    public static string? Describe(InstallChoices? choices)
    {
        if (choices is null)
        {
            return null;
        }

        var parts = new List<string>();
        if (choices.DesktopShortcut is { } shortcut)
        {
            parts.Add(shortcut ? "Desktop-Verknüpfung" : "ohne Desktop-Verknüpfung");
        }

        if (choices.Autostart is { } autostart)
        {
            parts.Add(autostart ? "Autostart" : "ohne Autostart");
        }

        if (choices.AutomaticUpdates is { } updates)
        {
            parts.Add(updates ? "automatische Updates" : "ohne automatische Updates");
        }

        if (choices.FileTypes is { } types)
        {
            parts.Add(types.Count == 0 ? "keine Dateitypen" : "öffnet " + string.Join(", ", types));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>A bundle that installs ThixInstall enrolls devices for its owner while it is young enough.</summary>
    public static bool CanEnroll(Bundle bundle)
        => bundle.IncludesAgent && bundle.OwnerUserId is not null && DateTime.UtcNow - bundle.CreateDate < TimeSpan.FromDays(ApiContract.BundleEnrollmentDays);

    /// <summary>The page of a bundle: by its token (anybody who has the link).</summary>
    public static async Task<BundleView?> ViewAsync(AppDbContext db, string token, CancellationToken ct = default)
    {
        if (ParseToken(token) is not { } guid)
        {
            return null;
        }

        var bundle = await db.Bundles.AsNoTracking().Include(b => b.Profile).Include(b => b.Owner).FirstOrDefaultAsync(b => b.Token == guid, ct);
        return bundle is null ? null : await ViewOfAsync(db, bundle, ct);
    }

    /// <summary>The page of a bundle in the signed-in area: the owner's own, every one for an administrator.</summary>
    public static async Task<Outcome<BundleView>> GetAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        var bundle = await db.Bundles.AsNoTracking().Include(b => b.Profile).Include(b => b.Owner)
            .FirstOrDefaultAsync(b => b.Id == id && (actor.IsAdmin || b.OwnerUserId == actor.UserId), ct);
        return bundle is null ? Outcome<BundleView>.Missing() : Outcome<BundleView>.Success(await ViewOfAsync(db, bundle, ct));
    }

    private static async Task<BundleView> ViewOfAsync(AppDbContext db, Bundle bundle, CancellationToken ct)
    {
        var items = await db.BundleItems.AsNoTracking().Where(i => i.BundleId == bundle.Id).OrderBy(i => i.SortOrder)
            .Select(i => new
            {
                i.PackageId, i.ScriptId, i.InstallChoices,
                PackageName = i.Package != null ? i.Package.Name : null, PackageKey = i.Package != null ? i.Package.Key : null,
                PackageCategory = i.Package != null ? i.Package.Category : null, PackageDescription = i.Package != null ? i.Package.Description : null,
                HasIcon = i.PackageId != null && db.PackageIcons.Any(x => x.PackageId == i.PackageId),
                ScriptName = i.Script != null ? i.Script.Name : null, ScriptKey = i.Script != null ? i.Script.Key : null,
                ScriptCategory = i.Script != null ? i.Script.Category : null, ScriptDescription = i.Script != null ? i.Script.Description : null,
            })
            .ToListAsync(ct);
        var packages = items.Where(i => i.PackageId != null && i.PackageName != null)
            .Select(i => new PickerItem(i.PackageId!.Value, i.PackageKey!, i.PackageName!, i.PackageDescription, i.PackageCategory!, i.HasIcon, false, null, false,
                Describe(ParseChoices(i.InstallChoices)))).ToList();
        var scripts = items.Where(i => i.ScriptId != null && i.ScriptName != null)
            .Select(i => new PickerItem(i.ScriptId!.Value, i.ScriptKey!, i.ScriptName!, i.ScriptDescription, i.ScriptCategory!, false, false, null, true, null)).ToList();
        return new BundleView(bundle.Id, bundle.Token.ToString("N"), bundle.Name, bundle.Os, bundle.IncludesAgent, bundle.Profile?.Name, bundle.Owner?.DisplayName,
            bundle.OwnerUserId, packages, scripts, bundle.DownloadCount, bundle.CreateDate, bundle.LastDownloadDate, CanEnroll(bundle));
    }

    public static async Task<(IReadOnlyList<BundleListItem> Items, int Total, int Matching)> ListAsync(AppDbContext db, Actor actor, string? search, string? os, string? scope,
        string sort, bool descending, int page, int pageSize, CancellationToken ct = default)
    {
        // a person sees their own bundles; an administrator also every one ("all") or the anonymous ones of the start page
        var all = (actor.IsAdmin ? scope : null) switch
        {
            "all" => db.Bundles.AsNoTracking(),
            "anonymous" => db.Bundles.AsNoTracking().Where(b => b.OwnerUserId == null),
            _ => db.Bundles.AsNoTracking().Where(b => b.OwnerUserId == actor.UserId),
        };

        var total = await all.CountAsync(ct);
        var filtered = all;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + UserService.EscapeLike(search.Trim()) + "%";
            filtered = filtered.Where(b => EF.Functions.ILike(b.Name, pattern, "\\"));
        }

        filtered = os switch
        {
            "windows" => filtered.Where(b => b.Os == TargetOs.Windows),
            "macos" => filtered.Where(b => b.Os == TargetOs.MacOS),
            _ => filtered,
        };
        var matching = await filtered.CountAsync(ct);
        filtered = (sort, descending) switch
        {
            ("name", false) => filtered.OrderBy(b => b.Name),
            ("name", true) => filtered.OrderByDescending(b => b.Name),
            ("downloads", false) => filtered.OrderBy(b => b.DownloadCount),
            ("downloads", true) => filtered.OrderByDescending(b => b.DownloadCount),
            (_, false) => filtered.OrderBy(b => b.CreateDate),
            _ => filtered.OrderByDescending(b => b.CreateDate),
        };
        var items = await filtered.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(b => new BundleListItem(b.Id, b.Token.ToString(), b.Name, b.Os, b.IncludesAgent, b.Profile != null ? b.Profile.Name : null,
                b.Owner != null ? b.Owner.DisplayName : null, db.BundleItems.Count(i => i.BundleId == b.Id), b.DownloadCount, b.CreateDate, b.LastDownloadDate))
            .ToListAsync(ct);
        return (items.Select(i => i with { Token = Guid.Parse(i.Token).ToString("N") }).ToList(), total, matching);
    }

    /// <summary>Anonymous bundles that nobody downloaded (or used) for 90 days go (the housekeeping).</summary>
    public static async Task<int> CleanUpAsync(AppDbContext db, CancellationToken ct = default)
    {
        var limit = DateTime.UtcNow.AddDays(-90);
        return await db.Bundles.Where(b => b.OwnerUserId == null && (b.LastDownloadDate ?? b.CreateDate) < limit).ExecuteDeleteAsync(ct);
    }

    public static async Task<Outcome> DeleteAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        var bundle = await db.Bundles.FirstOrDefaultAsync(b => b.Id == id && (actor.IsAdmin || b.OwnerUserId == actor.UserId), ct);
        if (bundle is null)
        {
            return Outcome.Missing();
        }

        db.Bundles.Remove(bundle);
        await db.SaveChangesAsync(ct);
        return Outcome.Success();
    }

    /// <summary>
    /// The program of a bundle for a platform: the Windows setup with the ticket appended, or a zip of the Mac app with the ticket next to it.
    /// <paramref name="cloudUrl"/> is the address the browser used (the program comes back to it).
    /// </summary>
    public static async Task<Outcome<BundleDownload>> DownloadAsync(AppDbContext db, string token, string platformKey, string cloudUrl, CancellationToken ct = default)
    {
        if (ParseToken(token) is not { } guid)
        {
            return Outcome<BundleDownload>.Missing();
        }

        var bundle = await db.Bundles.FirstOrDefaultAsync(b => b.Token == guid, ct);
        if (bundle is null)
        {
            return Outcome<BundleDownload>.Missing();
        }

        if (!AgentService.IsPlatform(platformKey) || (bundle.Os == TargetOs.Windows) != platformKey.StartsWith("win-", StringComparison.Ordinal))
        {
            return Outcome<BundleDownload>.Fail("Dieses Paket ist für ein anderes System.");
        }

        var agent = await AgentService.PackageAsync(db, platformKey, ct);
        if (!agent.Ok)
        {
            return Outcome<BundleDownload>.Fail(agent.Error ?? "Das ThixInstall-Programm ist gerade nicht verfügbar.");
        }

        // the program carries the whole configuration: exactly what GET /api/v1/bundles/{token}?arch= answers for this platform
        var architecture = platformKey.EndsWith("arm64", StringComparison.Ordinal) ? CpuArchitecture.Arm64 : CpuArchitecture.X64;
        var dto = await DtoAsync(db, bundle.Token.ToString("N"), architecture, ct);
        var ticket = new BundleTicket(cloudUrl.TrimEnd('/'), bundle.Token.ToString("N"), dto);
        var slug = Text.Slug(bundle.Name, 40);
        var baseName = "ThixInstall" + (slug.Length == 0 ? string.Empty : "-" + slug);
        bundle.DownloadCount++;
        bundle.LastDownloadDate = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        if (platformKey.StartsWith("win-", StringComparison.Ordinal))
        {
            return Outcome<BundleDownload>.Success(new BundleDownload(agent.Value!.Path, baseName + ".exe", "application/vnd.microsoft.portable-executable", ticket.ToTrailer()));
        }

        var zip = Path.Combine(Path.GetTempPath(), "thixinstall-bundle-" + Guid.NewGuid().ToString("N") + ".zip");
        await Task.Run(() => BuildMacZip(agent.Value!.Path, zip, baseName + BundleTicket.FileExtension, ticket), ct);
        return Outcome<BundleDownload>.Success(new BundleDownload(zip, $"{baseName}-{platformKey.Replace("osx-", "macOS-", StringComparison.Ordinal)}.zip", "application/zip", null, DeleteAfterSend: true));
    }

    /// <summary>
    /// A zip with every entry of the app's zip (their Unix permissions kept: the program stays executable) and the ticket file next to the app.
    /// </summary>
    public static void BuildMacZip(string appZip, string target, string ticketFileName, BundleTicket ticket)
    {
        using var source = ZipFile.OpenRead(appZip);
        using var output = ZipFile.Open(target, ZipArchiveMode.Create);
        foreach (var entry in source.Entries)
        {
            var copy = output.CreateEntry(entry.FullName, CompressionLevel.Fastest);
            copy.ExternalAttributes = entry.ExternalAttributes;
            copy.LastWriteTime = entry.LastWriteTime;
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }

            using var from = entry.Open();
            using var to = copy.Open();
            from.CopyTo(to);
        }

        var ticketEntry = output.CreateEntry(ticketFileName, CompressionLevel.Fastest);
        ticketEntry.ExternalAttributes = Convert.ToInt32("100644", 8) << 16;
        using (var writer = new StreamWriter(ticketEntry.Open(), new System.Text.UTF8Encoding(false)))
        {
            writer.Write(ticket.ToJson());
        }
    }
}
