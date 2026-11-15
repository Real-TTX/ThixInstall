using System.Net.Http.Headers;

namespace ThixInstall.Cloud.Business;

/// <summary>
/// The HTTP client the business layer talks to the world with (GitHub, Homebrew, the sources of installers, homepages for icons). One client for
/// the life of the server; tests put a handler of their own in (<see cref="UseHandlerForTests"/>).
/// </summary>
public static class Net
{
    private static HttpClient _client = Create(null);

    public static HttpClient Client => _client;

    /// <summary>Requests to the world carry a neutral product name, nothing about the installation.</summary>
    public static string UserAgent => $"ThixInstall.Cloud/{AppInfo.Version}";

    public static void UseHandlerForTests(HttpMessageHandler? handler) => _client = Create(handler);

    private static HttpClient Create(HttpMessageHandler? handler)
    {
        var client = handler is null
            ? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
            : new HttpClient(handler, disposeHandler: false);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ThixInstall.Cloud", Sanitize(AppInfo.Version)));
        return client;
    }

    private static string Sanitize(string version)
    {
        var clean = new string(version.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
        return clean.Length == 0 ? "0" : clean;
    }

    /// <summary>A GET with a timeout; the caller disposes the response.</summary>
    public static async Task<HttpResponseMessage> GetAsync(string url, TimeSpan timeout, CancellationToken ct, Action<HttpRequestMessage>? configure = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        configure?.Invoke(request);
        return await Client.SendAsync(request, completion, timer.Token);
    }
}
