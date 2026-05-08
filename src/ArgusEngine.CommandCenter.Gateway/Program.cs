using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient(
    "legacy-command-center",
    (serviceProvider, client) =>
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var legacyBaseUrl = configuration["CommandCenter:LegacyBaseUrl"]
            ?? configuration["Argus:CommandCenter:LegacyBaseUrl"]
            ?? "http://command-center:8080/";
        client.BaseAddress = new Uri(legacyBaseUrl, UriKind.Absolute);
        client.Timeout = TimeSpan.FromMinutes(5);
    });

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));

app.Map("/{**path}", ForwardToLegacyCommandCenterAsync);

await app.RunAsync().ConfigureAwait(false);

static async Task<IResult> ForwardToLegacyCommandCenterAsync(
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken)
{
    using var request = CreateForwardRequest(context);
    using var response = await httpClientFactory
        .CreateClient("legacy-command-center")
        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
        .ConfigureAwait(false);

    context.Response.StatusCode = (int)response.StatusCode;
    CopyHeaders(response.Headers, context.Response.Headers);
    CopyHeaders(response.Content.Headers, context.Response.Headers);
    context.Response.Headers.Remove("transfer-encoding");

    await response.Content.CopyToAsync(context.Response.Body, cancellationToken).ConfigureAwait(false);
    return Results.Empty;
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
