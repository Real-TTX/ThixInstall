using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ThixInstall.Cloud.Business;

/// <summary>Small helpers for texts the business layer stores: trimming, keys, slugs, hashes.</summary>
public static partial class Text
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex KeyRegex();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Regex();

    /// <summary>Trims a text and cuts it to <paramref name="max"/>; empty becomes null.</summary>
    public static string? Clean(string? text, int max)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return null;
        }

        return value.Length <= max ? value : value[..max];
    }

    public static string Clip(string? text, int max)
    {
        var value = text ?? string.Empty;
        return value.Length <= max ? value : value[..max];
    }

    /// <summary>The end of a long output (the end says how it ended).</summary>
    public static string? Tail(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return text.Length <= max ? text : "…" + text[^(max - 1)..];
    }

    public static bool IsKey(string? key) => key is not null && KeyRegex().IsMatch(key);

    /// <summary>"Visual Studio Code" → "visual-studio-code" (umlauts written out).</summary>
    public static string Slug(string? text, int max = 64)
    {
        var lower = (text ?? string.Empty).ToLowerInvariant()
            .Replace("ä", "ae", StringComparison.Ordinal).Replace("ö", "oe", StringComparison.Ordinal)
            .Replace("ü", "ue", StringComparison.Ordinal).Replace("ß", "ss", StringComparison.Ordinal)
            .Replace("+", "plus", StringComparison.Ordinal);
        var normalized = lower.Normalize(NormalizationForm.FormD);
        var plain = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                plain.Append(c);
            }
        }

        var slug = NotSlug().Replace(plain.ToString(), "-").Trim('-');
        if (slug.Length > max)
        {
            slug = slug[..max].Trim('-');
        }

        return slug;
    }

    public static string Sha256Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static bool IsSha256(string? value) => value is not null && Sha256Regex().IsMatch(value);

    /// <summary>An absolute http or https address.</summary>
    public static bool IsHttpUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>A token for the API (32 hex characters, letters and digits only).</summary>
    public static string NewToken() => Guid.NewGuid().ToString("N");

    /// <summary>The file name at the end of an address ("Firefox%20133.0.dmg" → "Firefox 133.0.dmg"), safe for a file system; null when there is none.</summary>
    public static string? FileNameOf(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var last = Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty);
        var safe = new string(last.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c is not ('/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')).ToArray()).Trim();
        return safe.Length == 0 || safe is "." or ".." ? null : Clip(safe, 200);
    }
}
