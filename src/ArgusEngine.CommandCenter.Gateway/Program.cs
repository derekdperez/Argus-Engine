using System.Net.Http.Headers;
using System.Net.WebSockets;

var builder = WebApplication.CreateBuilder(args);

var serviceRoutes = GatewayServiceRoutes.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(serviceRoutes);

builder.Services.AddHttpClient(GatewayServiceRoutes.WebClientName, client => ConfigureClient(client, serviceRoutes.Web));
builder.Services.AddHttpClient(GatewayServiceRoutes.DiscoveryClientName, client => ConfigureClient(client, serviceRoutes.Discovery));
builder.Services.AddHttpClient(GatewayServiceRoutes.OperationsClientName, client => ConfigureClient(client, serviceRoutes.Operations));
builder.Services.AddHttpClient(GatewayServiceRoutes.WorkerControlClientName, client => ConfigureClient(client, serviceRoutes.WorkerControl));
builder.Services.AddHttpClient(GatewayServiceRoutes.MaintenanceClientName, client => ConfigureClient(client, serviceRoutes.Maintenance));
builder.Services.AddHttpClient(GatewayServiceRoutes.UpdatesClientName, client => ConfigureClient(client, serviceRoutes.Updates));
builder.Services.AddHttpClient(GatewayServiceRoutes.RealtimeClientName, client => ConfigureClient(client, serviceRoutes.Realtime));
builder.Services.AddHttpClient(GatewayServiceRoutes.CloudDeployClientName, client => ConfigureClient(client, serviceRoutes.CloudDeploy));

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
    .AllowAnonymous();

app.MapGet(
        "/health/ready",
        async (
            IHttpClientFactory httpClientFactory,
            GatewayServiceRoutes routes,
            CancellationToken cancellationToken) =>
            await CheckGatewayReadinessAsync(httpClientFactory, routes, cancellationToken).ConfigureAwait(false))
    .AllowAnonymous();

app.MapGet(
        "/api/gateway/routes",
        (GatewayServiceRoutes routes) => Results.Ok(
            new
            {
                gateway = "command-center-gateway",
                legacyFallback = false,
                routes = GatewayRouteDiagnostics.Routes,
                services = routes.AsDiagnosticObject()
            }))
    .AllowAnonymous();

app.Map("/{**path}", ForwardToSplitCommandCenterAsync).AllowAnonymous();

await app.RunAsync().ConfigureAwait(false);

static void ConfigureClient(HttpClient client, Uri baseAddress)
{
    client.BaseAddress = baseAddress;
    client.Timeout = TimeSpan.FromMinutes(5);
}

static async Task<IResult> CheckGatewayReadinessAsync(
    IHttpClientFactory httpClientFactory,
    GatewayServiceRoutes routes,
    CancellationToken cancellationToken)
{
    var targets = routes.HealthCheckTargets;
    var checks = await Task.WhenAll(
            targets.Select(target => CheckDownstreamAsync(httpClientFactory, target, cancellationToken)))
        .ConfigureAwait(false);

    var isReady = checks.All(check => check.Healthy);
    var payload = new
    {
        status = isReady ? "ready" : "unhealthy",
        mode = "split-command-center",
        legacyFallback = false,
        services = routes.AsDiagnosticObject(),
        dependencies = checks
    };

    return isReady
        ? Results.Ok(payload)
        : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
}

static async Task<DownstreamHealthResult> CheckDownstreamAsync(
    IHttpClientFactory httpClientFactory,
    DownstreamHealthTarget target,
    CancellationToken cancellationToken)
{
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

    try
    {
        using var response = await httpClientFactory
            .CreateClient(target.ClientName)
            .GetAsync("/health/live", timeoutCts.Token)
            .ConfigureAwait(false);

        return new DownstreamHealthResult(
            target.ClientName,
            target.BaseAddress,
            response.IsSuccessStatusCode,
            (int)response.StatusCode,
            response.ReasonPhrase ?? string.Empty);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
    {
        return new DownstreamHealthResult(
            target.ClientName,
            target.BaseAddress,
            false,
            null,
            ex.Message);
    }
}

static async Task<IResult> ForwardToSplitCommandCenterAsync(
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    GatewayServiceRoutes routes,
    CancellationToken cancellationToken)
{
    var selected = SelectClientName(context.Request.Path);

    if (selected is null)
    {
        return Results.NotFound(
            new
            {
                error = "No CommandCenter split-service route owns this path.",
                path = context.Request.Path.Value,
                legacyFallback = false
            });
    }

    if (context.WebSockets.IsWebSocketRequest)
    {
        await ForwardWebSocketAsync(context, routes.GetBaseAddress(selected), cancellationToken)
            .ConfigureAwait(false);

        return Results.Empty;
    }

    using var request = CreateForwardRequest(context);

    HttpResponseMessage response;
    try
    {
        response = await httpClientFactory
            .CreateClient(selected)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        return Results.Empty;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
    {
        return Results.Json(
            new
            {
                error = "Command Center gateway could not reach the selected downstream service.",
                service = selected,
                path = context.Request.Path.Value,
                detail = ex.Message
            },
            statusCode: StatusCodes.Status502BadGateway);
    }

    using (response)
    {
        context.Response.StatusCode = (int)response.StatusCode;
        CopyHeaders(response.Headers, context.Response.Headers);
        CopyHeaders(response.Content.Headers, context.Response.Headers);

        context.Response.Headers.Remove("transfer-encoding");

        await response.Content.CopyToAsync(context.Response.Body, cancellationToken)
            .ConfigureAwait(false);
    }

    return Results.Empty;
}

static string? SelectClientName(PathString path)
{
    if (path.StartsWithSegments("/api/cloud-deploy", StringComparison.OrdinalIgnoreCase))
    {
        return GatewayServiceRoutes.CloudDeployClientName;
    }

    if (path.StartsWithSegments("/api/rabbitmq", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/gcp-workers", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/workers", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/ec2-workers", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/ops/ecs-status", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/ops/spider/restart", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/ops/spider/continuous", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/ops/spider/subdomains/restart", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/ops/subdomain-enum/restart", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/ops/subdomain-enum/continuous", StringComparison.OrdinalIgnoreCase))
    {
        return GatewayServiceRoutes.WorkerControlClientName;
    }

    if (path.StartsWithSegments("/api/status", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/ops", StringComparison.OrdinalIgnoreCase))
    {
        return GatewayServiceRoutes.OperationsClientName;
    }

    if (path.StartsWithSegments("/api/targets", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/assets", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/asset-graph", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/tags", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/technologies", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/asset-admission-decisions", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/high-value-findings", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/high-value-assets", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/technology-identification", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/http-request-queue", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/filestore", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/events", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/discovery", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/recon-agent", StringComparison.OrdinalIgnoreCase))
    {
        return GatewayServiceRoutes.DiscoveryClientName;
    }

    if (path.StartsWithSegments("/api/admin", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/maintenance", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/diagnostics", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/bus", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/logs", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/worker-logs", StringComparison.OrdinalIgnoreCase))
    {
        return GatewayServiceRoutes.MaintenanceClientName;
    }

    if (path.StartsWithSegments("/api/development/components", StringComparison.OrdinalIgnoreCase))
    {
        return GatewayServiceRoutes.UpdatesClientName;
    }

    if (path.StartsWithSegments("/hubs/discovery", StringComparison.OrdinalIgnoreCase))
    {
        return GatewayServiceRoutes.RealtimeClientName;
    }

    if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    return GatewayServiceRoutes.WebClientName;
}

static HttpRequestMessage CreateForwardRequest(HttpContext context)
{
    var targetPath = context.Request.PathBase.Add(context.Request.Path).ToString();
    var targetUri = targetPath + context.Request.QueryString;
    var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);

    if (HttpMethods.IsPost(context.Request.Method)
        || HttpMethods.IsPut(context.Request.Method)
        || HttpMethods.IsPatch(context.Request.Method)
        || HttpMethods.IsDelete(context.Request.Method))
    {
        request.Content = new StreamContent(context.Request.Body);

        if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
        {
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType);
        }
    }

    foreach (var header in context.Request.Headers)
    {
        if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
            || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
            || header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
            || header.Key.StartsWith("Sec-WebSocket", StringComparison.OrdinalIgnoreCase)
            || header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
            || header.Key.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
        {
            request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
    }

    request.Headers.TryAddWithoutValidation("X-Forwarded-Host", context.Request.Host.Value);
    request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", context.Request.Scheme);
    request.Headers.TryAddWithoutValidation("X-Forwarded-For", context.Connection.RemoteIpAddress?.ToString());

    return request;
}

static async Task ForwardWebSocketAsync(
    HttpContext context,
    Uri serviceBaseAddress,
    CancellationToken cancellationToken)
{
    var targetUri = BuildWebSocketUri(
        serviceBaseAddress,
        context.Request.PathBase.Add(context.Request.Path),
        context.Request.QueryString);

    using var upstream = new ClientWebSocket();
    upstream.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

    foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols)
    {
        upstream.Options.AddSubProtocol(protocol);
    }

    await upstream.ConnectAsync(targetUri, cancellationToken).ConfigureAwait(false);

    using var downstream = await context.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol)
        .ConfigureAwait(false);

    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken,
        context.RequestAborted);

    var downstreamToUpstream = PumpWebSocketAsync(downstream, upstream, linkedCts.Token);
    var upstreamToDownstream = PumpWebSocketAsync(upstream, downstream, linkedCts.Token);

    await Task.WhenAny(downstreamToUpstream, upstreamToDownstream).ConfigureAwait(false);

    linkedCts.Cancel();
}

static Uri BuildWebSocketUri(Uri serviceBaseAddress, PathString path, QueryString query)
{
    var builder = new UriBuilder(new Uri(serviceBaseAddress, path + query.ToString()))
    {
        Scheme = serviceBaseAddress.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            ? "wss"
            : "ws"
    };

    return builder.Uri;
}

static async Task PumpWebSocketAsync(
    WebSocket source,
    WebSocket destination,
    CancellationToken cancellationToken)
{
    var buffer = new byte[64 * 1024];

    while (!cancellationToken.IsCancellationRequested
           && source.State == WebSocketState.Open
           && destination.State == WebSocketState.Open)
    {
        var result = await source.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            if (destination.State == WebSocketState.Open || destination.State == WebSocketState.CloseReceived)
            {
                await destination
                    .CloseOutputAsync(
                        result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        result.CloseStatusDescription,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        await destination
            .SendAsync(
                buffer.AsMemory(0, result.Count),
                result.MessageType,
                result.EndOfMessage,
                cancellationToken)
            .ConfigureAwait(false);
    }
}

static void CopyHeaders(HttpHeaders source, IHeaderDictionary destination)
{
    foreach (var header in source)
    {
        destination[header.Key] = header.Value.ToArray();
    }
}

sealed record DownstreamHealthTarget(string ClientName, Uri BaseAddress);

sealed record DownstreamHealthResult(
    string Service,
    Uri BaseAddress,
    bool Healthy,
    int? StatusCode,
    string Detail);

sealed record GatewayRouteOwner(string Owner, string[] Prefixes);

static class GatewayRouteDiagnostics
{
    private static readonly string[] WorkerControlPrefixes =
    [
        "/api/workers",
        "/api/ec2-workers",
        "/api/ops/ecs-status",
        "/api/ops/spider/restart",
        "/api/ops/subdomain-enum/restart"
    ];

    private static readonly string[] OperationsPrefixes = ["/api/status", "/api/ops"];

    private static readonly string[] DiscoveryPrefixes =
    [
        "/api/targets",
        "/api/assets",
        "/api/asset-graph",
        "/api/tags",
        "/api/technologies",
        "/api/asset-admission-decisions",
        "/api/high-value-findings",
        "/api/high-value-assets",
        "/api/technology-identification",
        "/api/http-request-queue",
        "/api/filestore",
        "/api/events",
        "/api/discovery"
    ];

    private static readonly string[] MaintenancePrefixes =
    [
        "/api/admin",
        "/api/maintenance",
        "/api/diagnostics",
        "/api/ui-preferences",
        "/api/bus",
        "/api/logs",
        "/api/worker-logs"
    ];

    private static readonly string[] UpdatesPrefixes = ["/api/development/components"];
    private static readonly string[] RealtimePrefixes = ["/hubs/discovery"];
    private static readonly string[] CloudDeployPrefixes = ["/api/cloud-deploy"];

    private static readonly string[] WebPrefixes =
    [
        "/",
        "/ops",
        "/commandcenter",
        "/status",
        "/admin",
        "/asset-admission",
        "/configuration",
        "/development",
        "/high-value-findings",
        "/technology-identification",
        "/_framework",
        "/_content",
        "/css",
        "/js"
    ];

    public static readonly GatewayRouteOwner[] Routes =
    [
        new("command-center-worker-control-api", WorkerControlPrefixes),
        new("command-center-operations-api", OperationsPrefixes),
        new("command-center-discovery-api", DiscoveryPrefixes),
        new("command-center-maintenance-api", MaintenancePrefixes),
        new("command-center-updates-api", UpdatesPrefixes),
        new("command-center-realtime", RealtimePrefixes),
        new("command-center-cloud-deploy-api", CloudDeployPrefixes),
        new("command-center-web", WebPrefixes)
    ];
}

sealed record GatewayServiceRoutes(
    Uri Web,
    Uri Discovery,
    Uri Operations,
    Uri WorkerControl,
    Uri Maintenance,
    Uri Updates,
    Uri Realtime,
    Uri CloudDeploy)
{
    public const string WebClientName = "command-center-web";
    public const string DiscoveryClientName = "command-center-discovery-api";
    public const string OperationsClientName = "command-center-operations-api";
    public const string WorkerControlClientName = "command-center-worker-control-api";
    public const string MaintenanceClientName = "command-center-maintenance-api";
    public const string UpdatesClientName = "command-center-updates-api";
    public const string RealtimeClientName = "command-center-realtime";
    public const string CloudDeployClientName = "command-center-cloud-deploy-api";

    public IReadOnlyList<DownstreamHealthTarget> HealthCheckTargets =>
    [
        new(WebClientName, Web),
        new(DiscoveryClientName, Discovery),
        new(OperationsClientName, Operations),
        new(WorkerControlClientName, WorkerControl),
        new(MaintenanceClientName, Maintenance),
        new(UpdatesClientName, Updates),
        new(RealtimeClientName, Realtime),
        new(CloudDeployClientName, CloudDeploy)
    ];

    public static GatewayServiceRoutes FromConfiguration(IConfiguration configuration)
    {
        return new GatewayServiceRoutes(
            GetUri(configuration, "Web", "http://command-center-web:8080/"),
            GetUri(configuration, "Discovery", "http://command-center-discovery-api:8080/"),
            GetUri(configuration, "Operations", "http://command-center-operations-api:8080/"),
            GetUri(configuration, "WorkerControl", "http://command-center-worker-control-api:8080/"),
            GetUri(configuration, "Maintenance", "http://command-center-maintenance-api:8080/"),
            GetUri(configuration, "Updates", "http://command-center-updates-api:8080/"),
            GetUri(configuration, "Realtime", "http://command-center-realtime:8080/"),
            GetUri(configuration, "CloudDeploy", "http://command-center-cloud-deploy-api:8080/"));
    }

    public Uri GetBaseAddress(string clientName) =>
        clientName switch
        {
            WebClientName => Web,
            DiscoveryClientName => Discovery,
            OperationsClientName => Operations,
            WorkerControlClientName => WorkerControl,
            MaintenanceClientName => Maintenance,
            UpdatesClientName => Updates,
            RealtimeClientName => Realtime,
            CloudDeployClientName => CloudDeploy,
            _ => throw new InvalidOperationException($"Unknown CommandCenter split-service client '{clientName}'.")
        };

    public object AsDiagnosticObject() =>
        new
        {
            web = Web,
            discovery = Discovery,
            operations = Operations,
            workerControl = WorkerControl,
            maintenance = Maintenance,
            updates = Updates,
            realtime = Realtime,
            cloudDeploy = CloudDeploy
        };

    private static Uri GetUri(IConfiguration configuration, string serviceName, string localDefault)
    {
        var value =
            configuration[$"CommandCenter:Services:{serviceName}"] ??
            configuration[$"Argus:CommandCenter:Services:{serviceName}"] ??
            configuration[$"CommandCenter:{serviceName}BaseUrl"] ??
            configuration[$"Argus:CommandCenter:{serviceName}BaseUrl"] ??
            localDefault;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                $"Invalid CommandCenter split-service URL for '{serviceName}': '{value}'. " +
                $"Configure CommandCenter:Services:{serviceName}.");
        }

        return uri;
    }
}
