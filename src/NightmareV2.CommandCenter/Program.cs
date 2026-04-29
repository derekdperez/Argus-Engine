using System.Net.Http;
using System.Text.Json;
using MassTransit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightmareV2.Application.Assets;
using NightmareV2.Application.Events;
using NightmareV2.Application.FileStore;
using NightmareV2.Application.HighValue;
using NightmareV2.Application.Workers;
using NightmareV2.CommandCenter;
using NightmareV2.CommandCenter.Components;
using NightmareV2.CommandCenter.DataMaintenance;
using NightmareV2.CommandCenter.Diagnostics;
using NightmareV2.CommandCenter.Endpoints;
using NightmareV2.CommandCenter.Hubs;
using NightmareV2.CommandCenter.Models;
using NightmareV2.Contracts;
using NightmareV2.Contracts.Events;
using NightmareV2.Domain.Entities;
using NightmareV2.Infrastructure;
using NightmareV2.Infrastructure.Data;
using NightmareV2.Infrastructure.Messaging;

var builder = WebApplication.CreateBuilder(args);

OpsSnapshotBuilder.RegisterHttpClient(builder);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddScoped(sp =>
{
    var nav = sp.GetRequiredService<NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
});

builder.Services.AddNightmareInfrastructure(builder.Configuration);
builder.Services.AddSignalR();
builder.Services.AddNightmareRabbitMq(builder.Configuration, _ => { });
builder.Services.AddOptions<NightmareRuntimeOptions>()
    .Bind(builder.Configuration.GetSection("Nightmare"))
    .Validate(
        o => !o.Diagnostics.Enabled || !string.IsNullOrWhiteSpace(o.Diagnostics.ApiKey),
        "Nightmare:Diagnostics:Enabled=true requires Nightmare:Diagnostics:ApiKey.")
    .Validate(
        o => !o.DataMaintenance.Enabled || !string.IsNullOrWhiteSpace(o.DataMaintenance.ApiKey),
        "Nightmare:DataMaintenance:Enabled=true requires Nightmare:DataMaintenance:ApiKey.")
    .ValidateOnStart();
builder.Services.AddOptions<ReliabilityBudgetOptions>()
    .Bind(builder.Configuration.GetSection("Nightmare:Reliability"))
    .Validate(o => o.MinEventProcessingSuccessRate is >= 0m and <= 1m, "Nightmare:Reliability:MinEventProcessingSuccessRate must be in [0,1].")
    .Validate(o => o.MaxQueueBacklogAgeSeconds >= 0, "Nightmare:Reliability:MaxQueueBacklogAgeSeconds must be >= 0.")
    .Validate(o => o.MinQueueDrainPerHour >= 0, "Nightmare:Reliability:MinQueueDrainPerHour must be >= 0.")
    .Validate(o => o.MaxWorkerErrorsPerHour >= 0, "Nightmare:Reliability:MaxWorkerErrorsPerHour must be >= 0.")
    .ValidateOnStart();

var app = builder.Build();

var skipStartupDatabase = app.Configuration.GetValue("Nightmare:SkipStartupDatabase", false)
    || string.Equals(
        Environment.GetEnvironmentVariable("NIGHTMARE_SKIP_STARTUP_DATABASE"),
        "1",
        StringComparison.OrdinalIgnoreCase);

await InitializeStartupDatabasesAsync(app, skipStartupDatabase).ConfigureAwait(false);

var listenPlainHttp = app.Configuration.GetValue("Nightmare:ListenPlainHttp", false);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    if (!listenPlainHttp)
        app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (!listenPlainHttp)
    app.UseHttpsRedirection();
app.UseAntiforgery();

DiagnosticsEndpoints.Map(app);
DataMaintenanceEndpoints.Map(app);

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHub<DiscoveryHub>("/hubs/discovery");
TargetEndpoints.Map(app);
HttpRequestQueueEndpoints.Map(app);
BusJournalEndpoints.Map(app);

app.MapGet(
        "/api/assets",
        async (NightmareDbContext db, Guid? targetId, int? take, CancellationToken ct) =>
        {
            var limit = Math.Clamp(take ?? 500, 1, 5000);
            var q = db.Assets.AsNoTracking().OrderByDescending(a => a.DiscoveredAtUtc).AsQueryable();
            if (targetId is { } tid)
                q = q.Where(a => a.TargetId == tid);
            var rows = await q.Take(limit)
                .Select(a => new AssetGridRowDto(
                    a.Id,
                    a.TargetId,
                    a.Kind.ToString(),
                    a.CanonicalKey,
                    a.RawValue,
                    a.Depth,
                    a.DiscoveredBy,
                    a.DiscoveryContext,
                    a.DiscoveredAtUtc,
                    a.LifecycleStatus))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            return Results.Ok(rows);
        })
    .WithName("ListAssets");

const long fileStoreMaxUploadBytes = 50L * 1024 * 1024;

app.MapPost(
        "/api/filestore",
        async (HttpRequest req, IFileStore store, CancellationToken ct) =>
        {
            if (!req.HasFormContentType)
                return Results.BadRequest("multipart/form-data with field \"file\" is required");
            var form = await req.ReadFormAsync(ct).ConfigureAwait(false);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.BadRequest("multipart field \"file\" is required");
            if (file.Length > fileStoreMaxUploadBytes)
                return Results.BadRequest($"file exceeds maximum size ({fileStoreMaxUploadBytes} bytes)");
            var logical = form["logicalName"].ToString();
            if (string.IsNullOrWhiteSpace(logical))
                logical = file.FileName;
            await using var uploadStream = file.OpenReadStream();
            var created = await store.StoreAsync(uploadStream, file.ContentType, logical, ct).ConfigureAwait(false);
            return Results.Created($"/api/filestore/{created.Id}", created);
        })
    .WithName("UploadFileBlob")
    .DisableAntiforgery();

app.MapGet(
        "/api/filestore/{id:guid}/info",
        async (Guid id, IFileStore store, CancellationToken ct) =>
        {
            var meta = await store.GetDescriptorAsync(id, ct).ConfigureAwait(false);
            return meta is null ? Results.NotFound() : Results.Ok(meta);
        })
    .WithName("GetFileBlobInfo");

app.MapGet(
        "/api/filestore/{id:guid}",
        async (Guid id, IFileStore store, CancellationToken ct) =>
        {
            var meta = await store.GetDescriptorAsync(id, ct).ConfigureAwait(false);
            if (meta is null)
                return Results.NotFound();
            var stream = await store.OpenReadAsync(id, ct).ConfigureAwait(false);
            if (stream is null)
                return Results.NotFound();
            return Results.File(
                stream,
                meta.ContentType ?? "application/octet-stream",
                fileDownloadName: meta.LogicalName ?? $"{meta.Id:N}");
        })
    .WithName("DownloadFileBlob");

app.MapDelete(
        "/api/filestore/{id:guid}",
        async (Guid id, IFileStore store, CancellationToken ct) =>
        {
            var meta = await store.GetDescriptorAsync(id, ct).ConfigureAwait(false);
            if (meta is null)
                return Results.NotFound();
            await store.DeleteAsync(id, ct).ConfigureAwait(false);
            return Results.NoContent();
        })
    .WithName("DeleteFileBlob");

app.MapGet(
        "/api/high-value-findings",
        async (NightmareDbContext db, bool? criticalOnly, int? take, CancellationToken ct) =>
        {
            var limit = Math.Clamp(take ?? 500, 1, 5000);
            var fetchCount = Math.Clamp(limit * 6, limit, 30000);
            var q = db.HighValueFindings.AsNoTracking()
                .Where(f => f.SourceAssetId != null)
                .Join(
                    db.Assets.AsNoTracking(),
                    f => f.SourceAssetId!.Value,
                    a => a.Id,
                    (f, a) => new { f, a })
                .Join(
                    db.Targets.AsNoTracking(),
                    x => x.f.TargetId,
                    t => t.Id,
                    (x, t) => new { x.f, x.a, t.RootDomain });
            if (criticalOnly == true)
                q = q.Where(x => x.f.Severity == "Critical");
            var rows = await q
                .OrderByDescending(x => x.f.DiscoveredAtUtc)
                .Take(fetchCount)
                .Select(x => new
                {
                    Row = new HighValueFindingRowDto(
                        x.f.Id,
                        x.f.TargetId,
                        x.f.SourceAssetId,
                        x.f.FindingType,
                        x.f.Severity,
                        x.f.PatternName,
                        x.f.Category,
                        x.f.MatchedText,
                        x.f.SourceUrl,
                        x.f.WorkerName,
                        x.f.ImportanceScore,
                        x.f.DiscoveredAtUtc,
                        x.RootDomain),
                    x.a.LifecycleStatus,
                    x.a.DiscoveredBy,
                    x.a.TypeDetailsJson,
                })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var confirmedRows = rows
                .Where(x => string.Equals(x.LifecycleStatus, AssetLifecycleStatus.Confirmed, StringComparison.Ordinal))
                .ToList();

            var allowedHighValuePaths = LoadHighValuePathSet();

            var filtered = confirmedRows
                .Select(x => new
                {
                    x.Row,
                    x.DiscoveredBy,
                    SnapshotOk = TryParseSnapshot(x.TypeDetailsJson, out var snap),
                    Snapshot = snap,
                })
                .Where(x => x.SnapshotOk)
                .Where(x => !LooksLikeSoft404(x.Snapshot))
                .Where(x => FindingSourceIsAllowed(x.Row, x.DiscoveredBy, x.Snapshot, allowedHighValuePaths))
                .Select(x => x.Row)
                .Take(limit)
                .ToList();
            return Results.Ok(filtered);
        })
    .WithName("ListHighValueFindings");

static bool FindingSourceIsAllowed(
    HighValueFindingRowDto row,
    string discoveredBy,
    UrlFetchSnapshot snap,
    IReadOnlySet<string> allowedHighValuePaths)
{
    // Regex findings can be raised from any confirmed URL response. The previous page query
    // incorrectly limited every high-value finding to hvpath:* assets, which hid confirmed
    // assets that matched the high-value regex scanner.
    if (!discoveredBy.StartsWith("hvpath:", StringComparison.OrdinalIgnoreCase))
        return true;

    return HighValuePathRedirectIsAllowed(row, snap, allowedHighValuePaths);
}

static bool HighValuePathRedirectIsAllowed(
    HighValueFindingRowDto row,
    UrlFetchSnapshot snap,
    IReadOnlySet<string> allowedHighValuePaths)
{
    var source = NormalizeUrlForCompare(row.SourceUrl);
    if (source is null)
        return false;

    // Older confirmed snapshots may not include FinalUrl. Treat them as displayable after the
    // confirmed-status and soft-404 checks above; otherwise historical high-value assets vanish.
    var final = NormalizeUrlForCompare(snap.FinalUrl);
    if (final is null)
        return true;

    var redirected = !string.Equals(source, final, StringComparison.OrdinalIgnoreCase);
    if (!redirected)
        return true;
    if (!Uri.TryCreate(final, UriKind.Absolute, out var finalUri))
        return false;
    return allowedHighValuePaths.Contains(NormalizeWordlistPath(finalUri.AbsolutePath));
}

static string? NormalizeUrlForCompare(string? url)
{
    if (string.IsNullOrWhiteSpace(url))
        return null;
    if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u))
        return null;
    if (u.Scheme is not ("http" or "https"))
        return null;
    var canonical = u.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
    return canonical.TrimEnd('/');
}

static IReadOnlySet<string> LoadHighValuePathSet()
{
    var dir = Path.Combine(AppContext.BaseDirectory, "Resources", "Wordlists", "high_value");
    var list = HighValueWordlistCatalog.LoadFromDirectory(dir);
    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (_, lines) in list)
    {
        foreach (var line in lines)
            set.Add(NormalizeWordlistPath(line));
    }

    return set;
}

static string NormalizeWordlistPath(string path)
{
    var p = path.Trim();
    if (p.Length == 0)
        return "/";
    var q = p.IndexOfAny(['?', '#']);
    if (q >= 0)
        p = p[..q];
    if (!p.StartsWith('/'))
        p = "/" + p;
    return p.TrimEnd('/');
}

static bool TryParseSnapshot(string? typeDetailsJson, out UrlFetchSnapshot snapshot)
{
    snapshot = default!;
    if (string.IsNullOrWhiteSpace(typeDetailsJson))
        return false;
    try
    {
        snapshot = JsonSerializer.Deserialize<UrlFetchSnapshot>(typeDetailsJson)!;
        return snapshot is not null;
    }
    catch
    {
        return false;
    }
}

static bool LooksLikeSoft404(UrlFetchSnapshot? snap)
{
    if (snap is null)
        return true;
    if (snap.StatusCode is 404 or 410)
        return true;
    if (snap.StatusCode < 200 || snap.StatusCode >= 300)
        return true;

    var body = snap.ResponseBody;
    if (string.IsNullOrWhiteSpace(body))
        return false;

    var contentType = snap.ContentType ?? "";
    var textLike = contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    if (!textLike)
        return false;

    var normalized = body.ToLowerInvariant();
    return normalized.Contains("404 not found", StringComparison.Ordinal)
        || normalized.Contains("page not found", StringComparison.Ordinal)
        || normalized.Contains("doesn't exist", StringComparison.Ordinal)
        || normalized.Contains("cannot be found", StringComparison.Ordinal)
        || normalized.Contains("the page you are looking for", StringComparison.Ordinal);
}

WorkerOpsEndpoints.Map(app);


static async Task InitializeStartupDatabasesAsync(WebApplication app, bool skipStartupDatabase)
{
    var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    if (skipStartupDatabase)
    {
        startupLog.LogWarning(
            "Startup database EnsureCreated skipped (Nightmare:SkipStartupDatabase or NIGHTMARE_SKIP_STARTUP_DATABASE=1). "
            + "APIs that need Postgres will still fail until a database is reachable.");
        return;
    }

    var continueOnFailure = app.Configuration.GetValue("Nightmare:ContinueOnStartupDatabaseFailure", true);
    var retryDelays = new[]
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15),
    };

    for (var attempt = 1; attempt <= retryDelays.Length + 1; attempt++)
    {
        try
        {
            await StartupDatabaseBootstrap.InitializeAsync(
                    app.Services,
                    app.Configuration,
                    startupLog,
                    includeFileStore: true,
                    app.Lifetime.ApplicationStopping)
                .ConfigureAwait(false);
            startupLog.LogInformation("Startup database initialization completed.");
            return;
        }
        catch (Exception ex) when (attempt <= retryDelays.Length && !app.Lifetime.ApplicationStopping.IsCancellationRequested)
        {
            startupLog.LogWarning(
                ex,
                "Startup database initialization failed on attempt {Attempt}; retrying.",
                attempt);
            await Task.Delay(retryDelays[attempt - 1], app.Lifetime.ApplicationStopping).ConfigureAwait(false);
        }
        catch (Exception ex) when (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
        {
            if (!continueOnFailure)
                throw;

            startupLog.LogError(
                ex,
                "Startup database initialization failed after retries. Command Center will continue to serve /health and diagnostics, but database-backed APIs will fail until Postgres/schema is fixed.");
            return;
        }
    }
}

app.Run();
