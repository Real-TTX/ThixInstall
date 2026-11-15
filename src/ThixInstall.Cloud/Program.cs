using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using ThixInstall.Cloud;
using ThixInstall.Cloud.Api;
using ThixInstall.Cloud.Data;
using ThixInstall.Cloud.Web;
using ThixInstall.Shared;

if (args.Contains("--healthcheck"))
{
    return await HealthCheck.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

// ---- the database (PostgreSQL): the default fits the compose files, so the container needs no variable; THIXINSTALL_DB overrides it ----
var connection = Environment.GetEnvironmentVariable("THIXINSTALL_DB")
    ?? builder.Configuration.GetConnectionString("Default")
    ?? "Host=db;Port=5432;Database=thixinstall;Username=thixinstall;Password=thixinstall";
builder.Services.AddHttpContextAccessor();
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));

// The keys that protect the cookie are in the volume: a session survives a restart of the container.
var dataDirectory = Path.GetFullPath(builder.Configuration["DataDirectory"] ?? "/data", builder.Environment.ContentRootPath);
var keys = Path.Combine(dataDirectory, "keys");
Directory.CreateDirectory(keys);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys)).SetApplicationName("ThixInstall.Cloud");

// German text stays text in the pages (not &#xFC; for ü): the encoder lets every letter through and still encodes what is markup.
builder.Services.AddWebEncoders(o => o.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(System.Text.Unicode.UnicodeRanges.All));
builder.Services.AddMemoryCache();
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");
builder.Services.AddSingleton<Assets>();
builder.Services.AddSingleton<SetupState>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddHostedService<MaintenanceService>();
builder.Services.AddHostedService<ResolverService>();

// The download cache and the uploaded desktop packages live in the volume too.
ThixInstall.Cloud.Business.DownloadCache.UseDirectory(Path.Combine(dataDirectory, "cache"));
ThixInstall.Cloud.Business.AgentService.UseDirectory(Path.Combine(dataDirectory, "agent"));

// The JSON of the API is the one of the contract (camelCase, enums as text, no nulls).
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = CookieSignIn.CookieName;
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.Cookie.IsEssential = true;
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Error?code=403";
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = CookieSignIn.ValidateAsync;
        o.Events.OnRedirectToLogin = context =>
        {
            // a script or an API call gets a status, not the sign-in page
            if (context.Request.Path.StartsWithSegments("/api")
                || context.Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            }
            else
            {
                context.Response.Redirect(context.RedirectUri);
            }

            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/Setup");
    options.Conventions.AllowAnonymousToPage("/Index");
    options.Conventions.AllowAnonymousToPage("/Bundles/Public");
    options.Conventions.AuthorizeFolder("/Catalog", "AdminOnly");
    options.Conventions.AuthorizeFolder("/Profiles", "AdminOnly");
    options.Conventions.AuthorizeFolder("/Admin", "AdminOnly");
}).AddMvcOptions(o =>
{
    o.ModelBinderProviders.Insert(0, new EmptyStringBinderProvider());
    o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
});

// The sign-in (browser and desktop): 10 attempts a minute per address. An API caller gets the error as JSON.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("signin", http => HttpMethods.IsGet(http.Request.Method)
        ? RateLimitPartition.GetNoLimiter("show")
        : RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.OnRejected = async (context, ct) =>
    {
        if (context.HttpContext.Request.Path.StartsWithSegments("/api"))
        {
            await ThixInstall.Cloud.Api.ApiEndpoints.Error(StatusCodes.Status429TooManyRequests, "Zu viele Anmeldeversuche. Bitte in einer Minute noch einmal versuchen.").ExecuteAsync(context.HttpContext);
        }
    };
});

// Uploads: the desktop packages of an administrator (up to 400 MB) and package icons.
var bodyLimit = ThixInstall.Cloud.Business.AgentService.MaxUploadBytes + 2 * 1024 * 1024;
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = bodyLimit);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = bodyLimit);

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // behind a reverse proxy (Caddy, nginx, Traefik) the address and the scheme of the visitor are in the headers
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

await Database.MigrateWithRetryAsync(app);

app.UseForwardedHeaders();
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Error");
}

app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.Value!.EndsWith("/download", StringComparison.Ordinal),
    branch => branch.UseStatusCodePagesWithReExecute("/Error", "?code={0}"));
app.UseMiddleware<SecurityHeaders>();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.Context.Request.Path;
        if (path == "/sw.js" || path == "/manifest.webmanifest")
        {
            // the service worker and the manifest are always asked again, so a new version is used at once
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
        else if (ctx.Context.Request.Query.ContainsKey("v"))
        {
            // files are versioned with ?v= (asp-append-version): they can be kept for a long time
            ctx.Context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        }
    },
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<SetupRedirect>();
app.UseMiddleware<ForcePasswordChange>();
app.UseAuthorization();

app.MapGet("/health", async (AppDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok("ok") : Results.StatusCode(503)).AllowAnonymous();
app.MapApi();
app.MapBundleDownload();
app.MapRazorPages();

app.Run();
return 0;

/// <summary>The build: <c>0.1.42-20261114</c> (release), <c>nightly-42-20261114</c> (development) or <c>local-20261114</c>.</summary>
public static class AppInfo
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "local";

    /// <summary>release, nightly or local (from the assembly metadata of Directory.Build.props).</summary>
    public static string Channel { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "ThixInstallChannel")?.Value ?? "local";
}

public partial class Program;
