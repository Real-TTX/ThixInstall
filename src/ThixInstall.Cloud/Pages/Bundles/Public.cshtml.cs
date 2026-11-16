using Microsoft.AspNetCore.Mvc;
using ThixInstall.Cloud.Business;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Pages.Bundles;

/// <summary>The page of a bundle behind its link (anybody who has the link): what is in it, the download per system, the link to pass on.</summary>
public sealed class PublicModel : AppPageModel
{
    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true, Name = "error")]
    public string? DownloadError { get; set; }

    public BundleView Bundle { get; private set; } = null!;

    public bool SignedIn => Me.IsAuthenticated;

    public string Link => $"{Request.Scheme}://{Request.Host}{Request.PathBase}{ApiContract.BundlePage(Bundle.Token)}";

    /// <summary>The platforms of the bundle's system the cloud has a program for (only those get a download button).</summary>
    public IReadOnlySet<string> Available { get; private set; } = new HashSet<string>();

    public string DownloadUrl(string platform) => ApiContract.BundleDownload(Bundle.Token) + "?platform=" + platform;

    public async Task<IActionResult> OnGetAsync()
    {
        var bundle = await BundleService.ViewAsync(Db, Token, HttpContext.RequestAborted);
        if (bundle is null)
        {
            return NotFound();
        }

        Bundle = bundle;
        var platforms = bundle.Os == TargetOs.Windows ? new[] { "win-x64" } : ["osx-arm64", "osx-x64"];
        var available = new HashSet<string>();
        foreach (var platform in platforms)
        {
            if (await AgentService.IsAvailableAsync(Db, platform, HttpContext.RequestAborted))
            {
                available.Add(platform);
            }
        }

        Available = available;
        return Page();
    }
}
