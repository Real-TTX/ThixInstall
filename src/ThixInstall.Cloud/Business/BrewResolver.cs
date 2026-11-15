using System.Net;
using System.Text.Json;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Business;

/// <summary>
/// Homebrew: the JSON API of formulae.brew.sh. A cask names its version, the download (URL and SHA-256) and what it installs (the .app, a .pkg);
/// its <c>variations</c> carry the downloads that differ for Intel Macs or older systems. ThixInstall installs a dmg or zip with one app and a
/// plain pkg itself (from the cloud's cache); everything else, and every formula, is installed with "brew".
/// </summary>
public static class BrewResolver
{
    public const string ApiBase = "https://formulae.brew.sh/api";

    /// <summary>macOS names from new to old: the Intel download of the newest system wins.</summary>
    private static readonly string[] SystemsNewestFirst = ["tahoe", "sequoia", "sonoma", "ventura", "monterey", "big_sur", "catalina", "mojave", "high_sierra"];

    public static bool IsValidName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Length <= 200 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '@' or '+' or '/');

    public static async Task<IReadOnlyDictionary<CpuArchitecture, ResolvedRelease>> ResolveAsync(string name, bool isCask, CancellationToken ct)
    {
        if (!IsValidName(name))
        {
            throw new ResolveException($"„{name}“ ist kein gültiger Homebrew-Name.");
        }

        var url = $"{ApiBase}/{(isCask ? "cask" : "formula")}/{Uri.EscapeDataString(name)}.json";
        using var response = await Net.GetAsync(url, TimeSpan.FromSeconds(30), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new ResolveException(isCask ? $"Den Cask „{name}“ gibt es bei Homebrew nicht." : $"Die Formel „{name}“ gibt es bei Homebrew nicht.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ResolveException($"formulae.brew.sh antwortet mit {(int)response.StatusCode}.");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        return new Dictionary<CpuArchitecture, ResolvedRelease>
        {
            [CpuArchitecture.Arm64] = isCask ? FromCask(json, name, CpuArchitecture.Arm64) : FromFormula(json, name),
            [CpuArchitecture.X64] = isCask ? FromCask(json, name, CpuArchitecture.X64) : FromFormula(json, name),
        };
    }

    /// <summary>A formula: only the version; "brew install" does the rest.</summary>
    public static ResolvedRelease FromFormula(string json, string name)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        var version = root.TryGetProperty("versions", out var versions) && versions.TryGetProperty("stable", out var stable) ? stable.GetString() : null;
        return new ResolvedRelease(InstallMethod.Homebrew, version);
    }

    /// <summary>A cask for an architecture (pure, for tests).</summary>
    public static ResolvedRelease FromCask(string json, string name, CpuArchitecture architecture)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        var version = String(root, "version");
        if (version is not null && version.Contains(',', StringComparison.Ordinal))
        {
            // "1.2.3,4567": the part before the comma is the version people know
            version = version.Split(',')[0];
        }

        var url = String(root, "url");
        var sha = String(root, "sha256");
        if (architecture == CpuArchitecture.X64 && root.TryGetProperty("variations", out var variations) && variations.ValueKind == JsonValueKind.Object)
        {
            foreach (var system in SystemsNewestFirst)
            {
                if (variations.TryGetProperty(system, out var variation) && String(variation, "url") is { } intelUrl)
                {
                    url = intelUrl;
                    sha = String(variation, "sha256") ?? sha;
                    break;
                }
            }
        }

        var apps = new List<string>();
        var hasPkg = false;
        var hasOther = false;
        var hasExtras = false;
        if (root.TryGetProperty("artifacts", out var artifacts) && artifacts.ValueKind == JsonValueKind.Array)
        {
            foreach (var artifact in artifacts.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object))
            {
                foreach (var property in artifact.EnumerateObject())
                {
                    switch (property.Name)
                    {
                        case "app":
                            apps.AddRange(property.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!));
                            break;
                        case "pkg":
                            hasPkg = true;
                            break;
                        case "uninstall" or "zap" or "postflight" or "preflight" or "uninstall_postflight" or "uninstall_preflight" or "target":
                            break;
                        case "binary" or "command_wrapper" or "manpage" or "bash_completion" or "zsh_completion" or "fish_completion":
                            // links into the app for the command line: a convenience of brew, the app works without them
                            hasExtras = true;
                            break;
                        default:
                            hasOther = true; // binary, suite, installer script …: brew knows how
                            break;
                    }
                }
            }
        }

        var shaValue = Text.IsSha256(sha) ? sha!.ToLowerInvariant() : null; // "no_check": the cloud keeps what it downloads
        var fileName = Text.FileNameOf(url);
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        var appName = apps.Count == 1 ? Path.GetFileName(apps[0]) : null;

        if (!Text.IsHttpUrl(url) || hasOther || (hasExtras && apps.Count == 0))
        {
            return new ResolvedRelease(InstallMethod.HomebrewCask, version, AppBundleName: appName, Note: "Wird mit brew installiert.");
        }

        if (extension == ".pkg" && apps.Count == 0)
        {
            return new ResolvedRelease(InstallMethod.HomebrewCask, version, url, shaValue, InstallerKind.Pkg, FileName: fileName);
        }

        if (appName is not null && !hasPkg && extension is ".dmg" or ".zip")
        {
            return new ResolvedRelease(InstallMethod.HomebrewCask, version, url, shaValue, extension == ".dmg" ? InstallerKind.Dmg : InstallerKind.Zip,
                AppBundleName: appName, FileName: fileName);
        }

        return new ResolvedRelease(InstallMethod.HomebrewCask, version, AppBundleName: appName, Note: "Wird mit brew installiert.");
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;

    private static JsonDocument Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ResolveException("Die Antwort von Homebrew ist kein gültiges JSON: " + ex.Message);
        }
    }
}
