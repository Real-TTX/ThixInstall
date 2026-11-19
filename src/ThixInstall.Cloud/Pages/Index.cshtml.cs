using Microsoft.AspNetCore.Mvc;
using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Controls;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Pages;

/// <summary>
/// The start page: the catalog as tiles to tick, like Ninite. Anybody may put a bundle together from the public apps and scripts (while the setting
/// allows it); signed in, every item counts and ThixInstall itself and a profile can be added. "Paket erstellen" makes the bundle and leads to its page.
/// </summary>
public sealed class IndexModel : AppPageModel
{
    [BindProperty(SupportsGet = true, Name = "os")]
    public string? OsQuery { get; set; }

    [BindProperty]
    public List<long> PackageIds { get; set; } = [];

    [BindProperty]
    public List<long> ScriptIds { get; set; } = [];

    [BindProperty]
    public string Name { get; set; } = string.Empty;

    [BindProperty]
    public bool IncludesAgent { get; set; }

    [BindProperty]
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>The install options of the picked Thix apps, by package id ("Options[12].Autostart"); a hidden card posts nothing.</summary>
    [BindProperty]
    public Dictionary<long, InstallOptionsForm> Options { get; set; } = [];

    public TargetOs Os { get; private set; } = TargetOs.Windows;

    public bool SignedIn => Me.IsAuthenticated;

    public IReadOnlyList<(string Category, IReadOnlyList<PickerItem> Items)> PackageGroups { get; private set; } = [];

    public IReadOnlyList<PickerItem> Scripts { get; private set; } = [];

    public IReadOnlyList<Choice> Profiles { get; private set; } = [];

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!SignedIn && !await CatalogService.IsPublicAsync(Db))
        {
            return Redirect("/Account/Login");
        }

        Os = ApiContract.ParseOs(OsQuery) ?? GuessOs();
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Os = ApiContract.ParseOs(OsQuery) ?? TargetOs.Windows;
        long? profile = long.TryParse(ProfileId, out var id) ? id : null;
        var outcome = await BundleService.CreateAsync(Db, SignedIn ? Actor : null,
            new BundleRequest(Os, PackageIds, ScriptIds, Name, SignedIn && IncludesAgent, SignedIn ? profile : null, Choices()), HttpContext.RequestAborted);
        if (!outcome.Ok)
        {
            Error = outcome.Error;
            await LoadAsync();
            return Page();
        }

        return Redirect(ApiContract.BundlePage(outcome.Value!));
    }

    /// <summary>"Nur ThixInstall herunterladen": a bundle without apps that installs ThixInstall and enrolls the device (with the chosen profile).</summary>
    public async Task<IActionResult> OnPostAgentOnlyAsync()
    {
        if (!SignedIn)
        {
            return Redirect("/Account/Login");
        }

        PackageIds = [];
        ScriptIds = [];
        IncludesAgent = true;
        return await OnPostAsync();
    }

    private async Task LoadAsync()
    {
        var (packages, scripts) = await CatalogService.PickerAsync(Db, SignedIn ? Actor : null, Os, HttpContext.RequestAborted);
        // the categories with the most popular apps first, the popular apps first in their category
        PackageGroups = packages.GroupBy(p => p.Category)
            .OrderByDescending(g => g.Count(p => p.IsFeatured)).ThenBy(g => g.Key, StringComparer.CurrentCulture)
            .Select(g => (g.Key, (IReadOnlyList<PickerItem>)g.OrderByDescending(p => p.IsFeatured).ThenBy(p => p.Name, StringComparer.CurrentCulture).ToList()))
            .ToList();
        Scripts = scripts;
        if (SignedIn)
        {
            Profiles = [new Choice(string.Empty, "Kein Profil"), .. (await ProfileService.ChoicesAsync(Db, HttpContext.RequestAborted)).Select(p => new Choice(p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), p.Name))];
        }
    }

    /// <summary>Whether a switch of a Thix app is on: what was posted (a page shown again after an error), else the default of the app.</summary>
    public bool IsOn(long packageId, string option, bool fallback)
    {
        if (!Options.TryGetValue(packageId, out var posted))
        {
            return fallback;
        }

        return option switch
        {
            nameof(InstallOptionsForm.DesktopShortcut) => posted.DesktopShortcut ?? fallback,
            nameof(InstallOptionsForm.Autostart) => posted.Autostart ?? fallback,
            nameof(InstallOptionsForm.AutomaticUpdates) => posted.AutomaticUpdates ?? fallback,
            _ => posted.FileTypes?.Contains(option, StringComparer.OrdinalIgnoreCase) ?? fallback,
        };
    }

    /// <summary>The posted options as choices (the business layer keeps only what each app offers).</summary>
    private Dictionary<long, InstallChoices> Choices() => Options.ToDictionary(
        option => option.Key,
        option => new InstallChoices(option.Value.DesktopShortcut, option.Value.Autostart, option.Value.AutomaticUpdates,
            option.Value.FileTypes?.Where(type => !string.IsNullOrWhiteSpace(type)).ToList()));

    /// <summary>A Mac sees the Mac apps first; everybody else the Windows apps.</summary>
    private TargetOs GuessOs()
    {
        var agent = Request.Headers.UserAgent.ToString();
        return agent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase) || agent.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase) ? TargetOs.MacOS : TargetOs.Windows;
    }
}

/// <summary>The switches of one Thix app on the start page (SETUP-08). <see cref="FileTypes"/> always posts an empty value first, so "none" is "none".</summary>
public sealed class InstallOptionsForm
{
    public bool? DesktopShortcut { get; set; }

    public bool? Autostart { get; set; }

    public bool? AutomaticUpdates { get; set; }

    public List<string>? FileTypes { get; set; }
}
