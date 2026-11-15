using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace ThixInstall.Cloud.Web;

/// <summary>Small helpers for the markup of the toolkit: icons, the logo, initials.</summary>
public static partial class Ui
{
    public const string AppId = "thixinstall";
    public const string AppName = "ThixInstall";

    /// <summary>The accent of ThixInstall (the teal of the logo). White text on it reaches 4.5:1.</summary>
    public const string Accent = "#0F7B6C";

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex NameRegex();

    /// <summary>An icon from the page's inline sprite (the toolkit's and ThixInstall's own).</summary>
    public static IHtmlContent Icon(string name, string? extraClass = null)
    {
        if (!NameRegex().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not an icon name.", nameof(name));
        }

        var cls = string.IsNullOrEmpty(extraClass) ? "mat-icon" : $"mat-icon {extraClass}";
        return new HtmlString($"<svg class=\"{cls}\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#mat-i-{name}\"></use></svg>");
    }

    /// <summary>The logo of the menu and the sign-in: the app icon of ThixInstall.</summary>
    public static IHtmlContent Logo(string size = "md")
    {
        var px = size switch { "lg" => 56, "sm" => 28, _ => 36 };
        return new HtmlString($"<img class=\"ti-logo ti-logo--{size}\" src=\"/img/logo.svg\" alt=\"{AppName}\" width=\"{px}\" height=\"{px}\" />");
    }

    /// <summary>The colours of the letter tiles of apps without an icon: the same app always gets the same one (a mark, never the colour of a text).</summary>
    private static readonly string[] TileColours = ["#2563eb", "#0f7b6c", "#d6336c", "#7c3aed", "#ea580c", "#0891b2", "#65a30d", "#c2410c", "#4f46e5", "#be185d"];

    public static string TileColour(string key)
    {
        var sum = 0;
        foreach (var c in key)
        {
            sum = (sum * 31 + c) & 0x7fffffff;
        }

        return TileColours[sum % TileColours.Length];
    }

    /// <summary>The icon of an app: its picture from the catalog, or its first letter on a tile in its colour.</summary>
    public static IHtmlContent AppIcon(long id, string key, string name, bool hasIcon, string size = "md")
    {
        if (hasIcon)
        {
            return new HtmlString($"<span class=\"ti-appicon ti-appicon--{size}\"><img src=\"/api/v1/packages/{id}/icon\" alt=\"\" loading=\"lazy\" decoding=\"async\" /></span>");
        }

        var letter = System.Net.WebUtility.HtmlEncode(name.Length == 0 ? "?" : char.ToUpperInvariant(name[0]).ToString());
        return new HtmlString($"<span class=\"ti-appicon ti-appicon--{size} ti-appicon--letter\" aria-hidden=\"true\"><svg class=\"ti-appicon__tile\" viewBox=\"0 0 40 40\" focusable=\"false\"><rect width=\"40\" height=\"40\" rx=\"10\" fill=\"{TileColour(key)}\"/></svg><span class=\"ti-appicon__letter\">{letter}</span></span>");
    }

    /// <summary>"1 App", "3 Apps".</summary>
    public static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{Number(n)} {many}";

    public static string OsName(ThixInstall.Shared.TargetOs os) => os == ThixInstall.Shared.TargetOs.MacOS ? "macOS" : "Windows";

    public static string OsIcon(ThixInstall.Shared.TargetOs os) => os == ThixInstall.Shared.TargetOs.MacOS ? "apple" : "windows";

    public static string Initials(string name)
    {
        var letters = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));
        return letters.Length == 0 ? "?" : letters;
    }

    /// <summary>A size in bytes as people read it (German: 1,4 MB).</summary>
    public static string Bytes(long? bytes)
    {
        if (bytes is null)
        {
            return "–";
        }

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        string Format(double value, string unit) => value.ToString(value < 10 ? "0.0" : "0", culture).Replace('.', ',') + " " + unit;
        return bytes.Value switch
        {
            < 1024 => $"{bytes.Value} B",
            < 1024 * 1024 => Format(bytes.Value / 1024d, "KB"),
            _ => Format(bytes.Value / 1024d / 1024d, "MB"),
        };
    }

    /// <summary>A number with a German thousands separator (12.345).</summary>
    public static string Number(long value)
    {
        var text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (i > 0 && (text.Length - i) % 3 == 0 && text[i - 1] != '-')
            {
                sb.Append('.');
            }

            sb.Append(text[i]);
        }

        return sb.ToString();
    }
}
