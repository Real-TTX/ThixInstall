using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ThixInstall.Cloud.Data;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Business;

/// <summary>An installer an administrator uploads (the file of the form, read once).</summary>
public sealed record UploadFile(Stream Content, string FileName, long Length);

/// <summary>An uploaded installer as it is stored.</summary>
public sealed record StoredUpload(string Sha256, string FileName, long Size, InstallerKind Kind);

/// <summary>
/// The installers administrators upload for apps that are in no repository (<see cref="InstallMethod.Upload"/>): Windows MSI, EXE, MSIX; macOS
/// DMG, PKG, ZIP. They live in <c>/data/uploads/&lt;sha256&gt;</c> – not in the download cache, whose clean-up would remove them – and are sent
/// like a cached file (the release names them as <c>upload:&lt;sha256&gt;</c>). A file goes when no app uses it any more.
/// </summary>
public static class PackageUploads
{
    /// <summary>The largest installer (2 GB).</summary>
    public const long MaxBytes = 2L * 1024 * 1024 * 1024;

    public const string UrlPrefix = "upload:";

    public static readonly IReadOnlyList<string> WindowsExtensions = [".msi", ".exe", ".msix", ".msixbundle", ".appx", ".appxbundle"];

    public static readonly IReadOnlyList<string> MacExtensions = [".dmg", ".pkg", ".zip"];

    private static string _directory = "/data/uploads";

    public static string Directory => _directory;

    public static void UseDirectory(string directory)
    {
        _directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public static string PathOf(string sha256) => Path.Combine(_directory, sha256);

    public static string UrlOf(string sha256) => UrlPrefix + sha256;

    public static bool IsUpload(string? installerUrl) => installerUrl?.StartsWith(UrlPrefix, StringComparison.Ordinal) ?? false;

    /// <summary>The SHA-256 named by a release's address "upload:&lt;sha256&gt;"; null for any other address.</summary>
    public static string? ShaOf(string? installerUrl)
        => IsUpload(installerUrl) && Text.IsSha256(installerUrl![UrlPrefix.Length..]) ? installerUrl[UrlPrefix.Length..] : null;

    /// <summary>What an installer is by its file name (null: not one ThixInstall installs on that system).</summary>
    public static InstallerKind? KindOf(string? fileName, TargetOs os)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return os == TargetOs.Windows
            ? extension switch
            {
                ".msi" => InstallerKind.Msi,
                ".exe" => InstallerKind.Exe,
                ".msix" or ".msixbundle" or ".appx" or ".appxbundle" => InstallerKind.Msix,
                _ => null,
            }
            : extension switch
            {
                ".dmg" => InstallerKind.Dmg,
                ".pkg" => InstallerKind.Pkg,
                ".zip" => InstallerKind.Zip,
                _ => null,
            };
    }

    /// <summary>
    /// Stores an uploaded installer: the type by its name and its first (or last) bytes, the SHA-256 while it is written. The field of the error
    /// is <paramref name="field"/>.
    /// </summary>
    public static async Task<Outcome<StoredUpload>> SaveAsync(UploadFile upload, TargetOs os, string field, CancellationToken ct = default)
    {
        var name = Text.Clean(Path.GetFileName(upload.FileName), 200);
        var kind = KindOf(name, os);
        if (name is null || kind is null)
        {
            return Outcome<StoredUpload>.Fail(os == TargetOs.Windows
                ? "Bitte eine .msi, .exe oder .msix hochladen."
                : "Bitte eine .dmg, .pkg oder .zip hochladen.", field);
        }

        if (upload.Length <= 0 || upload.Length > MaxBytes)
        {
            return Outcome<StoredUpload>.Fail("Die Datei ist leer oder größer als 2 GB.", field);
        }

        System.IO.Directory.CreateDirectory(_directory);
        var temp = Path.Combine(_directory, "tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            string sha;
            long size = 0;
            await using (var target = File.Create(temp))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await upload.Content.ReadAsync(buffer, ct)) > 0)
                {
                    size += read;
                    if (size > MaxBytes)
                    {
                        return Outcome<StoredUpload>.Fail("Die Datei ist größer als 2 GB.", field);
                    }

                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            if (!await LooksLikeAsync(temp, kind.Value, name, ct))
            {
                return Outcome<StoredUpload>.Fail($"Die Datei ist keine echte {Path.GetExtension(name).TrimStart('.').ToUpperInvariant()}-Datei.", field);
            }

            var path = PathOf(sha);
            if (File.Exists(path))
            {
                File.Delete(temp);
            }
            else
            {
                File.Move(temp, path);
            }

            return Outcome<StoredUpload>.Success(new StoredUpload(sha, name, size, kind.Value));
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>Deletes the file of an upload when no app uses it any more.</summary>
    public static async Task DeleteIfUnusedAsync(AppDbContext db, string? sha256, CancellationToken ct = default)
    {
        if (!Text.IsSha256(sha256))
        {
            return;
        }

        // also the deleted apps (their rows stay for the history) do not need it
        var used = await db.Packages.AnyAsync(p => p.WindowsUploadSha256 == sha256 || p.MacUploadSha256 == sha256, ct);
        if (!used)
        {
            try
            {
                File.Delete(PathOf(sha256!));
            }
            catch (IOException)
            {
                // sent right now: the file stays until the next clean-up of the same file
            }
        }
    }

    /// <summary>The file of an upload, ready to be sent.</summary>
    public static CachedFile? Find(string sha256, string? fileName)
    {
        var path = PathOf(sha256);
        if (!File.Exists(path))
        {
            return null;
        }

        var name = fileName ?? sha256;
        return new CachedFile(path, name, "application/octet-stream", new FileInfo(path).Length, sha256);
    }

    /// <summary>The first bytes (or, for a disk image, the last 512) say what a file really is.</summary>
    private static async Task<bool> LooksLikeAsync(string path, InstallerKind kind, string name, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        var head = new byte[8];
        var read = await file.ReadAsync(head, ct);
        if (read < 4)
        {
            return false;
        }

        switch (kind)
        {
            case InstallerKind.Msi:
                return head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 && head[3] == 0xE0;
            case InstallerKind.Exe:
                return head[0] == 'M' && head[1] == 'Z';
            case InstallerKind.Msix or InstallerKind.Zip:
                return head[0] == 'P' && head[1] == 'K';
            case InstallerKind.Pkg:
                return head[0] == 'x' && head[1] == 'a' && head[2] == 'r' && head[3] == '!';
            case InstallerKind.Dmg:
                if (file.Length < 512)
                {
                    return false;
                }

                // the UDIF trailer of a disk image starts with "koly"
                var trailer = new byte[4];
                file.Seek(-512, SeekOrigin.End);
                await file.ReadExactlyAsync(trailer, ct);
                return trailer[0] == 'k' && trailer[1] == 'o' && trailer[2] == 'l' && trailer[3] == 'y';
            default:
                return false;
        }
    }
}
