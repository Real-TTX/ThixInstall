using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ThixInstall.Shared;

namespace ThixInstall.Tests;

/// <summary>
/// ThixInstall.Cloud in memory, as far as a device sees it: enrollment, the device, the catalog, the inventory and the
/// actions. Tests push actions with <see cref="Push"/> and read what the device reported.
/// </summary>
internal sealed class FakeCloud : HttpMessageHandler
{
    public const string Url = "https://cloud.test";
    private const string Token = "device-token-1";

    private readonly object _gate = new();
    private readonly List<ActionDto> _actions = [];
    private long _revision;

    public CatalogResponse Catalog { get; set; } = SampleCatalog.Create();

    public bool SyncEnabled { get; private set; }

    public string? ProfileName { get; set; }

    public DeviceFacts? Facts { get; private set; }

    public InventoryReport? Inventory { get; private set; }

    public ConcurrentDictionary<long, ActionResult> Finished { get; } = new();

    public ConcurrentBag<LocalRunReport> Reports { get; } = [];

    public bool LoggedOut { get; private set; }

    public int CatalogRequests { get; private set; }

    public void Push(ActionKind kind, PackageDto? package = null, ScriptDto? script = null)
    {
        lock (_gate)
        {
            _actions.Add(new ActionDto
            {
                Id = 100 + _actions.Count,
                Kind = kind,
                State = ActionState.Pending,
                Origin = ActionOrigin.Push,
                Title = $"{kind} {package?.Name ?? script?.Name}",
                Package = package,
                Script = script,
                CreatedAt = DateTimeOffset.Now,
                RequestedBy = "Admin",
                Revision = ++_revision,
            });
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var authorized = request.Headers.Authorization?.Parameter == Token;
        if (path == ApiContract.Enroll && request.Method == HttpMethod.Post)
        {
            var enroll = (await request.Content!.ReadFromJsonAsync<EnrollRequest>(ApiContract.Json, cancellationToken))!;
            if (enroll.EnrollmentKey is null && enroll.Password != "geheim")
            {
                return Json(HttpStatusCode.Unauthorized, new ApiError("Benutzername oder Passwort stimmt nicht.", "Password"));
            }

            Facts = enroll.Device;
            SyncEnabled = enroll.EnableSync;
            ProfileName = enroll.EnrollmentKey is null ? ProfileName : "Büro";
            return Json(HttpStatusCode.OK, new EnrollResponse(Token, Info()));
        }

        if (path == ApiContract.Catalog)
        {
            CatalogRequests++;
            return Json(HttpStatusCode.OK, Catalog);
        }

        if (!authorized)
        {
            return Json(HttpStatusCode.Unauthorized, new ApiError("Unbekanntes Gerät."));
        }

        switch (path)
        {
            case ApiContract.DeviceLogout:
                LoggedOut = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            case ApiContract.Device when request.Method == HttpMethod.Put:
                var update = (await request.Content!.ReadFromJsonAsync<DeviceUpdate>(ApiContract.Json, cancellationToken))!;
                SyncEnabled = update.SyncEnabled ?? SyncEnabled;
                Facts = update.Facts ?? Facts;
                return Json(HttpStatusCode.OK, Info());
            case ApiContract.Device:
                return Json(HttpStatusCode.OK, Info());
            case ApiContract.Inventory:
                Inventory = await request.Content!.ReadFromJsonAsync<InventoryReport>(ApiContract.Json, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            case ApiContract.ActionsWait:
                await Task.Delay(50, cancellationToken);
                lock (_gate)
                {
                    return Json(HttpStatusCode.OK, new WaitResult(_revision));
                }

            case ApiContract.Actions:
                lock (_gate)
                {
                    return Json(HttpStatusCode.OK, new ActionsResponse(_actions.Where(action => action.State == ActionState.Pending).ToList(), _revision));
                }

            case ApiContract.ActionReport:
                Reports.Add((await request.Content!.ReadFromJsonAsync<LocalRunReport>(ApiContract.Json, cancellationToken))!);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["api", "v1", "device", "actions", var id, var verb] && long.TryParse(id, out var actionId))
        {
            lock (_gate)
            {
                var index = _actions.FindIndex(action => action.Id == actionId);
                if (index < 0)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                _actions[index] = _actions[index] with { State = verb == "start" ? ActionState.Running : ActionState.Succeeded };
            }

            if (verb == "finish")
            {
                Finished[actionId] = (await request.Content!.ReadFromJsonAsync<ActionResult>(ApiContract.Json, cancellationToken))!;
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private DeviceInfo Info() => new(1, Facts?.Name ?? "Gerät", "anna", "Anna Albers", ProfileName, SyncEnabled, "local-test");

    private static HttpResponseMessage Json<T>(HttpStatusCode status, T body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body, ApiContract.Json), System.Text.Encoding.UTF8, "application/json"),
    };
}
