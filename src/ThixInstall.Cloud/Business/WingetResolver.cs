using System.Net;
using System.Text.Json;
using ThixInstall.Shared;
using YamlDotNet.RepresentationModel;

namespace ThixInstall.Cloud.Business;

/// <summary>What a source resolved to for one system and architecture (stored as a <see cref="Data.PackageRelease"/>).</summary>
public sealed record ResolvedRelease(
    InstallMethod Method,
    string? Version,
    string? InstallerUrl = null,
    string? Sha256 = null,
    InstallerKind? Kind = null,
    string? SilentArguments = null,
    string? ProductCode = null,
    string? AppBundleName = null,
    string? FileName = null,
    string? Note = null);

/// <summary>
/// winget: the manifests of <c>microsoft/winget-pkgs</c> on GitHub. The folder of a package id ("Mozilla.Firefox" → manifests/m/Mozilla/Firefox)
/// holds one folder per version (and folders of other packages, "ESR", "de" …, which do not start with a digit); the newest version's installer
/// manifest names the installers per architecture, scope and language. ThixInstall installs them itself with the silent switches winget would use;
/// what it cannot run silently (zip, portable, an exe without silent switch) gets a plan without download, and the device calls winget.
/// </summary>
public static class WingetResolver
{
    public const string Repository = "microsoft/winget-pkgs";

    /// <summary>The folder of a package id below the repository: manifests/&lt;first letter&gt;/&lt;part&gt;/&lt;part&gt;….</summary>
    public static string? ManifestPath(string? packageId)
    {
        var id = (packageId ?? string.Empty).Trim();
        if (id.Length == 0 || id.Contains("..", StringComparison.Ordinal) || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+')))
        {
            return null;
        }

        var parts = id.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? null : $"manifests/{char.ToLowerInvariant(id[0])}/{string.Join('/', parts)}";
    }

    /// <summary>The newest version among the folder names (only names that start with a digit are versions).</summary>
    public static string? NewestVersion(IEnumerable<string> folderNames)
        => folderNames.Where(n => n.Length > 0 && char.IsAsciiDigit(n[0]))
            .OrderByDescending(n => n, Comparer<string>.Create(AppVersion.Compare))
            .FirstOrDefault();

    /// <summary>Resolves a package id for both architectures of Windows.</summary>
    public static async Task<IReadOnlyDictionary<CpuArchitecture, ResolvedRelease>> ResolveAsync(string packageId, string? gitHubToken, CancellationToken ct)
    {
        var path = ManifestPath(packageId) ?? throw new ResolveException($"„{packageId}“ ist keine gültige winget-Id (Herausgeber.Name).");
        var listing = await GitHubJsonAsync($"https://api.github.com/repos/{Repository}/contents/{path}?ref=master", gitHubToken, ct);
        var folders = listing.EnumerateArray()
            .Where(e => e.TryGetProperty("type", out var t) && t.GetString() == "dir")
            .Select(e => e.GetProperty("name").GetString() ?? string.Empty)
            .ToList();
        var version = NewestVersion(folders) ?? throw new ResolveException($"Für {packageId} gibt es im winget-Repository keine Version.");

        var yaml = await RawAsync($"https://raw.githubusercontent.com/{Repository}/master/{path}/{version}/{packageId}.installer.yaml", ct)
                   ?? await RawAsync($"https://raw.githubusercontent.com/{Repository}/master/{path}/{version}/{packageId}.yaml", ct)
                   ?? throw new ResolveException($"Das Installer-Manifest von {packageId} {version} fehlt.");

        var result = new Dictionary<CpuArchitecture, ResolvedRelease>();
        foreach (var architecture in new[] { CpuArchitecture.X64, CpuArchitecture.Arm64 })
        {
            result[architecture] = FromManifest(yaml, packageId, version, architecture);
        }

        return result;
    }

    /// <summary>The installer of an installer manifest for an architecture, as a release (pure, for tests).</summary>
    public static ResolvedRelease FromManifest(string yaml, string packageId, string fallbackVersion, CpuArchitecture architecture)
    {
        var root = Parse(yaml);
        var version = Scalar(root, "PackageVersion") ?? fallbackVersion;
        if (root.Children.TryGetValue(new YamlScalarNode("Installers"), out var node) is false || node is not YamlSequenceNode installers || installers.Children.Count == 0)
        {
            return new ResolvedRelease(InstallMethod.Winget, version, Note: "Das Manifest nennt keine Installer.");
        }

        var candidates = installers.Children.OfType<YamlMappingNode>()
            .Select(i => new Candidate(i, root))
            .Where(c => ArchitectureRank(c.Architecture, architecture) < 99)
            .OrderBy(c => ArchitectureRank(c.Architecture, architecture))
            .ThenBy(c => ScopeRank(c.Scope))
            .ThenBy(c => LocaleRank(c.Locale))
            .ToList();
        if (candidates.Count == 0)
        {
            return new ResolvedRelease(InstallMethod.Winget, version, Note: $"Kein Installer für {ApiContract.ToQuery(architecture)}.");
        }

        // the best installer that ThixInstall can run silently; when there is none, the device calls winget with the id
        foreach (var candidate in candidates)
        {
            if (Map(candidate) is { } mapped && Text.IsHttpUrl(candidate.Url))
            {
                var fileName = Text.FileNameOf(candidate.Url) ?? $"{packageId}-{version}";
                var extension = mapped.Kind switch { InstallerKind.Msi => ".msi", InstallerKind.Msix => ".msix", _ => ".exe" };
                if (!fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    fileName += extension;
                }

                return new ResolvedRelease(InstallMethod.Winget, version, candidate.Url, candidate.Sha256?.ToLowerInvariant(), mapped.Kind, mapped.Arguments,
                    candidate.ProductCode, FileName: fileName);
            }
        }

        return new ResolvedRelease(InstallMethod.Winget, version, Note: $"Installer-Art „{candidates[0].Type}“: wird mit winget installiert.");
    }

    /// <summary>The silent switches winget uses for an installer type (the manifest's "Silent" replaces them, "Custom" is added).</summary>
    public static (InstallerKind Kind, string Arguments)? Map(string type, string? silent, string? custom, string? scope)
    {
        InstallerKind? kind;
        string? defaults;
        switch (type.ToLowerInvariant())
        {
            case "msi" or "wix":
                (kind, defaults) = (InstallerKind.Msi, "/qn /norestart");
                break;
            case "nullsoft":
                (kind, defaults) = (InstallerKind.Exe, "/S");
                break;
            case "inno":
                (kind, defaults) = (InstallerKind.Exe, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-" + (scope == "machine" ? " /ALLUSERS" : string.Empty));
                break;
            case "burn":
                (kind, defaults) = (InstallerKind.Exe, "/quiet /norestart");
                break;
            case "exe":
                (kind, defaults) = (InstallerKind.Exe, (string?)null);
                break;
            case "msix" or "appx":
                (kind, defaults) = (InstallerKind.Msix, string.Empty);
                break;
            default:
                return null;
        }

        var arguments = !string.IsNullOrWhiteSpace(silent) ? silent.Trim() : defaults;
        if (arguments is null)
        {
            // an exe without a silent switch would open its wizard
            return null;
        }

        if (!string.IsNullOrWhiteSpace(custom))
        {
            arguments = (arguments + " " + custom.Trim()).Trim();
        }

        return (kind!.Value, arguments);
    }

    private static (InstallerKind Kind, string Arguments)? Map(Candidate c)
        => c.Type is null || c.Type is "zip" or "portable" or "pwa" ? null : Map(c.Type, c.Silent, c.Custom, c.Scope);

    private static int ArchitectureRank(string? installer, CpuArchitecture wanted) => (wanted, installer?.ToLowerInvariant()) switch
    {
        (CpuArchitecture.X64, "x64") => 0,
        (CpuArchitecture.X64, "x86") => 1,
        (CpuArchitecture.X64, "neutral" or null) => 2,
        (CpuArchitecture.Arm64, "arm64") => 0,
        (CpuArchitecture.Arm64, "x64") => 1,
        (CpuArchitecture.Arm64, "x86") => 2,
        (CpuArchitecture.Arm64, "neutral" or null) => 3,
        _ => 99,
    };

    private static int ScopeRank(string? scope) => scope switch { "machine" => 0, null => 1, _ => 2 };

    private static int LocaleRank(string? locale) => locale switch { null => 0, "en-US" => 1, "de-DE" => 2, _ => 3 };

    private static YamlMappingNode Parse(string yaml)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            return stream.Documents.FirstOrDefault()?.RootNode as YamlMappingNode ?? throw new ResolveException("Das Manifest ist leer.");
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new ResolveException("Das Manifest ist kein gültiges YAML: " + ex.Message);
        }
    }

    internal static string? Scalar(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value)
            ? scalar.Value!.Trim()
            : null;

    private static YamlMappingNode? Mapping(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value as YamlMappingNode : null;

    /// <summary>One installer of the manifest; a value it lacks comes from the root of the manifest (winget's rule).</summary>
    private sealed class Candidate(YamlMappingNode installer, YamlMappingNode root)
    {
        public string? Architecture { get; } = Scalar(installer, "Architecture");

        public string? Type { get; } = (Scalar(installer, "InstallerType") ?? Scalar(root, "InstallerType"))?.ToLowerInvariant();

        public string? Scope { get; } = (Scalar(installer, "Scope") ?? Scalar(root, "Scope"))?.ToLowerInvariant();

        public string? Locale { get; } = Scalar(installer, "InstallerLocale") ?? Scalar(root, "InstallerLocale");

        public string? Url { get; } = Scalar(installer, "InstallerUrl");

        public string? Sha256 { get; } = Scalar(installer, "InstallerSha256");

        public string? ProductCode { get; } = Scalar(installer, "ProductCode") ?? Scalar(root, "ProductCode");

        public string? Silent { get; } = Switch(installer, root, "Silent");

        public string? Custom { get; } = Switch(installer, root, "Custom");

        private static string? Switch(YamlMappingNode installer, YamlMappingNode root, string key)
            => (Mapping(installer, "InstallerSwitches") is { } own ? Scalar(own, key) : null) ?? (Mapping(root, "InstallerSwitches") is { } shared ? Scalar(shared, key) : null);
    }

    private static async Task<JsonElement> GitHubJsonAsync(string url, string? token, CancellationToken ct)
    {
        using var response = await Net.GetAsync(url, TimeSpan.FromSeconds(30), ct, request =>
        {
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }
        });
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new ResolveException("Diese winget-Id gibt es im winget-Repository nicht.");
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            throw new ResolveException("GitHub begrenzt gerade die Anfragen. Ein GitHub-Token unter Einstellungen › Quellen hebt die Grenze an.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ResolveException($"GitHub antwortet mit {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.Clone();
    }

    private static async Task<string?> RawAsync(string url, CancellationToken ct)
    {
        using var response = await Net.GetAsync(url, TimeSpan.FromSeconds(30), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ResolveException($"GitHub antwortet mit {(int)response.StatusCode}.");
        }

        return await response.Content.ReadAsStringAsync(ct);
    }
}

/// <summary>A source could not be resolved; the message is for the person (German).</summary>
public sealed class ResolveException(string message) : Exception(message);
