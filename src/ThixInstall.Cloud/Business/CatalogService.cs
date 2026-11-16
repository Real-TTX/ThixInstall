using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ThixInstall.Cloud.Data;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Business;

/// <summary>A package in the list of the catalog.</summary>
public sealed record PackageListItem(long Id, string Key, string Name, string? Publisher, string Category, bool IsPublic, bool IsFeatured, bool HasIcon,
    InstallMethod? WindowsMethod, InstallMethod? MacMethod, string? WindowsVersion, string? MacVersion, bool HasError, DateTime? ResolvedDate);

/// <summary>The query of the list of packages.</summary>
public sealed record PackageQuery(string? Search, string? Category, string? Os, string? Visibility, string Sort, bool Descending, int Page, int PageSize);

/// <summary>Everything the edit page of a package shows and posts.</summary>
public sealed record PackageInput
{
    public long? Id { get; init; }
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Publisher { get; init; }
    public string? Description { get; init; }
    public string Category { get; init; } = string.Empty;
    public string? Homepage { get; init; }
    public bool IsPublic { get; init; } = true;
    public bool IsFeatured { get; init; }
    public int SortOrder { get; init; }
    public InstallMethod? WindowsMethod { get; init; }
    public string? WingetId { get; init; }
    public string? ChocoId { get; init; }
    public string? StoreId { get; init; }
    public string? StorePackageFamilyName { get; init; }
    public string? DirectUrl { get; init; }
    public string? DirectSilentArguments { get; init; }
    public string? DirectSha256 { get; init; }
    public string? DirectVersion { get; init; }
    /// <summary>The name of the uploaded Windows installer (shown; the file itself comes as <see cref="UploadFile"/>).</summary>
    public string? WindowsUploadFileName { get; init; }
    public string? DetectDisplayName { get; init; }
    public InstallMethod? MacMethod { get; init; }
    public string? BrewName { get; init; }
    public string? MacDirectUrl { get; init; }
    public string? MacDirectSha256 { get; init; }
    public string? MacDirectVersion { get; init; }
    public string? MacUploadFileName { get; init; }
    public string? MacAppBundleName { get; init; }
    public bool HasIcon { get; init; }
    public DateTime? ResolvedDate { get; init; }
}

/// <summary>A script in the list of the catalog.</summary>
public sealed record ScriptListItem(long Id, string Key, string Name, string Category, TargetOs Os, ScriptShell Shell, ScriptContext Context, bool IsPublic, int Lines, DateTime UpdateDate);

public sealed record ScriptQuery(string? Search, string? Os, string? Context, string Sort, bool Descending, int Page, int PageSize);

/// <summary>Everything the edit page of a script shows and posts.</summary>
public sealed record ScriptInput
{
    public long? Id { get; init; }
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Category { get; init; } = string.Empty;
    public TargetOs Os { get; init; } = TargetOs.Windows;
    public ScriptShell Shell { get; init; } = ScriptShell.PowerShell;
    public ScriptContext Context { get; init; } = ScriptContext.System;
    public string Content { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; }
    public bool IsPublic { get; init; } = true;
    public int SortOrder { get; init; }
}

/// <summary>An app or a script on the start page.</summary>
public sealed record PickerItem(long Id, string Key, string Name, string? Description, string Category, bool HasIcon, bool IsFeatured, string? Version, bool IsScript, string? Note);

/// <summary>
/// The catalog: the apps (<see cref="Package"/>) and scripts (<see cref="Script"/>) that devices install and run. Administrators keep it; everybody
/// may read it (anonymous visitors only what is public, and only while the setting allows it). It also builds what the API hands to devices.
/// </summary>
public static class CatalogService
{
    public const int MaxIconBytes = 512 * 1024;

    public static readonly IReadOnlyList<string> DefaultCategories =
        ["Browser", "Kommunikation", "Office", "Medien", "Bilder", "Entwicklung", "Werkzeuge", "Sicherheit", "Cloud-Speicher", "Spiele", "Laufzeitumgebungen", "System"];

    // ---- the catalog for devices and the start page -----------------------------------------------------------------------------------------------

    /// <summary>GET /api/v1/catalog: the packages with a source for the system and the scripts of the system. Anonymous (<paramref name="actor"/> null): public ones only.</summary>
    public static async Task<CatalogResponse> CatalogAsync(AppDbContext db, Actor? actor, TargetOs os, CpuArchitecture architecture, CancellationToken ct = default)
    {
        var anonymous = actor is null;
        var packages = await PackagesForAsync(db, os, architecture, anonymous, null, ct);
        var scripts = await db.Scripts.AsNoTracking().Where(s => s.Os == os && (!anonymous || s.IsPublic))
            .OrderBy(s => s.Category).ThenBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync(ct);
        return new CatalogResponse(os, architecture, packages, scripts.Select(ToDto).ToList(), AppInfo.Version, DateTimeOffset.UtcNow);
    }

    /// <summary>The packages (as devices see them) that have a source for the system; <paramref name="only"/> limits them to some ids.</summary>
    public static async Task<List<PackageDto>> PackagesForAsync(AppDbContext db, TargetOs os, CpuArchitecture architecture, bool publicOnly, IReadOnlyCollection<long>? only, CancellationToken ct = default)
    {
        var query = db.Packages.AsNoTracking().Where(p => !publicOnly || p.IsPublic);
        query = os == TargetOs.Windows ? query.Where(p => p.WindowsMethod != null) : query.Where(p => p.MacMethod != null);
        if (only is not null)
        {
            query = query.Where(p => only.Contains(p.Id));
        }

        var rows = await query.OrderByDescending(p => p.IsFeatured).ThenBy(p => p.Category).ThenBy(p => p.SortOrder).ThenBy(p => p.Name)
            .Select(p => new { Package = p, HasIcon = db.PackageIcons.Any(i => i.PackageId == p.Id) })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.Package.Id).ToList();
        var releases = await db.Releases.AsNoTracking().Where(r => ids.Contains(r.PackageId) && r.Os == os && r.Architecture == architecture).ToListAsync(ct);
        return rows.Select(r => ToDto(r.Package, r.HasIcon, releases.FirstOrDefault(x => x.PackageId == r.Package.Id), os))
            .Where(p => p.Install is not null)
            .ToList();
    }

    public static PackageDto ToDto(Package p, bool hasIcon, PackageRelease? release, TargetOs os) => new()
    {
        Id = p.Id,
        Key = p.Key,
        Name = p.Name,
        Publisher = p.Publisher,
        Description = p.Description,
        Category = p.Category,
        Homepage = p.Homepage,
        HasIcon = hasIcon,
        IsFeatured = p.IsFeatured,
        IsPublic = p.IsPublic,
        LatestVersion = release?.Method == (os == TargetOs.Windows ? p.WindowsMethod : p.MacMethod) ? release?.Version : null,
        Install = ReleaseService.Plan(p, release, os),
        Detection = os == TargetOs.Windows
            ? new DetectionRule
            {
                DisplayName = string.IsNullOrWhiteSpace(p.DetectDisplayName) ? p.Name : p.DetectDisplayName,
                ProductCode = release?.ProductCode,
                PackageFamilyName = p.WindowsMethod == InstallMethod.MicrosoftStore ? p.StorePackageFamilyName : null,
            }
            : new DetectionRule
            {
                AppBundleName = p.MacAppBundleName ?? release?.AppBundleName,
                BrewName = p.MacMethod == InstallMethod.Homebrew ? p.BrewName : null,
            },
    };

    public static ScriptDto ToDto(Script s) => new()
    {
        Id = s.Id,
        Key = s.Key,
        Name = s.Name,
        Description = s.Description,
        Category = s.Category,
        Os = s.Os,
        Shell = s.Shell,
        Context = s.Context,
        Content = s.Content,
        TimeoutSeconds = s.TimeoutSeconds,
        IsPublic = s.IsPublic,
        ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Content))).ToLowerInvariant(),
    };

    /// <summary>The start page: the apps and scripts of a system, as tiles. Anonymous: public ones only.</summary>
    public static async Task<(IReadOnlyList<PickerItem> Packages, IReadOnlyList<PickerItem> Scripts)> PickerAsync(AppDbContext db, Actor? actor, TargetOs os, CancellationToken ct = default)
    {
        var architecture = os == TargetOs.MacOS ? CpuArchitecture.Arm64 : CpuArchitecture.X64;
        var packages = await PackagesForAsync(db, os, architecture, actor is null, null, ct);
        var scripts = await db.Scripts.AsNoTracking().Where(s => s.Os == os && (actor != null || s.IsPublic))
            .OrderBy(s => s.Category).ThenBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync(ct);
        return (
            packages.Select(p => new PickerItem(p.Id, p.Key, p.Name, p.Description, p.Category, p.HasIcon, p.IsFeatured, p.LatestVersion, false, p.Publisher)).ToList(),
            scripts.Select(s => new PickerItem(s.Id, s.Key, s.Name, s.Description, s.Category, false, false, null, true,
                s.Context == ScriptContext.User ? "Als Benutzer" : "Mit Administratorrechten")).ToList());
    }

    /// <summary>Whether anonymous visitors may see the catalog (the start page, the public API).</summary>
    public static async Task<bool> IsPublicAsync(AppDbContext db, CancellationToken ct = default) => (await SettingService.GetAsync(db, ct)).PublicCatalog;

    public static async Task<(byte[] Data, string ContentType)?> IconAsync(AppDbContext db, long packageId, CancellationToken ct = default)
    {
        var icon = await db.PackageIcons.AsNoTracking().Where(i => i.PackageId == packageId).Select(i => new { i.Data, i.ContentType }).FirstOrDefaultAsync(ct);
        return icon is null ? null : (icon.Data, icon.ContentType);
    }

    // ---- packages (administrators) -----------------------------------------------------------------------------------------------------------------

    public static async Task<(IReadOnlyList<PackageListItem> Items, int Total, int Matching)> ListPackagesAsync(AppDbContext db, Actor actor, PackageQuery query, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var all = db.Packages.AsNoTracking();
        var total = await all.CountAsync(ct);
        var filtered = all;
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = "%" + UserService.EscapeLike(query.Search.Trim()) + "%";
            filtered = filtered.Where(p => EF.Functions.ILike(p.Name, pattern, "\\") || EF.Functions.ILike(p.Key, pattern, "\\")
                                           || EF.Functions.ILike(p.Publisher ?? "", pattern, "\\") || EF.Functions.ILike(p.WingetId ?? "", pattern, "\\")
                                           || EF.Functions.ILike(p.BrewName ?? "", pattern, "\\") || EF.Functions.ILike(p.ChocoId ?? "", pattern, "\\")
                                           || EF.Functions.ILike(p.StoreId ?? "", pattern, "\\"));
        }

        if (!string.IsNullOrEmpty(query.Category))
        {
            filtered = filtered.Where(p => p.Category == query.Category);
        }

        filtered = query.Os switch
        {
            "windows" => filtered.Where(p => p.WindowsMethod != null),
            "macos" => filtered.Where(p => p.MacMethod != null),
            _ => filtered,
        };
        filtered = query.Visibility switch
        {
            "public" => filtered.Where(p => p.IsPublic),
            "private" => filtered.Where(p => !p.IsPublic),
            "featured" => filtered.Where(p => p.IsFeatured),
            "error" => filtered.Where(p => db.Releases.Any(r => r.PackageId == p.Id && r.Error != null && r.InstallerUrl == null)),
            _ => filtered,
        };

        var matching = await filtered.CountAsync(ct);
        filtered = (query.Sort, query.Descending) switch
        {
            ("category", false) => filtered.OrderBy(p => p.Category).ThenBy(p => p.Name),
            ("category", true) => filtered.OrderByDescending(p => p.Category).ThenBy(p => p.Name),
            ("updated", false) => filtered.OrderBy(p => p.UpdateDate),
            ("updated", true) => filtered.OrderByDescending(p => p.UpdateDate),
            (_, true) => filtered.OrderByDescending(p => p.Name),
            _ => filtered.OrderBy(p => p.Name),
        };

        var items = await filtered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(p => new PackageListItem(p.Id, p.Key, p.Name, p.Publisher, p.Category, p.IsPublic, p.IsFeatured,
                db.PackageIcons.Any(i => i.PackageId == p.Id), p.WindowsMethod, p.MacMethod,
                db.Releases.Where(r => r.PackageId == p.Id && r.Os == TargetOs.Windows && r.Architecture == CpuArchitecture.X64).Select(r => r.Version).FirstOrDefault(),
                db.Releases.Where(r => r.PackageId == p.Id && r.Os == TargetOs.MacOS && r.Architecture == CpuArchitecture.Arm64).Select(r => r.Version).FirstOrDefault(),
                db.Releases.Any(r => r.PackageId == p.Id && r.Error != null && r.InstallerUrl == null),
                p.ResolvedDate))
            .ToListAsync(ct);
        return (items, total, matching);
    }

    /// <summary>"Alle auflösen": every package is due for the resolver (<see cref="ReleaseService.DueAsync"/>), which runs at once.</summary>
    public static async Task<int> MarkAllDueAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var count = await db.Packages.ExecuteUpdateAsync(s => s.SetProperty(p => p.ResolvedDate, (DateTime?)null), ct);
        ResolveNow?.Invoke();
        return count;
    }

    /// <summary>Wakes the background resolver (set by the server at start).</summary>
    public static Action? ResolveNow { get; set; }

    public static async Task<IReadOnlyList<string>> CategoriesAsync(AppDbContext db, CancellationToken ct = default)
    {
        var used = await db.Packages.AsNoTracking().Select(p => p.Category).Union(db.Scripts.AsNoTracking().Select(s => s.Category)).ToListAsync(ct);
        return used.Concat(DefaultCategories).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCulture).ToList();
    }

    public static async Task<Outcome<PackageInput>> GetPackageAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var p = await db.Packages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null)
        {
            return Outcome<PackageInput>.Missing();
        }

        var hasIcon = await db.PackageIcons.AnyAsync(i => i.PackageId == id, ct);
        return Outcome<PackageInput>.Success(new PackageInput
        {
            Id = p.Id, Key = p.Key, Name = p.Name, Publisher = p.Publisher, Description = p.Description, Category = p.Category, Homepage = p.Homepage,
            IsPublic = p.IsPublic, IsFeatured = p.IsFeatured, SortOrder = p.SortOrder, WindowsMethod = p.WindowsMethod, WingetId = p.WingetId, ChocoId = p.ChocoId,
            StoreId = p.StoreId, StorePackageFamilyName = p.StorePackageFamilyName,
            DirectUrl = p.DirectUrl, DirectSilentArguments = p.DirectSilentArguments, DirectSha256 = p.DirectSha256, DirectVersion = p.DirectVersion,
            WindowsUploadFileName = p.WindowsUploadFileName, DetectDisplayName = p.DetectDisplayName, MacMethod = p.MacMethod, BrewName = p.BrewName,
            MacDirectUrl = p.MacDirectUrl, MacDirectSha256 = p.MacDirectSha256, MacDirectVersion = p.MacDirectVersion, MacUploadFileName = p.MacUploadFileName,
            MacAppBundleName = p.MacAppBundleName, HasIcon = hasIcon, ResolvedDate = p.ResolvedDate,
        });
    }

    /// <summary>The tab "Allgemein" (also creates a package).</summary>
    public static async Task<Outcome<long>> SaveGeneralAsync(AppDbContext db, Actor actor, PackageInput input, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var name = Text.Clean(input.Name, 120);
        if (name is null)
        {
            return Outcome<long>.Fail("Bitte einen Namen eingeben.", "Name");
        }

        var key = (Text.Clean(input.Key, 64) ?? Text.Slug(name)).ToLowerInvariant();
        if (!Text.IsKey(key))
        {
            return Outcome<long>.Fail("Der Schlüssel hat 1 bis 64 Zeichen: a–z, Ziffern und Bindestriche.", "Key");
        }

        if (await db.Packages.AnyAsync(p => p.Key == key && p.Id != (input.Id ?? 0), ct))
        {
            return Outcome<long>.Fail("Diesen Schlüssel hat schon eine andere App.", "Key");
        }

        var category = Text.Clean(input.Category, 60);
        if (category is null)
        {
            return Outcome<long>.Fail("Bitte eine Kategorie angeben.", "Category");
        }

        var homepage = Text.Clean(input.Homepage, 500);
        if (homepage is not null && !Text.IsHttpUrl(homepage))
        {
            return Outcome<long>.Fail("Die Homepage ist eine Adresse mit https://.", "Homepage");
        }

        Package package;
        if (input.Id is null)
        {
            package = new Package();
            db.Packages.Add(package);
        }
        else
        {
            var found = await db.Packages.FirstOrDefaultAsync(p => p.Id == input.Id, ct);
            if (found is null)
            {
                return Outcome<long>.Missing();
            }

            package = found;
        }

        package.Key = key;
        package.Name = name;
        package.Publisher = Text.Clean(input.Publisher, 120);
        package.Description = Text.Clean(input.Description, 1000);
        package.Category = category;
        package.Homepage = homepage;
        package.IsPublic = input.IsPublic;
        package.IsFeatured = input.IsFeatured;
        package.SortOrder = Math.Clamp(input.SortOrder, -9999, 9999);
        await db.SaveChangesAsync(ct);
        return Outcome<long>.Success(package.Id);
    }

    /// <summary>
    /// The tab "Windows": the source and how the installed app is recognised; <paramref name="upload"/> is a new installer for the source "Eigene
    /// Datei". Resolves the source at once.
    /// </summary>
    public static async Task<Outcome<string>> SaveWindowsAsync(AppDbContext db, Actor actor, PackageInput input, UploadFile? upload = null, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var package = await db.Packages.FirstOrDefaultAsync(p => p.Id == input.Id, ct);
        if (package is null)
        {
            return Outcome<string>.Missing();
        }

        StoredUpload? stored = null;
        switch (input.WindowsMethod)
        {
            case null:
                break;
            case InstallMethod.Winget:
                if (WingetResolver.ManifestPath(input.WingetId) is null)
                {
                    return Outcome<string>.Fail("Die winget-Id hat die Form Herausgeber.Name, z. B. Mozilla.Firefox.", "WingetId");
                }

                break;
            case InstallMethod.MicrosoftStore:
                if (!StoreSource.IsValidId(Text.Clean(input.StoreId, 40)))
                {
                    return Outcome<string>.Fail("Bitte die Store-Id angeben, z. B. 9NKSQGP7F2NH (aus der Adresse der App im Microsoft Store).", "StoreId");
                }

                break;
            case InstallMethod.Chocolatey:
                if (!ChocolateySource.IsValidId(Text.Clean(input.ChocoId, 200)))
                {
                    return Outcome<string>.Fail("Bitte die Chocolatey-Id angeben (Buchstaben, Ziffern, . - _).", "ChocoId");
                }

                break;
            case InstallMethod.Upload:
                if (upload is not null)
                {
                    var saved = await PackageUploads.SaveAsync(upload, TargetOs.Windows, "WindowsFile", ct);
                    if (!saved.Ok)
                    {
                        return Outcome<string>.Fail(saved.Error!, saved.Field);
                    }

                    stored = saved.Value;
                }
                else if (!Text.IsSha256(package.WindowsUploadSha256))
                {
                    return Outcome<string>.Fail("Bitte den Installer hochladen (.msi, .exe oder .msix).", "WindowsFile");
                }

                break;
            case InstallMethod.Direct:
                if (!Text.IsHttpUrl(input.DirectUrl))
                {
                    return Outcome<string>.Fail("Bitte die Adresse des Installers angeben (https://…).", "DirectUrl");
                }

                if (Text.Clean(input.DirectSha256, 64) is { } sha && !Text.IsSha256(sha))
                {
                    return Outcome<string>.Fail("Die SHA-256 hat 64 Hex-Zeichen.", "DirectSha256");
                }

                break;
            default:
                return Outcome<string>.Fail("Diese Quelle gibt es unter Windows nicht.", "WindowsMethod");
        }

        var oldUpload = package.WindowsUploadSha256;
        package.WindowsMethod = input.WindowsMethod;
        package.WingetId = Text.Clean(input.WingetId, 200);
        package.ChocoId = Text.Clean(input.ChocoId, 200);
        var storeId = Text.Clean(input.StoreId, 40)?.ToUpperInvariant();
        if (storeId != package.StoreId)
        {
            // another app of the Store: its package family comes with the next resolve
            package.StorePackageFamilyName = null;
        }

        package.StoreId = storeId;
        package.DirectUrl = Text.Clean(input.DirectUrl, 1000);
        package.DirectSilentArguments = Text.Clean(input.DirectSilentArguments, 500);
        package.DirectSha256 = Text.Clean(input.DirectSha256, 64)?.ToLowerInvariant();
        package.DirectVersion = Text.Clean(input.DirectVersion, 60);
        package.DetectDisplayName = Text.Clean(input.DetectDisplayName, 200);
        if (stored is not null)
        {
            package.WindowsUploadSha256 = stored.Sha256;
            package.WindowsUploadFileName = stored.FileName;
        }
        else if (input.WindowsMethod != InstallMethod.Upload)
        {
            // another source: the uploaded installer is not needed any more
            package.WindowsUploadSha256 = null;
            package.WindowsUploadFileName = null;
        }

        await db.SaveChangesAsync(ct);
        if (oldUpload is not null && oldUpload != package.WindowsUploadSha256)
        {
            await PackageUploads.DeleteIfUnusedAsync(db, oldUpload, ct);
        }

        return await ResolveAfterSaveAsync(db, package.Id, ct);
    }

    /// <summary>
    /// The tab "macOS": a Homebrew cask or formula, a direct download or an uploaded installer (<paramref name="upload"/>). Resolves the source at once.
    /// </summary>
    public static async Task<Outcome<string>> SaveMacAsync(AppDbContext db, Actor actor, PackageInput input, UploadFile? upload = null, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var package = await db.Packages.FirstOrDefaultAsync(p => p.Id == input.Id, ct);
        if (package is null)
        {
            return Outcome<string>.Missing();
        }

        var appName = Text.Clean(input.MacAppBundleName, 200);
        if (appName is not null && (!appName.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || appName.Contains('/')))
        {
            return Outcome<string>.Fail("Der Name der App endet auf .app, z. B. Firefox.app.", "MacAppBundleName");
        }

        StoredUpload? stored = null;
        switch (input.MacMethod)
        {
            case null:
                break;
            case InstallMethod.Homebrew or InstallMethod.HomebrewCask:
                if (!BrewResolver.IsValidName(Text.Clean(input.BrewName, 200)))
                {
                    return Outcome<string>.Fail("Bitte den Namen bei Homebrew angeben, z. B. firefox.", "BrewName");
                }

                break;
            case InstallMethod.Direct:
                if (!Text.IsHttpUrl(input.MacDirectUrl))
                {
                    return Outcome<string>.Fail("Bitte die Adresse angeben (https://…/App.dmg).", "MacDirectUrl");
                }

                if (PackageUploads.KindOf(Text.FileNameOf(input.MacDirectUrl), TargetOs.MacOS) is null)
                {
                    return Outcome<string>.Fail("Die Adresse endet auf .dmg, .pkg oder .zip.", "MacDirectUrl");
                }

                if (Text.Clean(input.MacDirectSha256, 64) is { } sha && !Text.IsSha256(sha))
                {
                    return Outcome<string>.Fail("Die SHA-256 hat 64 Hex-Zeichen.", "MacDirectSha256");
                }

                if (appName is null && PackageUploads.KindOf(Text.FileNameOf(input.MacDirectUrl), TargetOs.MacOS) != InstallerKind.Pkg)
                {
                    return Outcome<string>.Fail("Bitte den Namen der App angeben, z. B. Firefox.app: ThixInstall kopiert sie aus dem Download nach /Programme.", "MacAppBundleName");
                }

                break;
            case InstallMethod.Upload:
                if (upload is null && !Text.IsSha256(package.MacUploadSha256))
                {
                    return Outcome<string>.Fail("Bitte den Installer hochladen (.dmg, .pkg oder .zip).", "MacFile");
                }

                // checked before the file is stored: a dmg or zip needs the name of the app inside
                var kind = PackageUploads.KindOf(upload?.FileName ?? package.MacUploadFileName, TargetOs.MacOS);
                if (appName is null && kind is not null && kind != InstallerKind.Pkg)
                {
                    return Outcome<string>.Fail("Bitte den Namen der App angeben, z. B. Firefox.app: ThixInstall kopiert sie aus der Datei nach /Programme.", "MacAppBundleName");
                }

                if (upload is not null)
                {
                    var saved = await PackageUploads.SaveAsync(upload, TargetOs.MacOS, "MacFile", ct);
                    if (!saved.Ok)
                    {
                        return Outcome<string>.Fail(saved.Error!, saved.Field);
                    }

                    stored = saved.Value;
                }

                break;
            default:
                return Outcome<string>.Fail("Diese Quelle gibt es unter macOS nicht.", "MacMethod");
        }

        var oldUpload = package.MacUploadSha256;
        package.MacMethod = input.MacMethod;
        package.BrewName = Text.Clean(input.BrewName, 200);
        package.MacDirectUrl = Text.Clean(input.MacDirectUrl, 1000);
        package.MacDirectSha256 = Text.Clean(input.MacDirectSha256, 64)?.ToLowerInvariant();
        package.MacDirectVersion = Text.Clean(input.MacDirectVersion, 60);
        package.MacAppBundleName = appName;
        if (stored is not null)
        {
            package.MacUploadSha256 = stored.Sha256;
            package.MacUploadFileName = stored.FileName;
        }
        else if (input.MacMethod != InstallMethod.Upload)
        {
            package.MacUploadSha256 = null;
            package.MacUploadFileName = null;
        }

        await db.SaveChangesAsync(ct);
        if (oldUpload is not null && oldUpload != package.MacUploadSha256)
        {
            await PackageUploads.DeleteIfUnusedAsync(db, oldUpload, ct);
        }

        return await ResolveAfterSaveAsync(db, package.Id, ct);
    }

    private static async Task<Outcome<string>> ResolveAfterSaveAsync(AppDbContext db, long packageId, CancellationToken ct)
    {
        var errors = await ReleaseService.ResolveAsync(db, packageId, ct);
        return Outcome<string>.Success(errors.Count == 0 ? "Gespeichert und aufgelöst." : "Gespeichert. " + string.Join(" ", errors.Distinct()));
    }

    public static async Task<Outcome> DeletePackageAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var package = await db.Packages.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (package is null)
        {
            return Outcome.Missing();
        }

        // the profiles and bundles no longer offer it; the history of the devices keeps its title
        await db.ProfileItems.Where(i => i.PackageId == id).ExecuteDeleteAsync(ct);
        await db.BundleItems.Where(i => i.PackageId == id).ExecuteDeleteAsync(ct);
        db.Packages.Remove(package);
        await db.SaveChangesAsync(ct);
        await PackageUploads.DeleteIfUnusedAsync(db, package.WindowsUploadSha256, ct);
        await PackageUploads.DeleteIfUnusedAsync(db, package.MacUploadSha256, ct);
        return Outcome.Success();
    }

    /// <summary>A new icon (PNG, SVG, ICO, JPEG, WebP up to 512 KB) or, with null, none.</summary>
    public static async Task<Outcome> SetIconAsync(AppDbContext db, Actor actor, long packageId, byte[]? data, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        if (!await db.Packages.AnyAsync(p => p.Id == packageId, ct))
        {
            return Outcome.Missing();
        }

        if (data is null)
        {
            await db.PackageIcons.Where(i => i.PackageId == packageId).ExecuteDeleteAsync(ct);
            return Outcome.Success();
        }

        if (data.Length > MaxIconBytes)
        {
            return Outcome.Fail("Das Icon ist größer als 512 KB.", "Icon");
        }

        var type = IconType(data);
        if (type is null)
        {
            return Outcome.Fail("Das ist kein Bild (PNG, SVG, ICO, JPEG oder WebP).", "Icon");
        }

        await StoreIconAsync(db, packageId, data, type, ct);
        return Outcome.Success();
    }

    internal static async Task StoreIconAsync(AppDbContext db, long packageId, byte[] data, string type, CancellationToken ct)
    {
        var icon = await db.PackageIcons.FirstOrDefaultAsync(i => i.PackageId == packageId, ct);
        if (icon is null)
        {
            db.PackageIcons.Add(new PackageIcon { PackageId = packageId, Data = data, ContentType = type });
        }
        else
        {
            icon.Data = data;
            icon.ContentType = type;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>The type of an image by its first bytes (never by what a server or a file name claims); null when it is none of the allowed.</summary>
    public static string? IconType(byte[] data)
    {
        if (data.Length < 8)
        {
            return null;
        }

        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return "image/png";
        }

        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0)
        {
            return "image/x-icon";
        }

        if (data.Length > 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
        {
            return "image/webp";
        }

        var head = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 512)).TrimStart('﻿', ' ', '\r', '\n', '\t');
        if ((head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) || (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && head.Contains("<svg", StringComparison.OrdinalIgnoreCase)))
            && !Encoding.UTF8.GetString(data).Contains("<script", StringComparison.OrdinalIgnoreCase))
        {
            return "image/svg+xml";
        }

        return null;
    }

    // ---- scripts (administrators) ------------------------------------------------------------------------------------------------------------------

    public static async Task<(IReadOnlyList<ScriptListItem> Items, int Total, int Matching)> ListScriptsAsync(AppDbContext db, Actor actor, ScriptQuery query, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var all = db.Scripts.AsNoTracking();
        var total = await all.CountAsync(ct);
        var filtered = all;
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = "%" + UserService.EscapeLike(query.Search.Trim()) + "%";
            filtered = filtered.Where(s => EF.Functions.ILike(s.Name, pattern, "\\") || EF.Functions.ILike(s.Key, pattern, "\\") || EF.Functions.ILike(s.Description ?? "", pattern, "\\"));
        }

        filtered = query.Os switch
        {
            "windows" => filtered.Where(s => s.Os == TargetOs.Windows),
            "macos" => filtered.Where(s => s.Os == TargetOs.MacOS),
            _ => filtered,
        };
        filtered = query.Context switch
        {
            "system" => filtered.Where(s => s.Context == ScriptContext.System),
            "user" => filtered.Where(s => s.Context == ScriptContext.User),
            _ => filtered,
        };
        var matching = await filtered.CountAsync(ct);
        filtered = (query.Sort, query.Descending) switch
        {
            ("updated", false) => filtered.OrderBy(s => s.UpdateDate),
            ("updated", true) => filtered.OrderByDescending(s => s.UpdateDate),
            (_, true) => filtered.OrderByDescending(s => s.Name),
            _ => filtered.OrderBy(s => s.Name),
        };
        var rows = await filtered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(ct);
        var items = rows.Select(s => new ScriptListItem(s.Id, s.Key, s.Name, s.Category, s.Os, s.Shell, s.Context, s.IsPublic,
            s.Content.Split('\n').Length, s.UpdateDate)).ToList();
        return (items, total, matching);
    }

    public static async Task<Outcome<ScriptInput>> GetScriptAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var s = await db.Scripts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return s is null
            ? Outcome<ScriptInput>.Missing()
            : Outcome<ScriptInput>.Success(new ScriptInput
            {
                Id = s.Id, Key = s.Key, Name = s.Name, Description = s.Description, Category = s.Category, Os = s.Os, Shell = s.Shell, Context = s.Context,
                Content = s.Content, TimeoutSeconds = s.TimeoutSeconds, IsPublic = s.IsPublic, SortOrder = s.SortOrder,
            });
    }

    public static bool ShellFits(TargetOs os, ScriptShell shell) => os == TargetOs.Windows ? shell is ScriptShell.PowerShell or ScriptShell.Cmd : shell is ScriptShell.Bash or ScriptShell.Zsh;

    /// <summary>Saves the general part (<paramref name="content"/> false) or the script itself.</summary>
    public static async Task<Outcome<long>> SaveScriptAsync(AppDbContext db, Actor actor, ScriptInput input, bool content, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        Script script;
        if (input.Id is null)
        {
            script = new Script { Content = string.Empty };
            db.Scripts.Add(script);
        }
        else
        {
            var found = await db.Scripts.FirstOrDefaultAsync(s => s.Id == input.Id, ct);
            if (found is null)
            {
                return Outcome<long>.Missing();
            }

            script = found;
        }

        if (content)
        {
            if (!ShellFits(script.Os, input.Shell))
            {
                return Outcome<long>.Fail(script.Os == TargetOs.Windows ? "Unter Windows: PowerShell oder Cmd." : "Unter macOS: Bash oder Zsh.", "Shell");
            }

            var text = (input.Content ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
            if (text.Trim().Length == 0)
            {
                return Outcome<long>.Fail("Bitte das Skript eingeben.", "Content");
            }

            if (text.Length > ApiContract.MaxScriptLength)
            {
                return Outcome<long>.Fail($"Das Skript hat höchstens {ApiContract.MaxScriptLength:N0} Zeichen.", "Content");
            }

            if (input.TimeoutSeconds is < 0 or > 86_400)
            {
                return Outcome<long>.Fail("Die Zeitgrenze liegt zwischen 0 (30 Minuten) und 86.400 Sekunden.", "TimeoutSeconds");
            }

            script.Shell = input.Shell;
            script.Context = input.Context;
            script.Content = text;
            script.TimeoutSeconds = input.TimeoutSeconds;
            await db.SaveChangesAsync(ct);
            return Outcome<long>.Success(script.Id);
        }

        var name = Text.Clean(input.Name, 120);
        if (name is null)
        {
            return Outcome<long>.Fail("Bitte einen Namen eingeben.", "Name");
        }

        var key = (Text.Clean(input.Key, 64) ?? Text.Slug(name)).ToLowerInvariant();
        if (!Text.IsKey(key))
        {
            return Outcome<long>.Fail("Der Schlüssel hat 1 bis 64 Zeichen: a–z, Ziffern und Bindestriche.", "Key");
        }

        if (await db.Scripts.AnyAsync(s => s.Key == key && s.Id != (input.Id ?? 0), ct))
        {
            return Outcome<long>.Fail("Diesen Schlüssel hat schon ein anderes Skript.", "Key");
        }

        var category = Text.Clean(input.Category, 60);
        if (category is null)
        {
            return Outcome<long>.Fail("Bitte eine Kategorie angeben.", "Category");
        }

        if (input.Id is not null && script.Os != input.Os)
        {
            return Outcome<long>.Fail("Das System eines Skripts lässt sich nicht ändern. Bitte ein neues Skript anlegen.", "Os");
        }

        script.Key = key;
        script.Name = name;
        script.Description = Text.Clean(input.Description, 1000);
        script.Category = category;
        script.IsPublic = input.IsPublic;
        script.SortOrder = Math.Clamp(input.SortOrder, -9999, 9999);
        if (input.Id is null)
        {
            script.Os = input.Os;
            script.Shell = input.Os == TargetOs.Windows ? ScriptShell.PowerShell : ScriptShell.Zsh;
            script.Content = input.Os == TargetOs.Windows ? "# PowerShell\nWrite-Output \"Hallo von ThixInstall\"\n" : "#!/bin/zsh\necho \"Hallo von ThixInstall\"\n";
        }

        await db.SaveChangesAsync(ct);
        return Outcome<long>.Success(script.Id);
    }

    public static async Task<Outcome> DeleteScriptAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var script = await db.Scripts.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (script is null)
        {
            return Outcome.Missing();
        }

        await db.ProfileItems.Where(i => i.ScriptId == id).ExecuteDeleteAsync(ct);
        await db.BundleItems.Where(i => i.ScriptId == id).ExecuteDeleteAsync(ct);
        db.Scripts.Remove(script);
        await db.SaveChangesAsync(ct);
        return Outcome.Success();
    }

    public static string ShellName(ScriptShell shell) => shell switch
    {
        ScriptShell.PowerShell => "PowerShell",
        ScriptShell.Cmd => "Eingabeaufforderung (cmd)",
        ScriptShell.Bash => "Bash",
        ScriptShell.Zsh => "Zsh",
        _ => shell.ToString(),
    };

    public static string ContextName(ScriptContext context) => context == ScriptContext.User ? "Angemeldeter Benutzer" : "System (Administratorrechte)";

    public static string OsName(TargetOs os) => os == TargetOs.MacOS ? "macOS" : "Windows";
}
