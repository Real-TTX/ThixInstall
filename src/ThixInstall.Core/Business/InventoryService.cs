using System.Text.RegularExpressions;
using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Platform;
using ThixInstall.Shared;

namespace ThixInstall.Core.Business;

/// <summary>
/// What is installed on this device, and which catalog app each entry is. Read at start, after every job and every few
/// hours; the cloud gets it while "Sync" is on.
/// </summary>
public static class InventoryService
{
    private static readonly object Gate = new();
    private static IReadOnlyList<InstalledEntry> _entries = [];
    private static DateTimeOffset? _readAt;
    private static Func<IReadOnlyList<InstalledEntry>> _reader = InstalledApps.Read;

    /// <summary>Raised after every new reading (any thread).</summary>
    public static event EventHandler? Changed;

    public static DateTimeOffset? ReadAt
    {
        get
        {
            lock (Gate)
            {
                return _readAt;
            }
        }
    }

    internal static IReadOnlyList<InstalledEntry> Entries
    {
        get
        {
            lock (Gate)
            {
                return _entries;
            }
        }
    }

    /// <summary>Reads the installed apps again (takes a moment: registry, app bundles, Homebrew).</summary>
    public static void Refresh()
    {
        var entries = _reader();
        lock (Gate)
        {
            _entries = entries;
            _readAt = DateTimeOffset.Now;
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>The installed entry of a catalog app (the newest when several match), or null.</summary>
    internal static InstalledEntry? FindInstalled(PackageDto package) => FindInstalled(package, Entries);

    internal static InstalledEntry? FindInstalled(PackageDto package, IReadOnlyList<InstalledEntry> entries)
    {
        var rule = EffectiveRule(package);
        InstalledEntry? best = null;
        foreach (var entry in entries)
        {
            if (Matches(rule, entry) && (best is null || AppVersion.Compare(entry.Version, best.Version) > 0))
            {
                best = entry;
            }
        }

        return best;
    }

    /// <summary>The report for the cloud: every entry, the catalog apps marked.</summary>
    internal static InventoryReport BuildReport(IReadOnlyList<PackageDto> packages)
    {
        var entries = Entries;
        var matched = new Dictionary<InstalledEntry, long>(ReferenceEqualityComparer.Instance);
        foreach (var package in packages)
        {
            if (FindInstalled(package, entries) is { } entry)
            {
                matched.TryAdd(entry, package.Id);
            }
        }

        // Store packages only when they are apps of the catalog (Windows itself brings dozens)
        var apps = entries
            .Where(entry => entry.Source != "appx" || matched.ContainsKey(entry))
            .Take(ApiContract.MaxInventoryApps)
            .Select(entry => new InstalledApp(entry.Name, entry.Version, entry.Publisher, matched.TryGetValue(entry, out var id) ? id : null, entry.Source))
            .ToList();
        return new InventoryReport(apps, ReadAt ?? DateTimeOffset.Now);
    }

    /// <summary>The detection of the catalog, or a sensible one from the name and the plan.</summary>
    internal static DetectionRule EffectiveRule(PackageDto package)
    {
        var rule = package.Detection ?? new DetectionRule();
        var plan = package.Install;
        if (AppInfo.Os == TargetOs.MacOS)
        {
            return rule with
            {
                AppBundleName = rule.AppBundleName ?? plan?.AppBundleName ?? (plan?.Method == InstallMethod.Homebrew ? null : package.Name + ".app"),
                BrewName = rule.BrewName ?? (plan?.Method is InstallMethod.Homebrew or InstallMethod.HomebrewCask ? plan.PackageId : null),
            };
        }

        return rule with
        {
            DisplayName = string.IsNullOrWhiteSpace(rule.DisplayName) ? package.Name : rule.DisplayName,
            ProductCode = rule.ProductCode ?? plan?.ProductCode,
        };
    }

    internal static bool Matches(DetectionRule rule, InstalledEntry entry)
    {
        switch (entry.Source)
        {
            case "registry":
                if (rule.ProductCode is { Length: > 0 } code && string.Equals(entry.Key, code, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.IsNullOrWhiteSpace(rule.DisplayName))
                {
                    return false;
                }

                if (rule.DisplayName.StartsWith('^'))
                {
                    try
                    {
                        return Regex.IsMatch(entry.Name, rule.DisplayName, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                    }
                    catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException)
                    {
                        return false;
                    }
                }

                return entry.Name.StartsWith(rule.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase);

            case "app":
                return rule.AppBundleName is { Length: > 0 } bundle &&
                       string.Equals(Path.GetFileName(entry.Key), Path.GetFileName(bundle), StringComparison.OrdinalIgnoreCase);

            case "brew":
                return rule.BrewName is { Length: > 0 } brew && string.Equals(entry.Key, brew, StringComparison.OrdinalIgnoreCase);

            case "appx":
                return rule.PackageFamilyName is { Length: > 0 } family && string.Equals(entry.Key, family, StringComparison.OrdinalIgnoreCase);

            default:
                return false;
        }
    }

    /// <summary>Tests read their own list instead of the computer's.</summary>
    internal static void UseReaderForTests(Func<IReadOnlyList<InstalledEntry>>? reader)
    {
        _reader = reader ?? InstalledApps.Read;
        lock (Gate)
        {
            _entries = [];
            _readAt = null;
        }
    }
}
