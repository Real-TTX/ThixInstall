using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using ThixInstall.Cloud.Web;

namespace ThixInstall.Cloud.Controls;

/// <summary>
/// The one code base of the buttons under a create or edit page, in one row, from positive to negative: <c>Speichern</c>, <c>Zurück</c>, a gap,
/// <c>Löschen</c> (MatDesign FORM-01, LIST-08): <c>&lt;ti-buttons save="Speichern" back="/Clips" delete-handler="Delete" delete-confirm="..." /&gt;</c>.
/// A button without its attribute is left out. Other buttons of the row (Kopieren ...) are the content of the element: they stand between Back and the gap.
/// Delete is a submit button of the same form that skips the validation and asks first; its address keeps the query of the page (the id of the item).
/// </summary>
[HtmlTargetElement("ti-buttons")]
public sealed class ButtonRowTagHelper : TagHelper
{
    [HtmlAttributeName("save")] public string? Save { get; set; }

    [HtmlAttributeName("save-icon")] public string SaveIcon { get; set; } = "save";

    [HtmlAttributeName("back")] public string? Back { get; set; }

    [HtmlAttributeName("back-label")] public string BackLabel { get; set; } = "Zurück";

    [HtmlAttributeName("delete-handler")] public string? DeleteHandler { get; set; }

    [HtmlAttributeName("delete-label")] public string DeleteLabel { get; set; } = "Löschen";

    [HtmlAttributeName("delete-confirm")] public string? DeleteConfirm { get; set; }

    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = null!;

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    /// <summary>The address of a handler of this page with the query the page has (without a handler of its own).</summary>
    public static string HandlerUrl(HttpRequest request, string handler)
    {
        var pairs = request.Query
            .Where(q => !string.Equals(q.Key, "handler", StringComparison.OrdinalIgnoreCase))
            .SelectMany(q => q.Value.Select(v => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(v ?? string.Empty)}"))
            .Append($"handler={Uri.EscapeDataString(handler)}");
        return request.Path + "?" + string.Join('&', pairs);
    }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var extra = (await output.GetChildContentAsync()).GetContent();
        output.TagName = null;
        var sb = new StringBuilder("<div class=\"mat-btn-row ti-btn-row\">");
        if (Save is not null)
        {
            sb.Append($"<button type=\"submit\" class=\"mat-btn mat-btn--primary\">{Ui.Icon(SaveIcon)}<span>{E(Save)}</span></button>");
        }

        if (Back is not null)
        {
            sb.Append($"<a class=\"mat-btn mat-btn--secondary ti-btn-collapse\" href=\"{E(Back)}\" aria-label=\"{E(BackLabel)}\" data-tip=\"{E(BackLabel)}\">{Ui.Icon("back")}<span>{E(BackLabel)}</span></a>");
        }

        sb.Append(extra);
        if (!string.IsNullOrEmpty(DeleteHandler))
        {
            var action = HandlerUrl(ViewContext.HttpContext.Request, DeleteHandler);
            sb.Append("<span class=\"mat-btn-row__spacer\"></span>");
            sb.Append($"<button type=\"submit\" class=\"mat-btn mat-btn--danger ti-btn-collapse\" formnovalidate formaction=\"{E(action)}\" aria-label=\"{E(DeleteLabel)}\" data-tip=\"{E(DeleteLabel)}\" data-confirm=\"{E(DeleteConfirm ?? "Wirklich löschen?")}\" data-confirm-ok=\"{E(DeleteLabel)}\" data-confirm-danger>{Ui.Icon("trash")}<span>{E(DeleteLabel)}</span></button>");
        }

        sb.Append("</div>");
        output.Content.SetHtmlContent(sb.ToString());
    }
}
