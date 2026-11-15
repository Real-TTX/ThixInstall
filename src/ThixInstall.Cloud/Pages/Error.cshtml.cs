using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ThixInstall.Cloud.Pages;

/// <summary>Every error page: not found (also what the person may not see), not allowed, too many attempts, an error of the server.</summary>
[IgnoreAntiforgeryToken]
public sealed class ErrorModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? Code { get; set; }

    public string Title => Code switch
    {
        404 => "Nicht gefunden",
        403 => "Kein Zugriff",
        429 => "Zu viele Versuche",
        400 => "Ungültige Anfrage",
        _ => "Etwas ist schiefgegangen",
    };

    public string Text => Code switch
    {
        404 => "Diese Seite oder diesen Eintrag gibt es nicht (mehr).",
        403 => "Diese Seite ist der Administration vorbehalten.",
        429 => "Bitte warte eine Minute und versuche es dann noch einmal.",
        400 => "Die Anfrage konnte nicht verarbeitet werden. Bitte lade die Seite neu.",
        _ => "Bitte versuche es gleich noch einmal.",
    };

    public void OnGet()
    {
    }

    public void OnPost()
    {
    }
}
