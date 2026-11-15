using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ThixInstall.Shared;

namespace ThixInstall.Core.Infrastructure;

/// <summary>The cloud refused a request; <see cref="StatusCode"/> 401 means the device is no longer signed in.</summary>
internal sealed class CloudException(HttpStatusCode statusCode, string message, string? field = null) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string? Field { get; } = field;

    public bool IsUnauthorized => StatusCode == HttpStatusCode.Unauthorized;
}

/// <summary>
/// HTTP calls of the protocol (<see cref="ApiContract"/>). One instance per server and token (or none: the public routes).
/// Only the agent's business classes use it.
/// </summary>
internal sealed class CloudClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromHours(2);

    private readonly HttpClient _http;

    public CloudClient(string serverUrl, string? token, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        ServerUrl = NormalizeUrl(serverUrl);
        _http.BaseAddress = new Uri(ServerUrl + "/");
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ThixInstall/{AppInfo.BuildNumber}");
        _http.DefaultRequestHeaders.Add(ApiContract.ProtocolHeader, ApiContract.ProtocolVersion.ToString());
        if (token is not null)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public string ServerUrl { get; }

    /// <summary>Tests talk to an in-memory server.</summary>
    internal static Func<HttpMessageHandler>? HandlerForTests { get; set; }

    public static CloudClient Create(string serverUrl, string? token = null) => new(serverUrl, token, HandlerForTests?.Invoke());

    /// <summary>"install.example.com" becomes "https://install.example.com"; a trailing slash goes.</summary>
    public static string NormalizeUrl(string url)
    {
        var text = url.Trim().TrimEnd('/');
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        return text;
    }

    public Task<CatalogResponse> GetCatalogAsync(TargetOs os, CpuArchitecture architecture, CancellationToken cancellationToken) =>
        SendAsync<CatalogResponse>(HttpMethod.Get, $"{ApiContract.Catalog}?os={ApiContract.ToQuery(os)}&arch={ApiContract.ToQuery(architecture)}", null, RequestTimeout, cancellationToken);

    public Task<BundleDto> GetBundleAsync(string token, CpuArchitecture architecture, CancellationToken cancellationToken) =>
        SendAsync<BundleDto>(HttpMethod.Get, $"{ApiContract.Bundle(token)}?arch={ApiContract.ToQuery(architecture)}", null, RequestTimeout, cancellationToken);

    public Task<EnrollResponse> EnrollAsync(EnrollRequest request, CancellationToken cancellationToken) =>
        SendAsync<EnrollResponse>(HttpMethod.Post, ApiContract.Enroll, Json(request), RequestTimeout, cancellationToken);

    public Task LogoutAsync(CancellationToken cancellationToken) =>
        SendAsync<object>(HttpMethod.Post, ApiContract.DeviceLogout, null, RequestTimeout, cancellationToken, expectBody: false);

    public Task<DeviceInfo> GetDeviceAsync(CancellationToken cancellationToken) =>
        SendAsync<DeviceInfo>(HttpMethod.Get, ApiContract.Device, null, RequestTimeout, cancellationToken);

    public Task<DeviceInfo> UpdateDeviceAsync(DeviceUpdate update, CancellationToken cancellationToken) =>
        SendAsync<DeviceInfo>(HttpMethod.Put, ApiContract.Device, Json(update), RequestTimeout, cancellationToken);

    public Task PutInventoryAsync(InventoryReport report, CancellationToken cancellationToken) =>
        SendAsync<object>(HttpMethod.Put, ApiContract.Inventory, Json(report), RequestTimeout, cancellationToken, expectBody: false);

    public Task<WaitResult> WaitForActionsAsync(long since, int timeoutSeconds, CancellationToken cancellationToken) =>
        SendAsync<WaitResult>(HttpMethod.Get, $"{ApiContract.ActionsWait}?since={since}&timeout={timeoutSeconds}", null,
            TimeSpan.FromSeconds(timeoutSeconds + 20), cancellationToken);

    public Task<ActionsResponse> GetActionsAsync(CancellationToken cancellationToken) =>
        SendAsync<ActionsResponse>(HttpMethod.Get, ApiContract.Actions, null, RequestTimeout, cancellationToken);

    public Task StartActionAsync(long actionId, CancellationToken cancellationToken) =>
        SendAsync<object>(HttpMethod.Post, ApiContract.ActionStart(actionId), null, RequestTimeout, cancellationToken, expectBody: false);

    public Task FinishActionAsync(long actionId, ActionResult result, CancellationToken cancellationToken) =>
        SendAsync<object>(HttpMethod.Post, ApiContract.ActionFinish(actionId), Json(result), RequestTimeout, cancellationToken, expectBody: false);

    public Task ReportAsync(LocalRunReport report, CancellationToken cancellationToken) =>
        SendAsync<object>(HttpMethod.Post, ApiContract.ActionReport, Json(report), RequestTimeout, cancellationToken, expectBody: false);

    /// <summary>
    /// Downloads a file of the cloud (a cached installer, a ThixInstall package) into <paramref name="target"/> and checks
    /// its SHA-256 when one is known. <paramref name="pathOrUrl"/> is a path of the cloud or an absolute address.
    /// </summary>
    public async Task DownloadAsync(string pathOrUrl, string target, string? sha256, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TransferTimeout);
        var address = Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absolute) && absolute.Scheme.StartsWith("http", StringComparison.Ordinal)
            ? absolute
            : new Uri(_http.BaseAddress!, pathOrUrl.TrimStart('/'));
        using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        await ThrowIfFailedAsync(response, timeout.Token);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var total = response.Content.Headers.ContentLength ?? 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
        await using (var file = File.Create(target))
        {
            var buffer = new byte[128 * 1024];
            long received = 0;
            int read;
            var lastReport = 0.0;
            while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                hash.AppendData(buffer, 0, read);
                received += read;
                if (total > 0 && progress is not null)
                {
                    var fraction = (double)received / total;
                    if (fraction - lastReport >= 0.01 || received == total)
                    {
                        lastReport = fraction;
                        progress.Report(fraction);
                    }
                }
            }
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.IsNullOrWhiteSpace(sha256) && !string.Equals(actual, sha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(target);
            throw new CloudException(HttpStatusCode.OK, "Die heruntergeladene Datei ist beschädigt (Prüfsumme stimmt nicht).");
        }
    }

    public void Dispose() => _http.Dispose();

    private static JsonContent Json<T>(T value) => JsonContent.Create(value, options: ApiContract.Json);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? content, TimeSpan timeoutAfter,
        CancellationToken cancellationToken, bool expectBody = true)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutAfter);
        using var request = new HttpRequestMessage(method, path.TrimStart('/')) { Content = content };
        using var response = await _http.SendAsync(request, timeout.Token);
        await ThrowIfFailedAsync(response, timeout.Token);
        if (!expectBody)
        {
            return default!;
        }

        return await response.Content.ReadFromJsonAsync<T>(ApiContract.Json, timeout.Token)
               ?? throw new CloudException(response.StatusCode, "Die Cloud hat eine leere Antwort geschickt.");
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        ApiError? error = null;
        try
        {
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                error = await response.Content.ReadFromJsonAsync<ApiError>(ApiContract.Json, cancellationToken);
            }
        }
        catch (JsonException)
        {
        }

        var message = error?.Message ?? response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Anmeldung abgelehnt.",
            HttpStatusCode.Forbidden => "Keine Berechtigung.",
            HttpStatusCode.NotFound => "Nicht gefunden – ist das eine ThixInstall.Cloud?",
            HttpStatusCode.Conflict => "Der Auftrag wurde inzwischen abgebrochen.",
            HttpStatusCode.TooManyRequests => "Zu viele Versuche – bitte kurz warten.",
            _ => $"Die Cloud antwortet mit Fehler {(int)response.StatusCode}.",
        };
        throw new CloudException(response.StatusCode, message, error?.Field);
    }
}
