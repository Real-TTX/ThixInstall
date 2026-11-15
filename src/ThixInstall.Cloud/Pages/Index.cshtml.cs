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
            new BundleRequest(Os, PackageIds, ScriptIds, Name, SignedIn && IncludesAgent, SignedIn ? profile : null), HttpContext.RequestAborted);
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

    /// <summary>A Mac sees the Mac apps first; everybody else the Windows apps.</summary>
    private TargetOs GuessOs()
    {
        var agent = Request.Headers.UserAgent.ToString();
        return agent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase) || agent.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase) ? TargetOs.MacOS : TargetOs.Windows;
    }
}
