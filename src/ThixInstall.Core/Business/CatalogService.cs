using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Models;
using ThixInstall.Shared;

namespace ThixInstall.Core.Business;

/// <summary>
/// The catalog of the connected cloud for this OS and architecture. Kept in catalog.json, so the app shows it (and can
/// install from it) while the cloud is not reachable.
/// </summary>
public static class CatalogService
{
    private static readonly object Gate = new();
    private static CatalogResponse? _catalog;
    private static bool _loaded;
    private static DateTimeOffset? _loadedAt;

    /// <summary>Raised when a new catalog arrived (any thread).</summary>
    public static event EventHandler? Changed;

    internal static CatalogResponse? Current
    {
        get
        {
            lock (Gate)
            {
                if (!_loaded)
                {
                    _loaded = true;
                    var stored = JsonFile.Read<StoredCatalog>(AppPaths.CatalogFile);
                    if (stored is not null && stored.Catalog.Os == AppInfo.Os)
                    {
                        _catalog = stored.Catalog;
                        _loadedAt = stored.LoadedAt;
                    }
                }

                return _catalog;
            }
        }
    }

    public static DateTimeOffset? LoadedAt
    {
        get
        {
            _ = Current;
            lock (Gate)
            {
                return _loadedAt;
            }
        }
    }

    /// <summary>Loads the catalog from the cloud (the device token shows the account's items, too).</summary>
    internal static async Task RefreshAsync(string serverUrl, string? token, CancellationToken cancellationToken)
    {
        using var client = CloudClient.Create(serverUrl, token);
        var catalog = await client.GetCatalogAsync(AppInfo.Os, AppInfo.Architecture, cancellationToken);
        Store(catalog);
    }

    internal static void Store(CatalogResponse catalog)
    {
        var now = DateTimeOffset.Now;
        lock (Gate)
        {
            _catalog = catalog;
            _loadedAt = now;
            _loaded = true;
        }

        try
        {
            JsonFile.Write(AppPaths.CatalogFile, new StoredCatalog(catalog, now));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"The catalog could not be stored: {exception.Message}");
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Forgets the catalog (disconnecting from the cloud).</summary>
    internal static void Clear()
    {
        lock (Gate)
        {
            _catalog = null;
            _loadedAt = null;
            _loaded = true;
        }

        try
        {
            File.Delete(AppPaths.CatalogFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static PackageDto? FindPackage(long id) => Current?.Packages.FirstOrDefault(package => package.Id == id);

    public static ScriptDto? FindScript(long id) => Current?.Scripts.FirstOrDefault(script => script.Id == id);

    /// <summary>The catalog with what is installed and what is being worked on, for the app.</summary>
    public static CatalogSnapshot Snapshot(IReadOnlySet<long> busyPackages)
    {
        var catalog = Current;
        if (catalog is null)
        {
            return CatalogSnapshot.Empty(CloudService.Connection.IsConnected
                ? "Der Katalog wird geladen …"
                : "Verbinde ThixInstall mit deiner ThixInstall.Cloud, um Apps aus dem Katalog zu installieren.");
        }

        var entries = InventoryService.Entries;
        var apps = catalog.Packages
            .Where(package => package.Install is not null)
            .Select(package =>
            {
                var installed = InventoryService.FindInstalled(package, entries);
                return new CatalogApp(
                    package,
                    installed?.Version,
                    installed is not null,
                    installed is not null && AppVersion.IsNewer(package.LatestVersion, installed.Version),
                    busyPackages.Contains(package.Id));
            })
            .ToList();
        return new CatalogSnapshot(apps, catalog.Scripts.Where(script => script.Os == AppInfo.Os).ToList(), LoadedAt, null);
    }

    /// <summary>The apps with a newer version in the catalog.</summary>
    internal static IReadOnlyList<PackageDto> AppsWithUpdates()
    {
        var catalog = Current;
        if (catalog is null)
        {
            return [];
        }

        var entries = InventoryService.Entries;
        return catalog.Packages
            .Where(package => package.Install is not null &&
                              InventoryService.FindInstalled(package, entries) is { } installed &&
                              AppVersion.IsNewer(package.LatestVersion, installed.Version))
            .ToList();
    }

    /// <summary>Tests start without a stored catalog.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _catalog = null;
            _loadedAt = null;
            _loaded = false;
        }
    }

    private sealed record StoredCatalog(CatalogResponse Catalog, DateTimeOffset LoadedAt);
}
