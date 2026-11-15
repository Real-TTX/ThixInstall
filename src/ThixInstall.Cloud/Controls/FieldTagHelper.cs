using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ThixInstall.Cloud.Controls;

/// <summary>One choice of a select or a group of radios.</summary>
public sealed record Choice(string Value, string Label, string? Hint = null);

/// <summary>
/// The one code base of a form field (label above, hint below, the error at the field): <c>&lt;ti-field name="Text" label="Text" required /&gt;</c>.
/// Kinds: text, password, number, url, search, textarea, select (the toolkit's select picker), radios (up to five exclusive choices), switch, check, file.
/// The message of the server (<c>ModelState</c>) is shown at the field; the id of a field is its name (or <c>id</c>, when two forms of a page have a field
/// of the same name), so the summary at the top of a form can link to it. A field can depend on another (<c>show-if="#Kind" show-value="Text"</c>).
/// </summary>
[HtmlTargetElement("ti-field", TagStructure = TagStructure.WithoutEndTag)]
public sealed class FieldTagHelper : TagHelper
{
    [HtmlAttributeName("name")] public string Name { get; set; } = string.Empty;

    [HtmlAttributeName("id")] public string? Id { get; set; }

    [HtmlAttributeName("label")] public string? Label { get; set; }

    [HtmlAttributeName("kind")] public string Kind { get; set; } = "text";

    [HtmlAttributeName("value")] public object? Value { get; set; }

    [HtmlAttributeName("required")] public bool Required { get; set; }

    [HtmlAttributeName("optional")] public bool Optional { get; set; }

    [HtmlAttributeName("hint")] public string? Hint { get; set; }

    [HtmlAttributeName("placeholder")] public string? Placeholder { get; set; }

    [HtmlAttributeName("autocomplete")] public string? Autocomplete { get; set; }

    [HtmlAttributeName("inputmode")] public string? InputMode { get; set; }

    [HtmlAttributeName("maxlength")] public int? MaxLength { get; set; }

    [HtmlAttributeName("minlength")] public int? MinLength { get; set; }

    [HtmlAttributeName("rows")] public int Rows { get; set; } = 4;

    [HtmlAttributeName("accept")] public string? Accept { get; set; }

    [HtmlAttributeName("max-size")] public string? MaxSize { get; set; }

    [HtmlAttributeName("options")] public IEnumerable<Choice>? Options { get; set; }

    [HtmlAttributeName("full")] public bool Full { get; set; }

    [HtmlAttributeName("disabled")] public bool Disabled { get; set; }

    [HtmlAttributeName("autofocus")] public bool Autofocus { get; set; }

    [HtmlAttributeName("mono")] public bool Mono { get; set; }

    /// <summary>Values to suggest while typing (a native list of suggestions; any other value is allowed too).</summary>
    [HtmlAttributeName("suggest")] public IEnumerable<string>? Suggest { get; set; }

    [HtmlAttributeName("show-if")] public string? ShowIf { get; set; }

    [HtmlAttributeName("show-value")] public string? ShowValue { get; set; }

    [HtmlAttributeName("msg-required")] public string? MsgRequired { get; set; }

    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = null!;

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = null;
        var id = Id ?? Name.Replace('.', '_');
        var value = Convert.ToString(Value, CultureInfo.InvariantCulture) ?? string.Empty;
        ViewContext.ViewData.ModelState.TryGetValue(Name, out var state);
        var error = state?.Errors.FirstOrDefault()?.ErrorMessage;
        if (state?.AttemptedValue is { } attempted && Kind is not ("password" or "file"))
        {
            value = attempted;
        }

        if (Kind == "radios")
        {
            output.Content.SetHtmlContent(Radios(id, value, error));
            return;
        }

        var sb = new StringBuilder();
        var cls = "mat-field" + (Full ? " mat-field--full" : string.Empty) + (error is null ? string.Empty : " is-invalid");
        sb.Append($"<div class=\"{cls}\"").Append(ShowIfAttributes()).Append('>');

        var describedBy = new List<string>();
        if (Hint is not null)
        {
            describedBy.Add(id + "-hint");
        }

        describedBy.Add(id + "-error");
        var common = new StringBuilder();
        if (Required)
        {
            common.Append(" required");
            common.Append($" data-msg-required=\"{E(MsgRequired ?? "Bitte ausfüllen.")}\"");
        }

        if (Disabled)
        {
            common.Append(" disabled");
        }

        if (Autofocus)
        {
            common.Append(" autofocus");
        }

        if (error is not null)
        {
            common.Append(" aria-invalid=\"true\"");
        }

        common.Append($" aria-describedby=\"{string.Join(' ', describedBy)}\"");

        switch (Kind)
        {
            case "switch":
                var on = value.StartsWith("true", StringComparison.OrdinalIgnoreCase) || value == "on";
                sb.Append($"<label class=\"mat-switch\"><input type=\"checkbox\" id=\"{id}\" name=\"{E(Name)}\" value=\"true\"{(on ? " checked" : string.Empty)}{(Disabled ? " disabled" : string.Empty)} /><span class=\"mat-switch__track\"></span><span>{E(Label)}</span></label>");
                sb.Append($"<input type=\"hidden\" name=\"{E(Name)}\" value=\"false\" />");
                break;
            case "check":
                var ticked = value.StartsWith("true", StringComparison.OrdinalIgnoreCase) || value == "on";
                sb.Append($"<label class=\"mat-check\"><input type=\"checkbox\" id=\"{id}\" name=\"{E(Name)}\" value=\"true\"{(ticked ? " checked" : string.Empty)}{(Disabled ? " disabled" : string.Empty)} /><span class=\"mat-check__text\">{E(Label)}</span></label>");
                sb.Append($"<input type=\"hidden\" name=\"{E(Name)}\" value=\"false\" />");
                break;
            default:
                if (Label is not null)
                {
                    sb.Append($"<label class=\"mat-label\" for=\"{id}\">{E(Label)}");
                    if (Required)
                    {
                        sb.Append(" <span class=\"mat-req\" aria-hidden=\"true\">*</span>");
                    }
                    else if (Optional)
                    {
                        sb.Append(" <span class=\"mat-opt\">(optional)</span>");
                    }

                    sb.Append("</label>");
                }

                AppendControl(sb, id, value, common.ToString());
                break;
        }

        if (Hint is not null)
        {
            sb.Append($"<span class=\"mat-hint\" id=\"{id}-hint\">{E(Hint)}</span>");
        }

        sb.Append($"<span class=\"mat-error\" id=\"{id}-error\" aria-live=\"polite\">{E(error)}</span>");
        sb.Append("</div>");
        output.Content.SetHtmlContent(sb.ToString());
    }

    private string ShowIfAttributes()
    {
        if (ShowIf is null)
        {
            return string.Empty;
        }

        return $" data-show-if=\"{E(ShowIf)}\"" + (ShowValue is null ? string.Empty : $" data-show-value=\"{E(ShowValue)}\"");
    }

    private void AppendControl(StringBuilder sb, string id, string value, string common)
    {
        var name = E(Name);
        string Attr(string key, string? v) => v is null ? string.Empty : $" {key}=\"{E(v)}\"";
        var limits = Attr("maxlength", MaxLength?.ToString(CultureInfo.InvariantCulture)) + Attr("minlength", MinLength?.ToString(CultureInfo.InvariantCulture))
                     + Attr("placeholder", Placeholder) + Attr("autocomplete", Autocomplete) + Attr("inputmode", InputMode);

        switch (Kind)
        {
            case "textarea":
                sb.Append($"<textarea class=\"mat-textarea{(Mono ? " mat-mono" : string.Empty)}\" id=\"{id}\" name=\"{name}\" rows=\"{Rows}\"{limits}{common}>{E(value)}</textarea>");
                break;
            case "select":
                var placeholder = Placeholder ?? Options?.FirstOrDefault(o => o.Value.Length == 0)?.Label;
                sb.Append($"<div class=\"mat-picker\" data-picker=\"select\"{Attr("data-placeholder", placeholder)}>");
                sb.Append($"<input class=\"mat-picker__input\" id=\"{id}\" name=\"{name}\" value=\"{E(value)}\"{common} />");
                sb.Append("<div class=\"mat-picker__options\" hidden>");
                foreach (var o in Options ?? [])
                {
                    sb.Append($"<button type=\"button\" class=\"mat-picker__option\" data-value=\"{E(o.Value)}\"><span class=\"mat-picker__text\"><span class=\"mat-picker__name\">{E(o.Label)}</span>");
                    if (o.Hint is not null)
                    {
                        sb.Append($"<span class=\"mat-picker__desc\">{E(o.Hint)}</span>");
                    }

                    sb.Append("</span></button>");
                }

                sb.Append("</div></div>");
                break;
            case "file":
                sb.Append($"<div class=\"mat-upload\" data-upload data-paste{Attr("data-max-size", MaxSize)}>");
                sb.Append($"<input class=\"mat-upload__input\" type=\"file\" id=\"{id}\" name=\"{name}\"{Attr("accept", Accept)}{common} /></div>");
                break;
            default:
                var type = Kind is "password" or "number" or "url" or "search" or "email" ? Kind : "text";
                var list = Suggest is null ? string.Empty : $" list=\"{id}-list\"";
                sb.Append($"<input class=\"mat-input{(Mono ? " mat-mono" : string.Empty)}\" type=\"{type}\" id=\"{id}\" name=\"{name}\" value=\"{E(type == "password" ? string.Empty : value)}\"{list}{limits}{common} />");
                if (Suggest is not null)
                {
                    sb.Append($"<datalist id=\"{id}-list\">");
                    foreach (var s in Suggest)
                    {
                        sb.Append($"<option value=\"{E(s)}\"></option>");
                    }

                    sb.Append("</datalist>");
                }
                break;
        }
    }

    /// <summary>Up to five exclusive choices: a fieldset with a legend and a radio per choice, as the toolkit draws them.</summary>
    private string Radios(string id, string value, string? error)
    {
        var sb = new StringBuilder();
        sb.Append($"<fieldset class=\"mat-field mat-fieldset{(error is null ? string.Empty : " is-invalid")}\" id=\"{id}\"").Append(ShowIfAttributes());
        sb.Append($"><legend class=\"mat-legend\">{E(Label)}</legend><div class=\"mat-checks mat-checks--inline\">");
        var i = 0;
        foreach (var o in Options ?? [])
        {
            var checkedAttr = o.Value == value ? " checked" : string.Empty;
            sb.Append($"<label class=\"mat-check\"><input type=\"radio\" id=\"{id}-{i++}\" name=\"{E(Name)}\" value=\"{E(o.Value)}\"{checkedAttr}{(Disabled ? " disabled" : string.Empty)} />");
            sb.Append($"<span class=\"mat-check__text\">{E(o.Label)}{(o.Hint is null ? string.Empty : $"<small class=\"mat-muted\">{E(o.Hint)}</small>")}</span></label>");
        }

        sb.Append("</div>");
        if (Hint is not null)
        {
            sb.Append($"<span class=\"mat-hint\" id=\"{id}-hint\">{E(Hint)}</span>");
        }

        sb.Append($"<span class=\"mat-error\" id=\"{id}-error\" aria-live=\"polite\">{E(error)}</span></fieldset>");
        return sb.ToString();
    }
}
