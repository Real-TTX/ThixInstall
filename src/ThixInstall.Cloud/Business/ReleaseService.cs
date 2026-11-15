using Microsoft.EntityFrameworkCore;
using ThixInstall.Cloud.Data;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Business;

/// <summary>One resolved release as the "Versionen" tab shows it.</summary>
public sealed record ReleaseView(long Id, TargetOs Os, CpuArchitecture Architecture, InstallMethod Method, string? Version, string? InstallerUrl, string? Sha256,
    InstallerKind? Kind, string? SilentArguments, string? FileName, DateTime ResolvedDate, string? Error, string? Note, bool IsCached);

/// <summary>
/// Resolves the sources of the packages into releases (<see cref="PackageRelease"/>), one per system and architecture: winget and Homebrew through their
/// resolvers, Chocolatey as a plan for the command line, a direct download as it is. Runs every six hours for every package (<see cref="DueAsync"/>), at once
/// after a package was saved, and by the button "Jetzt auflösen". A failure keeps the last release and writes its message into it.
/// </summary>
public static class ReleaseService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    public static readonly IReadOnlyList<(TargetOs Os, CpuArchitecture Architecture)> Platforms =
    [
        (TargetOs.Windows, CpuArchitecture.X64), (TargetOs.Windows, CpuArchitecture.Arm64),
        (TargetOs.MacOS, CpuArchitecture.Arm64), (TargetOs.MacOS, CpuArchitecture.X64),
    ];

    /// <summary>The packages whose sources were not resolved within <see cref="Interval"/>.</summary>
    public static async Task<IReadOnlyList<long>> DueAsync(AppDbContext db, CancellationToken ct = default)
    {
        var limit = DateTime.UtcNow - Interval;
        return await db.Packages.AsNoTracking()
            .Where(p => p.ResolvedDate == null || p.ResolvedDate < limit)
            .OrderBy(p => p.ResolvedDate)
            .Select(p => p.Id)
            .ToListAsync(ct);
    }

    /// <summary>"Jetzt auflösen" of one package (administrators).</summary>
    public static async Task<Outcome<string>> ResolveNowAsync(AppDbContext db, Actor actor, long packageId, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        if (!await db.Packages.AnyAsync(p => p.Id == packageId, ct))
        {
            return Outcome<string>.Missing();
        }

        var errors = await ResolveAsync(db, packageId, ct);
        return Outcome<string>.Success(errors.Count == 0 ? "Quellen aufgelöst." : string.Join(" ", errors.Distinct()));
    }

    /// <summary>Resolves the sources of a package for every platform; returns the messages of what failed.</summary>
    public static async Task<IReadOnlyList<string>> ResolveAsync(AppDbContext db, long packageId, CancellationToken ct = default)
    {
        var package = await db.Packages.FirstOrDefaultAsync(p => p.Id == packageId, ct);
        if (package is null)
        {
            return [];
        }

        var settings = await SettingService.GetAsync(db, ct);
        var errors = new List<string>();
        var results = new Dictionary<(TargetOs, CpuArchitecture), ResolvedRelease?>();

        await ResolveSystemAsync(TargetOs.Windows, () => WindowsAsync(package, settings.GitHubToken, ct));
        await ResolveSystemAsync(TargetOs.MacOS, () => MacAsync(package, ct));

        var existing = await db.Releases.Where(r => r.PackageId == packageId).ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var platform in Platforms)
        {
            var row = existing.FirstOrDefault(r => r.Os == platform.Os && r.Architecture == platform.Architecture);
            if (!results.TryGetValue(platform, out var resolved))
            {
                // the system has no source (any more): its release goes
                if (row is not null)
                {
                    db.Releases.Remove(row);
                }

                continue;
            }

            if (resolved is null)
            {
                // failed: the last release stays, with the message
                if (row is not null)
                {
                    row.Error = Text.Clip(errors.LastOrDefault(), 1000);
                    row.ResolvedDate = now;
                }
                else
                {
                    db.Releases.Add(new PackageRelease
                    {
                        PackageId = packageId, Os = platform.Os, Architecture = platform.Architecture, ResolvedDate = now,
                        Method = (platform.Os == TargetOs.Windows ? package.WindowsMethod : package.MacMethod) ?? InstallMethod.Winget,
                        Error = Text.Clip(errors.LastOrDefault(), 1000),
                    });
                }

                continue;
            }

            if (row is null)
            {
                row = new PackageRelease { PackageId = packageId, Os = platform.Os, Architecture = platform.Architecture };
                db.Releases.Add(row);
            }

            row.Method = resolved.Method;
            row.Version = Text.Clean(resolved.Version, 100);
            row.InstallerUrl = Text.Clean(resolved.InstallerUrl, 2000);
            row.Sha256 = Text.IsSha256(resolved.Sha256) ? resolved.Sha256!.ToLowerInvariant() : null;
            row.InstallerKind = row.InstallerUrl is null ? null : resolved.Kind;
            row.SilentArguments = Text.Clean(resolved.SilentArguments, 500);
            row.ProductCode = Text.Clean(resolved.ProductCode, 100);
            row.AppBundleName = Text.Clean(resolved.AppBundleName, 200);
            row.FileName = Text.Clean(resolved.FileName, 260);
            row.ResolvedDate = now;
            row.Error = null;
            row.Note = row.InstallerUrl is null ? Text.Clean(resolved.Note, 500) : null;
        }

        package.ResolvedDate = now;
        await db.SaveChangesAsync(ct);
        return errors;

        async Task ResolveSystemAsync(TargetOs os, Func<Task<IReadOnlyDictionary<CpuArchitecture, ResolvedRelease>?>> resolve)
        {
            try
            {
                var byArchitecture = await resolve();
                if (byArchitecture is null)
                {
                    return;
                }

                foreach (var (architecture, release) in byArchitecture)
                {
                    results[(os, architecture)] = release;
                }
            }
            catch (Exception ex) when (ex is ResolveException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                var message = ex is ResolveException ? ex.Message : $"Die Quelle ist nicht erreichbar ({ex.GetType().Name}).";
                errors.Add((os == TargetOs.Windows ? "Windows: " : "macOS: ") + message);
                foreach (var platform in Platforms.Where(p => p.Os == os))
                {
                    results[platform] = null;
                }
            }
        }
    }

    private static async Task<IReadOnlyDictionary<CpuArchitecture, ResolvedRelease>?> WindowsAsync(Package package, string? gitHubToken, CancellationToken ct)
    {
        switch (package.WindowsMethod)
        {
            case InstallMethod.Winget when !string.IsNullOrWhiteSpace(package.WingetId):
                return await WingetResolver.ResolveAsync(package.WingetId!, gitHubToken, ct);
            case InstallMethod.Chocolatey when !string.IsNullOrWhiteSpace(package.ChocoId):
                var choco = new ResolvedRelease(InstallMethod.Chocolatey, null);
                return new Dictionary<CpuArchitecture, ResolvedRelease> { [CpuArchitecture.X64] = choco, [CpuArchitecture.Arm64] = choco };
            case InstallMethod.Direct when Text.IsHttpUrl(package.DirectUrl):
                var direct = Direct(package);
                return new Dictionary<CpuArchitecture, ResolvedRelease> { [CpuArchitecture.X64] = direct, [CpuArchitecture.Arm64] = direct };
            default:
                return null;
        }
    }

    private static async Task<IReadOnlyDictionary<CpuArchitecture, ResolvedRelease>?> MacAsync(Package package, CancellationToken ct)
        => package.MacMethod is InstallMethod.HomebrewCask or InstallMethod.Homebrew && !string.IsNullOrWhiteSpace(package.BrewName)
            ? await BrewResolver.ResolveAsync(package.BrewName!, package.MacMethod == InstallMethod.HomebrewCask, ct)
            : null;

    /// <summary>A direct download: an msi is started with msiexec, everything else as a program with the given arguments.</summary>
    public static ResolvedRelease Direct(Package package)
    {
        var fileName = Text.FileNameOf(package.DirectUrl) ?? $"{package.Key}-setup.exe";
        var kind = fileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? InstallerKind.Msi
            : fileName.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".appx", StringComparison.OrdinalIgnoreCase) ? InstallerKind.Msix
            : InstallerKind.Exe;
        if (kind == InstallerKind.Exe && !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".exe";
        }

        var arguments = package.DirectSilentArguments ?? (kind == InstallerKind.Msi ? "/qn /norestart" : string.Empty);
        return new ResolvedRelease(InstallMethod.Direct, package.DirectVersion, package.DirectUrl, package.DirectSha256, kind, arguments, FileName: fileName);
    }

    public static async Task<IReadOnlyList<ReleaseView>> ListAsync(AppDbContext db, Actor actor, long packageId, CancellationToken ct = default)
    {
        UserService.RequireAdmin(actor);
        var rows = await db.Releases.AsNoTracking().Where(r => r.PackageId == packageId).OrderBy(r => r.Os).ThenBy(r => r.Architecture).ToListAsync(ct);
        var urls = rows.Where(r => r.InstallerUrl != null).Select(r => r.InstallerUrl!).Distinct().ToList();
        var cached = await db.CacheFiles.AsNoTracking().Where(c => urls.Contains(c.SourceUrl)).Select(c => c.SourceUrl).ToListAsync(ct);
        return rows.Select(r => new ReleaseView(r.Id, r.Os, r.Architecture, r.Method, r.Version, r.InstallerUrl, r.Sha256, r.InstallerKind, r.SilentArguments,
            r.FileName, r.ResolvedDate, r.Error, r.Note, r.InstallerUrl is not null && cached.Contains(r.InstallerUrl))).ToList();
    }

    /// <summary>The plan of a release for a device (or the command line of the method when there is no release yet).</summary>
    public static InstallPlan? Plan(Package package, PackageRelease? release, TargetOs os)
    {
        var method = os == TargetOs.Windows ? package.WindowsMethod : package.MacMethod;
        if (method is null)
        {
            return null;
        }

        var packageId = method switch
        {
            InstallMethod.Winget => package.WingetId,
            InstallMethod.Chocolatey => package.ChocoId,
            InstallMethod.Homebrew or InstallMethod.HomebrewCask => package.BrewName,
            _ => null,
        };
        if (release is null || release.Method != method)
        {
            return method == InstallMethod.Direct ? null : new InstallPlan { Method = method.Value, PackageId = packageId };
        }

        var download = release.InstallerUrl is not null && release.InstallerKind is not null;
        return new InstallPlan
        {
            Method = method.Value,
            PackageId = packageId,
            Version = release.Version,
            DownloadUrl = download ? ApiContract.Download(release.Id) : null,
            FileName = download ? release.FileName : null,
            Sha256 = download ? release.Sha256 : null,
            Size = download ? release.Size : null,
            InstallerKind = download ? release.InstallerKind : null,
            SilentArguments = download ? release.SilentArguments : null,
            ProductCode = release.ProductCode,
            AppBundleName = package.MacAppBundleName ?? release.AppBundleName,
        };
    }

    public static string MethodName(InstallMethod? method) => method switch
    {
        InstallMethod.Winget => "winget",
        InstallMethod.Chocolatey => "Chocolatey",
        InstallMethod.Direct => "Direkter Download",
        InstallMethod.Homebrew => "Homebrew-Formel",
        InstallMethod.HomebrewCask => "Homebrew-Cask",
        _ => "–",
    };

    public static string KindName(InstallerKind? kind) => kind switch
    {
        InstallerKind.Exe => "Programm (exe)",
        InstallerKind.Msi => "MSI",
        InstallerKind.Msix => "MSIX",
        InstallerKind.Dmg => "Disk-Image (dmg)",
        InstallerKind.Pkg => "Installer-Paket (pkg)",
        InstallerKind.Zip => "Zip mit App",
        _ => "Paketmanager",
    };
}
