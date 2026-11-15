using Microsoft.AspNetCore.Mvc;
using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Controls;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Pages.Catalog.Scripts;

/// <summary>A script with its areas as tabs: Allgemein (name, system, category, visibility) and Skript (shell, who runs it, the text).</summary>
public sealed class EditModel : AppPageModel
{
    public static readonly IReadOnlyList<Choice> Systems = [new("Windows", "Windows"), new("MacOS", "macOS")];

    public static readonly IReadOnlyList<Choice> Contexts =
    [
        new("System", "System", "Mit Administratorrechten (unter Windows: der ThixInstall-Dienst)"),
        new("User", "Angemeldeter Benutzer", "Für Einstellungen des Benutzers (Explorer, Dock, Design)"),
    ];

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Tab { get; set; } = "general";

    [BindProperty] public string Key { get; set; } = string.Empty;
    [BindProperty] public string Name { get; set; } = string.Empty;
    [BindProperty] public string Description { get; set; } = string.Empty;
    [BindProperty] public string Category { get; set; } = string.Empty;
    [BindProperty] public string Os { get; set; } = "Windows";
    [BindProperty] public bool IsPublic { get; set; } = true;
    [BindProperty] public int SortOrder { get; set; }
    [BindProperty] public string Shell { get; set; } = string.Empty;
    [BindProperty] public string Context { get; set; } = "System";
    [BindProperty(Name = "Content")] public string ScriptText { get; set; } = string.Empty;
    [BindProperty] public int TimeoutSeconds { get; set; }

    public bool IsNew => Id is null;

    public TargetOs SavedOs { get; private set; }

    public IReadOnlyList<string> Categories { get; private set; } = [];

    public IReadOnlyList<Choice> Shells => SavedOs == TargetOs.Windows
        ? [new("PowerShell", "PowerShell"), new("Cmd", "Eingabeaufforderung (cmd)")]
        : [new("Zsh", "Zsh"), new("Bash", "Bash")];

    public async Task<IActionResult> OnGetAsync()
    {
        Tab = IsNew ? "general" : Tab == "script" ? "script" : "general";
        if (!IsNew && !await LoadAsync(fill: true))
        {
            return NotFound();
        }

        Categories = await CatalogService.CategoriesAsync(Db);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Tab = "general";
        var outcome = await CatalogService.SaveScriptAsync(Db, Actor, Input(), content: false);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            return await RedisplayAsync(outcome.Field, outcome.Error);
        }

        Flash(IsNew ? "Skript angelegt. Jetzt das Skript selbst eingeben." : "Gespeichert.");
        return Redirect($"/Catalog/Scripts/Edit?id={outcome.Value}{(IsNew ? "&tab=script" : string.Empty)}");
    }

    public async Task<IActionResult> OnPostScriptAsync()
    {
        Tab = "script";
        var outcome = await CatalogService.SaveScriptAsync(Db, Actor, Input(), content: true);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            return await RedisplayAsync(outcome.Field, outcome.Error);
        }

        Flash("Skript gespeichert.");
        return Redirect($"/Catalog/Scripts/Edit?id={Id}&tab=script");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var outcome = await CatalogService.DeleteScriptAsync(Db, Actor, Id ?? 0);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        Flash("Skript gelöscht.");
        return Redirect("/Catalog/Scripts");
    }

    private ScriptInput Input() => new()
    {
        Id = Id,
        Key = Key,
        Name = Name,
        Description = Description,
        Category = Category,
        Os = Enum.TryParse<TargetOs>(Os, out var os) ? os : TargetOs.Windows,
        IsPublic = IsPublic,
        SortOrder = SortOrder,
        Shell = Enum.TryParse<ScriptShell>(Shell, out var shell) ? shell : ScriptShell.PowerShell,
        Context = Enum.TryParse<ScriptContext>(Context, out var context) ? context : ScriptContext.System,
        Content = ScriptText,
        TimeoutSeconds = TimeoutSeconds,
    };

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

    private async Task<bool> LoadAsync(bool fill)
    {
        var outcome = await CatalogService.GetScriptAsync(Db, Actor, Id!.Value);
        if (!outcome.Ok)
        {
            return false;
        }

        var s = outcome.Value!;
        SavedOs = s.Os;
        if (fill)
        {
            Key = s.Key;
            Name = s.Name;
            Description = s.Description ?? string.Empty;
            Category = s.Category;
            Os = s.Os.ToString();
            IsPublic = s.IsPublic;
            SortOrder = s.SortOrder;
            Shell = s.Shell.ToString();
            Context = s.Context.ToString();
            ScriptText = s.Content;
            TimeoutSeconds = s.TimeoutSeconds;
        }
        else if (Name.Length == 0)
        {
            Name = s.Name;
        }

        return true;
    }
}
