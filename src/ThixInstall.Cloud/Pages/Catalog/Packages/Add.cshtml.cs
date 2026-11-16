using Microsoft.AspNetCore.Mvc;
using ThixInstall.Cloud.Business;

namespace ThixInstall.Cloud.Pages.Catalog.Packages;

/// <summary>
/// "App hinzufügen": a name, the search over every source (winget, Microsoft Store, Chocolatey; Homebrew casks and formulae), one hit per system,
/// a category – and the cloud creates the app with everything the sources say about it. The detailed form (<c>Edit</c>) stays for special cases.
/// </summary>
public sealed class AddModel : AppPageModel
{
    [BindProperty(SupportsGet = true, Name = "q")]
    public string Query { get; set; } = string.Empty;

    [BindProperty] public string WindowsPick { get; set; } = string.Empty;
    [BindProperty] public string MacPick { get; set; } = string.Empty;
    [BindProperty] public string Category { get; set; } = string.Empty;
    [BindProperty] public bool IsPublic { get; set; } = true;

    public SourceSearchResult? Result { get; private set; }

    public IReadOnlyList<string> Categories { get; private set; } = [];

    public bool Searched => Query.Trim().Length >= SourceSearch.MinQueryLength;

    public async Task OnGetAsync()
    {
        SourceSearch.Warm();
        await LoadAsync(keepChoices: false);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await SourceSearch.AddAsync(Db, Actor, new QuickAddInput(WindowsPick, MacPick, Category, IsPublic), HttpContext.RequestAborted);
        if (!outcome.Ok)
        {
            AddError(outcome.Field, outcome.Error);
            await LoadAsync(keepChoices: true);
            return Page();
        }

        var (id, message) = outcome.Value;
        if (message.Contains("Windows:", StringComparison.Ordinal) || message.Contains("macOS:", StringComparison.Ordinal))
        {
            FlashError(message);
        }
        else
        {
            Flash(message);
        }

        return Redirect($"/Catalog/Packages/Edit?id={id}&tab=versions");
    }

    private async Task LoadAsync(bool keepChoices)
    {
        Categories = await CatalogService.CategoriesAsync(Db);
        if (!Searched)
        {
            return;
        }

        Result = await SourceSearch.SearchAsync(Db, Actor, Query, HttpContext.RequestAborted);
        if (!keepChoices)
        {
            WindowsPick = Result.SuggestedWindows ?? string.Empty;
            MacPick = Result.SuggestedMac ?? string.Empty;
            Category = Result.SuggestedCategory ?? string.Empty;
        }
    }
}
