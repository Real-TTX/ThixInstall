using System.Collections.Concurrent;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ThixInstall.Core.Infrastructure;

namespace ThixInstall;

/// <summary>
/// The icons of catalog apps: loaded once from the cloud (<c>/api/v1/packages/{id}/icon</c>) and kept in the app's data
/// folder; an app without one (or an SVG, which Avalonia does not draw from a stream) gets a coloured letter.
/// </summary>
internal static class PackageIcons
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> Loading = new();

    /// <summary>Dark enough for white letters in light and dark mode (4.5:1 or more).</summary>
    private static readonly Color[] Palette =
    [
        Color.Parse("#0F7B6C"), Color.Parse("#2563EB"), Color.Parse("#7C3AED"), Color.Parse("#C2410C"),
        Color.Parse("#BE185D"), Color.Parse("#15803D"), Color.Parse("#475569"), Color.Parse("#B45309"),
        Color.Parse("#0369A1"), Color.Parse("#9333EA"),
    ];

    static PackageIcons()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"ThixInstall/{AppInfo.BuildNumber}");
    }

    public static IBrush LetterBrush(string key)
    {
        var hash = 0;
        foreach (var character in key)
        {
            hash = unchecked(hash * 31 + character);
        }

        return new SolidColorBrush(Palette[(int)((uint)hash % Palette.Length)]);
    }

    public static string Letter(string name) =>
        name.FirstOrDefault(char.IsLetterOrDigit) is var first and not '\0' ? char.ToUpperInvariant(first).ToString() : "?";

    /// <summary>The icon of a package, or null (no cloud, no icon, not decodable).</summary>
    public static Task<Bitmap?> LoadAsync(string? serverUrl, long packageId)
    {
        if (string.IsNullOrEmpty(serverUrl))
        {
            return Task.FromResult<Bitmap?>(null);
        }

        var key = $"{serverUrl}|{packageId}";
        return Loading.GetOrAdd(key, _ => Task.Run(() => FetchAsync(serverUrl, packageId)));
    }

    private static async Task<Bitmap?> FetchAsync(string serverUrl, long packageId)
    {
        var folder = Path.Combine(AppPaths.DataDirectory, "icons");
        var file = Path.Combine(folder, $"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(serverUrl)))[..12]}-{packageId}");
        try
        {
            if (!File.Exists(file) || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(7))
            {
                using var response = await Http.GetAsync($"{serverUrl.TrimEnd('/')}{Shared.ApiContract.PackageIcon(packageId)}");
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var type = response.Content.Headers.ContentType?.MediaType ?? "";
                if (type.Contains("svg", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                Directory.CreateDirectory(folder);
                await File.WriteAllBytesAsync(file, await response.Content.ReadAsByteArrayAsync());
            }

            await using var stream = File.OpenRead(file);
            return Bitmap.DecodeToWidth(stream, 96, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException or ArgumentException or InvalidOperationException
                                              or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
        catch (Exception exception) when (exception.GetType().Name.Contains("Decoding", StringComparison.Ordinal))
        {
            return null;
        }
    }
}
