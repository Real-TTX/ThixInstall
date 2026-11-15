using System.Net;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ThixInstall.Cloud.Controls;

/// <summary>
/// The one code base of a table (MatDesign TABLE-01 .. TABLE-04): <c>&lt;ti-table caption="Benutzer"&gt;&lt;thead&gt;...&lt;/thead&gt;&lt;tbody&gt;...&lt;/tbody&gt;&lt;/ti-table&gt;</c>.
/// It draws the wrapper and the caption that screen readers read; every <c>td</c> carries <c>data-label</c> (on a phone a row becomes a card).
/// </summary>
[HtmlTargetElement("ti-table")]
public sealed class TableTagHelper : TagHelper
{
    [HtmlAttributeName("caption")] public string Caption { get; set; } = string.Empty;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.Attributes.SetAttribute("class", "mat-table-wrap");
        output.PreContent.SetHtmlContent($"<table class=\"mat-table\"><caption class=\"mat-sr-only\">{WebUtility.HtmlEncode(Caption)}</caption>");
        output.PostContent.SetHtmlContent("</table>");
    }
}

/// <summary>
/// The one code base of a tab bar whose tabs are pages of one item: <c>&lt;ti-tabs label="Konto"&gt;&lt;ti-tab href="/Account" active&gt;Allgemein&lt;/ti-tab&gt;&lt;/ti-tabs&gt;</c>.
/// Several tab bars can stand on one page.
/// </summary>
[HtmlTargetElement("ti-tabs")]
public sealed class TabsTagHelper : TagHelper
{
    [HtmlAttributeName("label")] public string Label { get; set; } = "Bereiche";

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "nav";
        output.Attributes.SetAttribute("class", "mat-tabs");
        output.Attributes.SetAttribute("aria-label", Label);
    }
}

[HtmlTargetElement("ti-tab", ParentTag = "ti-tabs")]
public sealed class TabTagHelper : TagHelper
{
    [HtmlAttributeName("href")] public string Href { get; set; } = "#";

    [HtmlAttributeName("active")] public bool Active { get; set; }

    [HtmlAttributeName("icon")] public string? Icon { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "a";
        output.Attributes.SetAttribute("class", Active ? "mat-tab is-active" : "mat-tab");
        output.Attributes.SetAttribute("href", Href);
        if (Active)
        {
            output.Attributes.SetAttribute("aria-current", "page");
        }

        if (Icon is not null)
        {
            output.PreContent.SetHtmlContent(Web.Ui.Icon(Icon));
        }
    }
}
