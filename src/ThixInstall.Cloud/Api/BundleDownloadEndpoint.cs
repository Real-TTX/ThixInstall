using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Data;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Api;

/// <summary>
/// <c>/b/{token}/download?platform=win-x64|osx-arm64|osx-x64</c>: the program of a bundle. Windows: the ThixInstall setup and the ticket appended to it
/// (an older ticket of the file is left out); macOS: a zip of the app with the ticket next to it. The ticket names the address the browser used, so the
/// program comes back to this cloud (behind a reverse proxy the forwarded host and scheme count).
/// </summary>
public static class BundleDownloadEndpoint
{
    public static void MapBundleDownload(this WebApplication app)
    {
        app.MapGet("/b/{token}/download", async (HttpContext http, AppDbContext db, string token, string? platform) =>
        {
            var key = platform ?? "win-x64";
            var cloudUrl = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
            var outcome = await BundleService.DownloadAsync(db, token, key, cloudUrl, http.RequestAborted);
            if (outcome.NotFound)
            {
                return Results.Redirect("/Error?code=404");
            }

            if (!outcome.Ok)
            {
                return Results.Redirect(ApiContract.BundlePage(token) + "?error=" + Uri.EscapeDataString(outcome.Error!));
            }

            var download = outcome.Value!;
            http.Response.Headers.CacheControl = "no-store";
            if (download.Trailer is null)
            {
                return new TempFileResult(download.Path, download.FileName, download.ContentType, download.DeleteAfterSend);
            }

            return new TrailerFileResult(download.Path, download.FileName, download.ContentType, download.Trailer);
        }).AllowAnonymous();
    }

    /// <summary>The program without the ticket it may already carry, then the new ticket.</summary>
    private sealed class TrailerFileResult(string path, string fileName, string contentType, byte[] trailer) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
            var length = BundleTicket.ProgramLength(file);
            file.Seek(0, SeekOrigin.Begin);
            http.Response.ContentType = contentType;
            http.Response.ContentLength = length + trailer.Length;
            http.Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { FileName = fileName }.ToString();
            var buffer = new byte[128 * 1024];
            var left = length;
            while (left > 0)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), http.RequestAborted);
                if (read == 0)
                {
                    break;
                }

                await http.Response.Body.WriteAsync(buffer.AsMemory(0, read), http.RequestAborted);
                left -= read;
            }

            await http.Response.Body.WriteAsync(trailer, http.RequestAborted);
        }
    }

    /// <summary>A file built for this one download (the zip of a Mac bundle): sent, then deleted.</summary>
    private sealed class TempFileResult(string path, string fileName, string contentType, bool delete) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            try
            {
                await Results.File(path, contentType, fileName).ExecuteAsync(http);
            }
            finally
            {
                if (delete)
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (IOException)
                    {
                        // the temporary folder is cleaned by the system
                    }
                }
            }
        }
    }
}
