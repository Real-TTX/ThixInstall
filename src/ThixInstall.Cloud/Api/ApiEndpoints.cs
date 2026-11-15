using System.Globalization;
using ThixInstall.Cloud.Business;
using ThixInstall.Cloud.Data;
using ThixInstall.Shared;

namespace ThixInstall.Cloud.Api;

/// <summary>
/// The API of the devices and of the bundle programs (<see cref="ApiContract"/>). Public routes: the catalog (anonymous: public items, only while the
/// setting allows it), icons, cached installers, bundles by their token, the enrollment, the desktop packages. Every other route needs the device token
/// as a bearer token. Answers are JSON (camelCase, enums as text); an error is an <see cref="ApiError"/> with a German message. Every answer carries the
/// protocol version (<see cref="ApiContract.ProtocolHeader"/>); a device that speaks a newer protocol than the server gets 400.
/// </summary>
public static class ApiEndpoints
{
    public const string DeviceItem = "ThixInstall.Device";

    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup(string.Empty).AllowAnonymous().DisableAntiforgery().AddEndpointFilter(ProtocolAsync);

        api.MapGet(ApiContract.Catalog, CatalogAsync);
        api.MapGet("/api/v1/packages/{id:long}/icon", IconAsync);
        api.MapGet("/api/v1/downloads/{id:long}", DownloadAsync);
        api.MapGet("/api/v1/bundles/{token}", async (HttpContext http, AppDbContext db, string token, string? arch) =>
        {
            var bundle = await BundleService.DtoAsync(db, token, ApiContract.ParseArchitecture(arch) ?? CpuArchitecture.X64, http.RequestAborted);
            return bundle is null ? Error(StatusCodes.Status404NotFound, "Dieses Paket gibt es nicht (mehr).") : Json(bundle);
        });
        api.MapPost(ApiContract.Enroll, EnrollAsync).RequireRateLimiting("signin");
        api.MapGet(ApiContract.AgentManifest, async (HttpContext http, AppDbContext db) =>
        {
            var manifest = await AgentService.ManifestAsync(db, http.RequestAborted);
            return manifest is null ? Error(StatusCodes.Status404NotFound, "Für den Kanal dieser Cloud gibt es noch keine Desktop-Version.") : Results.Json(manifest);
        });
        api.MapGet("/api/v1/agent/{platform}", async (HttpContext http, AppDbContext db, string platform) =>
        {
            var package = await AgentService.PackageAsync(db, platform, http.RequestAborted);
            if (package.NotFound)
            {
                return Error(StatusCodes.Status404NotFound, "Diese Plattform gibt es nicht.");
            }

            return package.Ok
                ? Results.File(package.Value!.Path, package.Value.ContentType, package.Value.FileName, enableRangeProcessing: true)
                : Error(StatusCodes.Status503ServiceUnavailable, package.Error!);
        });

        var device = api.MapGroup(string.Empty).AddEndpointFilter(DeviceAsync);
        device.MapPost(ApiContract.DeviceLogout, async (HttpContext http, AppDbContext db) =>
        {
            await DeviceService.LogoutAsync(db, DeviceOf(http), http.RequestAborted);
            return Results.NoContent();
        });
        device.MapGet(ApiContract.Device, async (HttpContext http, AppDbContext db) => Json(await DeviceService.InfoAsync(db, DeviceOf(http).DeviceId, http.RequestAborted)));
        device.MapPut(ApiContract.Device, async (HttpContext http, AppDbContext db, DeviceUpdate? update) =>
            update is null
                ? Error(StatusCodes.Status400BadRequest, "Die Anfrage ist leer.")
                : Answer(await DeviceService.UpdateSelfAsync(db, DeviceOf(http), update, http.RequestAborted)));
        device.MapPut(ApiContract.Inventory, async (HttpContext http, AppDbContext db, InventoryReport? report) =>
        {
            if (report is null)
            {
                return Error(StatusCodes.Status400BadRequest, "Das Inventar fehlt.");
            }

            var outcome = await DeviceService.ReportInventoryAsync(db, DeviceOf(http), report, http.RequestAborted);
            return outcome.Ok ? Results.NoContent() : Error(StatusCodes.Status409Conflict, outcome.Error!, outcome.Field);
        });
        device.MapGet(ApiContract.ActionsWait, WaitAsync);
        device.MapGet(ApiContract.Actions, async (HttpContext http, AppDbContext db) => Json(await ActionService.OpenAsync(db, DeviceOf(http), http.RequestAborted)));
        device.MapPost("/api/v1/device/actions/{id:long}/start", async (HttpContext http, AppDbContext db, long id) =>
        {
            var outcome = await ActionService.StartAsync(db, DeviceOf(http), id, http.RequestAborted);
            return outcome.NotFound ? Error(StatusCodes.Status404NotFound, "Diesen Auftrag gibt es nicht.")
                : outcome.Ok ? Results.NoContent() : Error(StatusCodes.Status409Conflict, outcome.Error!);
        });
        device.MapPost("/api/v1/device/actions/{id:long}/finish", async (HttpContext http, AppDbContext db, long id, ActionResult? result) =>
        {
            if (result is null)
            {
                return Error(StatusCodes.Status400BadRequest, "Das Ergebnis fehlt.");
            }

            var outcome = await ActionService.FinishAsync(db, DeviceOf(http), id, result, http.RequestAborted);
            return outcome.NotFound ? Error(StatusCodes.Status404NotFound, "Diesen Auftrag gibt es nicht.")
                : outcome.Ok ? Results.NoContent() : Error(StatusCodes.Status409Conflict, outcome.Error!);
        });
        device.MapPost(ApiContract.ActionReport, async (HttpContext http, AppDbContext db, LocalRunReport? report) =>
        {
            if (report is null)
            {
                return Error(StatusCodes.Status400BadRequest, "Der Bericht fehlt.");
            }

            var outcome = await ActionService.ReportAsync(db, DeviceOf(http), report, http.RequestAborted);
            return outcome.Ok ? Json(new { id = outcome.Value }) : Error(StatusCodes.Status400BadRequest, outcome.Error!, outcome.Field);
        });
    }

    public static IResult Error(int status, string message, string? field = null) => Results.Json(new ApiError(message, field), ApiContract.Json, statusCode: status);

    private static async Task<IResult> CatalogAsync(HttpContext http, AppDbContext db, string? os, string? arch)
    {
        var target = ApiContract.ParseOs(os);
        if (target is null)
        {
            return Error(StatusCodes.Status400BadRequest, "Bitte das System angeben: os=windows oder os=macos.", "os");
        }

        var architecture = ApiContract.ParseArchitecture(arch) ?? (target == TargetOs.MacOS ? CpuArchitecture.Arm64 : CpuArchitecture.X64);
        var device = await DeviceOfBearerAsync(http, db);
        if (device is null && !await CatalogService.IsPublicAsync(db, http.RequestAborted))
        {
            return Error(StatusCodes.Status401Unauthorized, "Der Katalog dieser Cloud ist nur für angemeldete Geräte.");
        }

        http.Response.Headers.CacheControl = "no-store";
        return Json(await CatalogService.CatalogAsync(db, device?.Actor, target.Value, architecture, http.RequestAborted));
    }

    private static async Task<IResult> IconAsync(HttpContext http, AppDbContext db, long id)
    {
        var icon = await CatalogService.IconAsync(db, id, http.RequestAborted);
        if (icon is null)
        {
            return Results.NotFound();
        }

        http.Response.Headers.CacheControl = "public, max-age=3600";
        if (icon.Value.ContentType == "image/svg+xml")
        {
            // an uploaded SVG is shown as a picture, never as a document of this site
            http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
        }

        return Results.File(icon.Value.Data, icon.Value.ContentType);
    }

    private static async Task<IResult> DownloadAsync(HttpContext http, AppDbContext db, long id)
    {
        var file = await DownloadCache.ReleaseAsync(db, id, http.RequestAborted);
        if (file.NotFound)
        {
            return Error(StatusCodes.Status404NotFound, "Diesen Installer gibt es nicht (mehr).");
        }

        if (!file.Ok)
        {
            return Error(StatusCodes.Status502BadGateway, file.Error!);
        }

        http.Response.Headers["X-Content-SHA256"] = file.Value!.Sha256;
        return Results.File(file.Value.Path, file.Value.ContentType, file.Value.FileName, enableRangeProcessing: true);
    }

    private static async Task<IResult> EnrollAsync(HttpContext http, AppDbContext db, EnrollRequest? request)
    {
        if (request is null)
        {
            return Error(StatusCodes.Status400BadRequest, "Die Anmeldung braucht die Angaben zum Gerät und Benutzername/Passwort, einen Registrierungsschlüssel oder ein Paket-Token.");
        }

        var outcome = await DeviceService.EnrollAsync(db, request, http.Connection.RemoteIpAddress?.ToString(), http.RequestAborted);
        if (outcome.Ok)
        {
            return Json(outcome.Value!);
        }

        // a wrong name, password, key or token is 401; a request the app has to fix is 400
        var status = outcome.Field == "password" ? StatusCodes.Status401Unauthorized : StatusCodes.Status400BadRequest;
        return Error(status, outcome.Error!, outcome.Field);
    }

    private static async Task<IResult> WaitAsync(HttpContext http, AppDbContext db, long? since, int? timeout)
    {
        var seconds = Math.Clamp(timeout ?? 25, 1, ApiContract.MaxWaitSeconds);
        try
        {
            var latest = await ActionService.WaitAsync(db, DeviceOf(http).DeviceId, since ?? 0, TimeSpan.FromSeconds(seconds), http.RequestAborted);
            return Json(new WaitResult(latest));
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }
    }

    /// <summary>Every answer names the protocol of the server; a device with a newer protocol is told to wait for a server update.</summary>
    private static async ValueTask<object?> ProtocolAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers[ApiContract.ProtocolHeader] = ApiContract.ProtocolVersion.ToString(CultureInfo.InvariantCulture);
        var asked = http.Request.Headers[ApiContract.ProtocolHeader].ToString();
        if (int.TryParse(asked, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) && version > ApiContract.ProtocolVersion)
        {
            return Error(StatusCodes.Status400BadRequest, "Diese Version von ThixInstall ist neuer als der Server. Bitte ThixInstall.Cloud aktualisieren.");
        }

        return await next(context);
    }

    /// <summary>The device of the bearer token, or 401.</summary>
    private static async ValueTask<object?> DeviceAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var device = await DeviceOfBearerAsync(http, db);
        if (device is null)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
            return Error(StatusCodes.Status401Unauthorized, "Dieses Gerät ist nicht (mehr) angemeldet. Bitte ThixInstall erneut mit der Cloud verbinden.");
        }

        http.Items[DeviceItem] = device;
        http.Items[AppDbContext.ActorItem] = device.UserId;
        await DeviceService.TouchAsync(db, device, http.Connection.RemoteIpAddress?.ToString(), AgentVersionOf(http.Request), http.RequestAborted);
        return await next(context);
    }

    private static async Task<DeviceIdentity?> DeviceOfBearerAsync(HttpContext http, AppDbContext db)
    {
        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
        return token is null ? null : await DeviceService.AuthenticateAsync(db, token, http.RequestAborted);
    }

    /// <summary>The version of the app from its user agent ("ThixInstall/0.1.42-20261114 (windows)").</summary>
    private static string? AgentVersionOf(HttpRequest request)
    {
        var agent = request.Headers.UserAgent.ToString();
        const string prefix = "ThixInstall/";
        if (!agent.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var version = agent[prefix.Length..];
        var end = version.IndexOf(' ');
        return end < 0 ? version : version[..end];
    }

    private static DeviceIdentity DeviceOf(HttpContext http) => (DeviceIdentity)http.Items[DeviceItem]!;

    private static IResult Json<T>(T value) => Results.Json(value, ApiContract.Json);

    private static IResult Answer<T>(Outcome<T> outcome)
    {
        if (outcome.NotFound)
        {
            return Error(StatusCodes.Status404NotFound, "Nicht gefunden.");
        }

        return outcome.Ok ? Json(outcome.Value) : Error(StatusCodes.Status400BadRequest, outcome.Error!, outcome.Field);
    }
}
