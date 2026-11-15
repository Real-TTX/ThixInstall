using Microsoft.AspNetCore.Mvc;
using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Controls;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Pages.Catalog.Packages;

/// <summary>
/// An app of the catalog with its areas as tabs: Allgemein (name, category, icon, visibility), Windows (winget, Chocolatey or a direct download, and how
/// the installed app is recognised), macOS (Homebrew cask or formula), Versionen (what the sources resolved to, "Jetzt auflösen"). A new app has the
/// first tab only; saving it opens the others.
/// </summary>
public sealed class EditModel : AppPageModel
{
    public static readonly IReadOnlyList<Choice> WindowsMethods =
    [
        new(string.Empty, "Nicht für Windows"),
        new("Winget", "winget", "Empfohlen: die Cloud lädt den Installer aus dem winget-Manifest und hält ihn im Cache"),
        new("Chocolatey", "Chocolatey", "Das Gerät ruft choco auf (Chocolatey muss installiert sein)"),
        new("Direct", "Direkter Download", "Eine Adresse und die Parameter für die stille Installation"),
    ];

    public static readonly IReadOnlyList<Choice> MacMethods =
    [
        new(string.Empty, "Nicht für macOS"),
        new("HomebrewCask", "Homebrew-Cask", "Apps: die Cloud lädt dmg, zip oder pkg und hält sie im Cache"),
        new("Homebrew", "Homebrew-Formel", "Kommandozeilen-Werkzeuge: das Gerät ruft brew auf"),
    ];

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Tab { get; set; } = "general";

    [BindProperty] public string Key { get; set; } = string.Empty;
    [BindProperty] public string Name { get; set; } = string.Empty;
    [BindProperty] public string Publisher { get; set; } = string.Empty;
    [BindProperty] public string Description { get; set; } = string.Empty;
    [BindProperty] public string Category { get; set; } = string.Empty;
    [BindProperty] public string Homepage { get; set; } = string.Empty;
    [BindProperty] public bool IsPublic { get; set; } = true;
    [BindProperty] public bool IsFeatured { get; set; }
    [BindProperty] public int SortOrder { get; set; }
    [BindProperty] public string WindowsMethod { get; set; } = string.Empty;
    [BindProperty] public string WingetId { get; set; } = string.Empty;
    [BindProperty] public string ChocoId { get; set; } = string.Empty;
    [BindProperty] public string DirectUrl { get; set; } = string.Empty;
    [BindProperty] public string DirectSilentArguments { get; set; } = string.Empty;
    [BindProperty] public string DirectSha256 { get; set; } = string.Empty;
    [BindProperty] public string DirectVersion { get; set; } = string.Empty;
    [BindProperty] public string DetectDisplayName { get; set; } = string.Empty;
    [BindProperty] public string MacMethod { get; set; } = string.Empty;
    [BindProperty] public string BrewName { get; set; } = string.Empty;
    [BindProperty] public string MacAppBundleName { get; set; } = string.Empty;
    [BindProperty] public IFormFile? Icon { get; set; }

    public bool IsNew => Id is null;

    public bool HasIcon { get; private set; }

    public DateTime? ResolvedDate { get; private set; }

    public IReadOnlyList<string> Categories { get; private set; } = [];

    public IReadOnlyList<ReleaseView> Releases { get; private set; } = [];

    public string Title => IsNew ? "Neue App" : Name;

    public async Task<IActionResult> OnGetAsync()
    {
        Tab = IsNew ? "general" : Tab is "windows" or "macos" or "versions" ? Tab : "general";
        if (!IsNew && !await LoadAsync(fill: true))
        {
            return NotFound();
        }

        Categories = await CatalogService.CategoriesAsync(Db);
        return Page();
    }

    /// <summary>The tab "Allgemein" (also creates the app).</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        Tab = "general";
        var outcome = await CatalogService.SaveGeneralAsync(Db, Actor, Input());
        if (outcome.NotFound)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            return await RedisplayAsync(outcome.Field, outcome.Error);
        }

        if (Icon is not null && await ReadIconAsync() is { } bytes)
        {
            var icon = await CatalogService.SetIconAsync(Db, Actor, outcome.Value, bytes);
            if (!icon.Ok)
            {
                Id = outcome.Value;
                return await RedisplayAsync(icon.Field, icon.Error);
            }
        }

        Flash(IsNew ? "App angelegt. Jetzt die Quellen für Windows und macOS angeben." : "Gespeichert.");
        return Redirect($"/Catalog/Packages/Edit?id={outcome.Value}{(IsNew ? "&tab=windows" : string.Empty)}");
    }

    public async Task<IActionResult> OnPostWindowsAsync()
    {
        Tab = "windows";
        var outcome = await CatalogService.SaveWindowsAsync(Db, Actor, Input(), HttpContext.RequestAborted);
        return await AfterSourceAsync(outcome);
    }

    public async Task<IActionResult> OnPostMacAsync()
    {
        Tab = "macos";
        var outcome = await CatalogService.SaveMacAsync(Db, Actor, Input(), HttpContext.RequestAborted);
        return await AfterSourceAsync(outcome);
    }

    public async Task<IActionResult> OnPostResolveAsync()
    {
        var outcome = await ReleaseService.ResolveNowAsync(Db, Actor, Id ?? 0, HttpContext.RequestAborted);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        Flash(outcome.Value!);
        return Redirect($"/Catalog/Packages/Edit?id={Id}&tab=versions");
    }

    public async Task<IActionResult> OnPostRemoveIconAsync()
    {
        var outcome = await CatalogService.SetIconAsync(Db, Actor, Id ?? 0, null);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        Flash("Icon entfernt.");
        return Redirect($"/Catalog/Packages/Edit?id={Id}");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var outcome = await CatalogService.DeletePackageAsync(Db, Actor, Id ?? 0);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        Flash("App gelöscht. Profile und Pakete bieten sie nicht mehr an.");
        return Redirect("/Catalog/Packages");
    }

    private async Task<IActionResult> AfterSourceAsync(Outcome<string> outcome)
    {
        if (outcome.NotFound)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            return await RedisplayAsync(outcome.Field, outcome.Error);
        }

        if (outcome.Value!.Contains("Windows:", StringComparison.Ordinal) || outcome.Value.Contains("macOS:", StringComparison.Ordinal))
        {
            FlashError(outcome.Value);
        }
        else
        {
            Flash(outcome.Value);
        }

        return Redirect($"/Catalog/Packages/Edit?id={Id}&tab={Tab}");
    }

    private async Task<IActionResult> RedisplayAsync(string? field, string? error)
    {
        AddError(field, error);
        if (!IsNew)
        {
            await LoadAsync(fill: false);
        }

        Categories = await CatalogService.CategoriesAsync(Db);
        return Page();
    }

    private PackageInput Input() => new()
    {
        Id = Id,
        Key = Key,
        Name = Name,
        Publisher = Publisher,
        Description = Description,
        Category = Category,
        Homepage = Homepage,
        IsPublic = IsPublic,
        IsFeatured = IsFeatured,
        SortOrder = SortOrder,
        WindowsMethod = Enum.TryParse<InstallMethod>(WindowsMethod, out var w) ? w : null,
        WingetId = WingetId,
        ChocoId = ChocoId,
        DirectUrl = DirectUrl,
        DirectSilentArguments = DirectSilentArguments,
        DirectSha256 = DirectSha256,
        DirectVersion = DirectVersion,
        DetectDisplayName = DetectDisplayName,
        MacMethod = Enum.TryParse<InstallMethod>(MacMethod, out var m) ? m : null,
        BrewName = BrewName,
        MacAppBundleName = MacAppBundleName,
    };

    private async Task<byte[]?> ReadIconAsync()
    {
        if (Icon is null || Icon.Length == 0)
        {
            return null;
        }

        if (Icon.Length > CatalogService.MaxIconBytes)
        {
            return new byte[CatalogService.MaxIconBytes + 1];
        }

        using var buffer = new MemoryStream();
        await Icon.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>Loads the app; <paramref name="fill"/> also puts its values into the fields (not after a post, whose values stay).</summary>
    private async Task<bool> LoadAsync(bool fill)
    {
        var outcome = await CatalogService.GetPackageAsync(Db, Actor, Id!.Value);
        if (!outcome.Ok)
        {
            return false;
        }

        var p = outcome.Value!;
        HasIcon = p.HasIcon;
        ResolvedDate = p.ResolvedDate;
        if (fill)
        {
            Key = p.Key;
            Name = p.Name;
            Publisher = p.Publisher ?? string.Empty;
            Description = p.Description ?? string.Empty;
            Category = p.Category;
            Homepage = p.Homepage ?? string.Empty;
            IsPublic = p.IsPublic;
            IsFeatured = p.IsFeatured;
            SortOrder = p.SortOrder;
            WindowsMethod = p.WindowsMethod?.ToString() ?? string.Empty;
            WingetId = p.WingetId ?? string.Empty;
            ChocoId = p.ChocoId ?? string.Empty;
            DirectUrl = p.DirectUrl ?? string.Empty;
            DirectSilentArguments = p.DirectSilentArguments ?? string.Empty;
            DirectSha256 = p.DirectSha256 ?? string.Empty;
            DirectVersion = p.DirectVersion ?? string.Empty;
            DetectDisplayName = p.DetectDisplayName ?? string.Empty;
            MacMethod = p.MacMethod?.ToString() ?? string.Empty;
            BrewName = p.BrewName ?? string.Empty;
            MacAppBundleName = p.MacAppBundleName ?? string.Empty;
        }
        else if (Name.Length == 0)
        {
            Name = p.Name;
        }

        if (Tab == "versions")
        {
            Releases = await ReleaseService.ListAsync(Db, Actor, Id!.Value);
        }

        return true;
    }
}
