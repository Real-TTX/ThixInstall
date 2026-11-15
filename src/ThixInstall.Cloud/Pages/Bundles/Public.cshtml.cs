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

    public string DownloadUrl(string platform) => ApiContract.BundleDownload(Bundle.Token) + "?platform=" + platform;

    public async Task<IActionResult> OnGetAsync()
    {
        var bundle = await BundleService.ViewAsync(Db, Token, HttpContext.RequestAborted);
        if (bundle is null)
        {
            return NotFound();
        }

        Bundle = bundle;
        return Page();
    }
}
