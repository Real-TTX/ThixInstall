using Microsoft.AspNetCore.Mvc;
using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Controls;

namespace ThixInstall.Cloud.Pages.Admin.Settings;

/// <summary>
/// The settings of the cloud with its areas as tabs: Allgemein (public catalog, cache size), Desktop-App (the channel the desktop packages come from,
/// uploads that replace them), Quellen (the GitHub token of the winget resolver).
/// </summary>
public sealed class IndexModel : AppPageModel
{
    public static readonly IReadOnlyList<Choice> Channels =
    [
        new("release", "Release", "Die stabilen Versionen (Branch main)"),
        new("nightly", "Nightly", "Die Entwicklungsversionen (Branch dev)"),
    ];

    [BindProperty(SupportsGet = true)]
    public string Tab { get; set; } = "general";

    [BindProperty] public bool PublicCatalog { get; set; }
    [BindProperty] public int CacheMaxGigabytes { get; set; }
    [BindProperty] public string DesktopChannel { get; set; } = "release";
    [BindProperty] public string GitHubToken { get; set; } = string.Empty;
    [BindProperty] public string Platform { get; set; } = string.Empty;
    [BindProperty(Name = "File")] public IFormFile? Upload { get; set; }

    public bool HasGitHubToken { get; private set; }

    public IReadOnlyList<AgentSource> Sources { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Tab = Tab is "desktop" or "sources" ? Tab : "general";
        await LoadAsync(fill: true);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Tab = "general";
        return await AfterAsync(await SettingService.SaveGeneralAsync(Db, Actor, PublicCatalog, CacheMaxGigabytes));
    }

    public async Task<IActionResult> OnPostChannelAsync()
    {
        Tab = "desktop";
        var outcome = await SettingService.SaveChannelAsync(Db, Actor, DesktopChannel);
        AgentService.Forget();
        return await AfterAsync(outcome);
    }

    public async Task<IActionResult> OnPostUploadAsync()
    {
        Tab = "desktop";
        if (Upload is null)
        {
            return await AfterAsync(Outcome.Fail("Bitte eine Datei wählen.", "File"));
        }

        await using var stream = Upload.OpenReadStream();
        return await AfterAsync(await AgentService.UploadAsync(Actor, Platform, stream, Upload.Length, HttpContext.RequestAborted));
    }

    public async Task<IActionResult> OnPostRemoveUploadAsync(string platform)
    {
        Tab = "desktop";
        return await AfterAsync(AgentService.RemoveUpload(Actor, platform));
    }

    public async Task<IActionResult> OnPostTokenAsync(bool remove)
    {
        Tab = "sources";
        return await AfterAsync(await SettingService.SaveGitHubTokenAsync(Db, Actor, GitHubToken, remove));
    }

    private async Task<IActionResult> AfterAsync(Outcome outcome)
    {
        if (outcome.NotFound)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            AddError(outcome.Field, outcome.Error);
            await LoadAsync(fill: false);
            return Page();
        }

        Flash("Gespeichert.");
        return Redirect($"/Admin/Settings{(Tab == "general" ? string.Empty : "?tab=" + Tab)}");
    }

    private async Task LoadAsync(bool fill)
    {
        UserService.RequireAdmin(Actor);
        var settings = await SettingService.GetAsync(Db);
        HasGitHubToken = settings.GitHubToken is not null;
        if (fill)
        {
            PublicCatalog = settings.PublicCatalog;
            CacheMaxGigabytes = settings.CacheMaxGigabytes;
            DesktopChannel = settings.DesktopChannel;
        }

        if (Tab == "desktop")
        {
            Sources = await AgentService.SourcesAsync(Db, Actor);
        }
    }
}
