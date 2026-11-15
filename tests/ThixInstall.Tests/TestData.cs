using ThixInstall.Core.Agent;
using ThixInstall.Core.Business;
using ThixInstall.Core.Infrastructure;
using ThixInstall.Core.Platform;
using ThixInstall.Shared;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ThixInstall.Tests;

/// <summary>
/// Every test gets its own data folder, an empty catalog and job list, an inventory of its own (no registry, no
/// /Applications), a token store in memory and no machine policies. Nothing a test does touches the real computer.
/// </summary>
internal sealed class TestData : IDisposable
{
    private readonly List<InstalledEntry> _installed = [];

    public TestData()
    {
        Folder = Path.Combine(Path.GetTempPath(), "thixinstall-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        AppPaths.UseDirectoryForTests(Folder);
        AppPaths.IsMachineAgent = false;
        SettingsStore.Reset();
        TokenStore.UseMemoryForTests(true);
        AccessPolicy.UsePolicyReaderForTests(_ => false);
        CloudClient.HandlerForTests = null;
        CloudService.ResetForTests();
        JobService.ResetForTests();
        CatalogService.ResetForTests();
        InventoryService.UseReaderForTests(() => _installed.ToList());
        AgentClient.UseInProcess();
    }

    public string Folder { get; }

    /// <summary>Pretend an app is installed (the next inventory reading finds it).</summary>
    public void Install(string name, string version, string key = "", string source = "registry")
    {
        _installed.Add(new InstalledEntry(name, version, null, source, key.Length > 0 ? key : name));
        InventoryService.Refresh();
    }

    public void Dispose()
    {
        CloudService.Stop();
        JobService.ResetForTests();
        CloudClient.HandlerForTests = null;
        AccessPolicy.UsePolicyReaderForTests(null);
        TokenStore.UseMemoryForTests(false);
        InventoryService.UseReaderForTests(null);
        CatalogService.ResetForTests();
        SettingsStore.Reset();
        AppPaths.UseDirectoryForTests(null);
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A small catalog like the one the cloud seeds: browsers, tools, development, scripts.</summary>
internal static class SampleCatalog
{
    public static PackageDto Package(long id, string key, string name, string category, string latest, string? publisher = null, bool featured = false) => new()
    {
        Id = id,
        Key = key,
        Name = name,
        Publisher = publisher,
        Category = category,
        LatestVersion = latest,
        IsPublic = true,
        IsFeatured = featured,
        Description = $"{name} – {category}",
        Install = OperatingSystem.IsMacOS()
            ? new InstallPlan { Method = InstallMethod.HomebrewCask, PackageId = key, Version = latest, AppBundleName = name + ".app" }
            : new InstallPlan { Method = InstallMethod.Winget, PackageId = $"{publisher ?? name}.{name}".Replace(" ", ""), Version = latest },
        Detection = new DetectionRule { DisplayName = name },
    };

    public static ScriptDto Script(long id, string name, ScriptContext context, string content) => new()
    {
        Id = id,
        Key = name.ToLowerInvariant().Replace(' ', '-'),
        Name = name,
        Description = $"{name} auf diesem Gerät.",
        Category = "Einstellungen",
        Os = AppInfo.Os,
        Shell = OperatingSystem.IsWindows() ? ScriptShell.PowerShell : ScriptShell.Bash,
        Context = context,
        Content = content,
        TimeoutSeconds = 120,
        IsPublic = true,
        ContentHash = "AB",
    };

    public static CatalogResponse Create() => new(
        AppInfo.Os,
        AppInfo.Architecture,
        [
            Package(1, "firefox", "Firefox", "Browser", "133.0", "Mozilla", featured: true),
            Package(2, "chrome", "Google Chrome", "Browser", "131.0.6778.86", "Google"),
            Package(3, "7zip", "7-Zip", "Werkzeuge", "24.09", "Igor Pavlov"),
            Package(4, "vscode", "Visual Studio Code", "Entwicklung", "1.95.3", "Microsoft", featured: true),
            Package(5, "git", "Git", "Entwicklung", "2.47.1", "Git"),
            Package(6, "vlc", "VLC media player", "Medien", "3.0.21", "VideoLAN"),
            Package(7, "notepadplusplus", "Notepad++", "Werkzeuge", "8.7.4", "Don Ho"),
            Package(8, "keepassxc", "KeePassXC", "Sicherheit", "2.7.9", "KeePassXC Team"),
        ],
        [
            Script(20, "Dateiendungen anzeigen", ScriptContext.User, OperatingSystem.IsWindows() ? "Write-Output 'ok'" : "echo ok"),
            Script(21, "Energiesparplan Höchstleistung", ScriptContext.System, OperatingSystem.IsWindows() ? "Write-Output 'ok'" : "echo ok"),
        ],
        "local-test",
        DateTimeOffset.Now);
}
