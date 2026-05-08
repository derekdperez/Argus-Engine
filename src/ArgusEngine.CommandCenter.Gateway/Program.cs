using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("command-center-web", (sp, c) => ConfigureClient(sp, c, "Web"));
builder.Services.AddHttpClient("command-center-discovery-api", (sp, c) => ConfigureClient(sp, c, "Discovery"));
builder.Services.AddHttpClient("command-center-operations-api", (sp, c) => ConfigureClient(sp, c, "Operations"));
builder.Services.AddHttpClient("command-center-worker-control-api", (sp, c) => ConfigureClient(sp, c, "WorkerControl"));
builder.Services.AddHttpClient("command-center-maintenance-api", (sp, c) => ConfigureClient(sp, c, "Maintenance"));
builder.Services.AddHttpClient("command-center-updates-api", (sp, c) => ConfigureClient(sp, c, "Updates"));
builder.Services.AddHttpClient("command-center-realtime", (sp, c) => ConfigureClient(sp, c, "Realtime"));

static void ConfigureClient(IServiceProvider sp, HttpClient c, string serviceName)
{
    var config = sp.GetRequiredService<IConfiguration>();
    var url = config[$"CommandCenter:Gateway:Services:{serviceName}"]
        ?? config[$"Argus:CommandCenter:Gateway:Services:{serviceName}"]
        ?? $"http://command-center-{serviceName.ToLowerInvariant()}:8080/";
    c.BaseAddress = new Uri(url, UriKind.Absolute);
    c.Timeout = TimeSpan.FromMinutes(5);
}

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));

app.Map("/{**path}", ForwardToSplitHostAsync);

await app.RunAsync().ConfigureAwait(false);

static async Task<IResult> ForwardToSplitHostAsync(
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken)
{
    var clientName = SelectClientName(context);
    using var request = CreateForwardRequest(context);
    using var response = await httpClientFactory
        .CreateClient(clientName)
        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
        .ConfigureAwait(false);

    context.Response.StatusCode = (int)response.StatusCode;
    CopyHeaders(response.Headers, context.Response.Headers);
    CopyHeaders(response.Content.Headers, context.Response.Headers);
    context.Response.Headers.Remove("transfer-encoding");

    await response.Content.CopyToAsync(context.Response.Body, cancellationToken).ConfigureAwait(false);
    return Results.Empty;
}

static string SelectClientName(HttpContext context)
{
    var path = context.Request.Path;

    if (path.StartsWithSegments("/api/status", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/ops", StringComparison.OrdinalIgnoreCase))
    {
        if (path.StartsWithSegments("/api/ops/spider/restart", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/ops/subdomain-enum/restart", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/ops/ecs-status", StringComparison.OrdinalIgnoreCase))
        {
            return "command-center-worker-control-api";
        }
        return "command-center-operations-api";
    }

    if (path.StartsWithSegments("/api/targets", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/assets", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/tags", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/technologies", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/technology-identification", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/http-request-queue", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/high-value-findings", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/asset-admission-decisions", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/filestore", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/events", StringComparison.OrdinalIgnoreCase))
    {
        return "command-center-discovery-api";
    }

    if (path.StartsWithSegments("/api/admin", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/maintenance", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/diagnostics", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/bus", StringComparison.OrdinalIgnoreCase))
    {
        return "command-center-maintenance-api";
    }

    if (path.StartsWithSegments("/api/workers", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/ec2-workers", StringComparison.OrdinalIgnoreCase))
    {
        return "command-center-worker-control-api";
    }

    if (path.StartsWithSegments("/api/development/components", StringComparison.OrdinalIgnoreCase))
    {
        return "command-center-updates-api";
    }

    if (path.StartsWithSegments("/hubs/discovery", StringComparison.OrdinalIgnoreCase))
    {
        return "command-center-realtime";
    }

    // Default to Web for pages and static assets
    return "command-center-web";
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
            || header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
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
    return request;
}

static void CopyHeaders(HttpHeaders source, IHeaderDictionary destination)
{
    foreach (var header in source)
    {
        destination[header.Key] = header.Value.ToArray();
    }
}
