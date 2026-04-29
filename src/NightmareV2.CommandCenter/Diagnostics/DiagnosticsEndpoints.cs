using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using NightmareV2.Infrastructure.Data;
using StackExchange.Redis;

namespace NightmareV2.CommandCenter.Diagnostics;

public static class DiagnosticsEndpoints
{
    private const string DiagnosticsKeyHeader = "X-Nightmare-Diagnostics-Key";

    public static void Map(WebApplication app)
    {
        app.MapGet(
                "/health",
                () => Results.Ok(new { status = "live", at = DateTimeOffset.UtcNow }))
            .WithName("HealthLive")
            .AllowAnonymous();

        app.MapGet(
                "/health/ready",
                async (NightmareDbContext db, CancellationToken ct) =>
                {
                    try
                    {
                        if (!await db.Database.CanConnectAsync(ct).ConfigureAwait(false))
                            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                        return Results.Ok(new { status = "ready", postgres = "ok", at = DateTimeOffset.UtcNow });
                    }
                    catch (Exception)
                    {
                        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                    }
                })
            .WithName("HealthReady")
            .AllowAnonymous();

        app.MapGet(
                "/api/diagnostics/self",
                async (
                    HttpContext http,
                    IConfiguration config,
                    NightmareDbContext db,
                    IDbContextFactory<FileStoreDbContext> fileStoreFactory,
                    CancellationToken ct) =>
                {
                    if (!TryAuthorizeDiagnostics(http, config, out var rejected))
                        return rejected!;

                    var postgres = await CheckPostgresAsync(db, ct).ConfigureAwait(false);
                    var fileStore = await CheckFileStoreAsync(fileStoreFactory, ct).ConfigureAwait(false);

                    var asm = typeof(DiagnosticsEndpoints).Assembly;
                    var informational =
                        asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                        ?? asm.GetName().Version?.ToString()
                        ?? "?";
                    var proc = Process.GetCurrentProcess();
                    var uptime = DateTimeOffset.UtcNow - proc.StartTime.ToUniversalTime();

                    return Results.Json(
                        new
                        {
                            at = DateTimeOffset.UtcNow,
                            service = "command-center",
                            environment = app.Environment.EnvironmentName,
                            machine = Environment.MachineName,
                            processId = Environment.ProcessId,
                            uptimeSeconds = (int)Math.Floor(uptime.TotalSeconds),
                            buildStamp = Environment.GetEnvironmentVariable("NIGHTMARE_BUILD_STAMP") ?? "(not set)",
                            assemblyInformationalVersion = informational,
                            contentRoot = app.Environment.ContentRootPath,
                            webRoot = app.Environment.WebRootPath,
                            postgres = postgres.Status,
                            fileStore = fileStore.Status,
                            rabbitHost = config["RabbitMq:Host"],
                            rabbitVirtualHost = config["RabbitMq:VirtualHost"],
                            rabbitManagementUrlConfigured = !string.IsNullOrWhiteSpace(config["RabbitMq:ManagementUrl"]),
                            redisConfigured = !string.IsNullOrWhiteSpace(config.GetConnectionString("Redis")),
                            expectedBlazorScriptPath = "/_framework/blazor.web.js",
                        });
                })
            .WithName("DiagnosticsSelf")
            .AllowAnonymous();

        app.MapGet(
                "/api/diagnostics/dependencies",
                async (
                    HttpContext http,
                    IConfiguration config,
                    NightmareDbContext db,
                    IDbContextFactory<FileStoreDbContext> fileStoreFactory,
                    IConnectionMultiplexer redis,
                    CancellationToken ct) =>
                {
                    if (!TryAuthorizeDiagnostics(http, config, out var rejected))
                        return rejected!;

                    var postgres = await CheckPostgresAsync(db, ct).ConfigureAwait(false);
                    var fileStore = await CheckFileStoreAsync(fileStoreFactory, ct).ConfigureAwait(false);
                    var redisCheck = await CheckRedisAsync(redis, ct).ConfigureAwait(false);
                    var rabbit = await CheckRabbitMqTcpAsync(config, ct).ConfigureAwait(false);
                    var staticAssets = CheckStaticAssets(config);

                    var checks = new[]
                    {
                        postgres,
                        fileStore,
                        redisCheck,
                        rabbit,
                        staticAssets,
                    };
                    var overall = checks.All(c => string.Equals(c.Status, "ok", StringComparison.Ordinal))
                        ? "ok"
                        : "degraded";

                    return Results.Json(
                        new
                        {
                            at = DateTimeOffset.UtcNow,
                            service = "command-center",
                            overall,
                            checks = checks.ToDictionary(c => c.Name),
                        });
                })
            .WithName("DiagnosticsDependencies")
            .AllowAnonymous();
    }

    private static bool TryAuthorizeDiagnostics(HttpContext http, IConfiguration config, out IResult? rejected)
    {
        if (!config.GetValue("Nightmare:Diagnostics:Enabled", false))
        {
            rejected = Results.NotFound();
            return false;
        }

        var requiredKey = config["Nightmare:Diagnostics:ApiKey"]?.Trim();
        if (string.IsNullOrWhiteSpace(requiredKey))
        {
            rejected = Results.Problem(
                title: "Diagnostics endpoint misconfigured",
                detail: "Nightmare:Diagnostics:Enabled=true requires Nightmare:Diagnostics:ApiKey to be configured.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
            return false;
        }

        if (!string.Equals(
                http.Request.Headers[DiagnosticsKeyHeader].ToString(),
                requiredKey,
                StringComparison.Ordinal))
        {
            rejected = Results.Unauthorized();
            return false;
        }

        rejected = null;
        return true;
    }

    private static async Task<DependencyCheck> CheckPostgresAsync(NightmareDbContext db, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var ok = await db.Database.CanConnectAsync(ct).ConfigureAwait(false);
            return DependencyCheck.From("postgres", ok ? "ok" : "fail", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return DependencyCheck.From("postgres", "fail", sw.ElapsedMilliseconds, ex.Message);
        }
    }

    private static async Task<DependencyCheck> CheckFileStoreAsync(
        IDbContextFactory<FileStoreDbContext> fileStoreFactory,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var fs = await fileStoreFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var ok = await fs.Database.CanConnectAsync(ct).ConfigureAwait(false);
            return DependencyCheck.From("fileStore", ok ? "ok" : "fail", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return DependencyCheck.From("fileStore", "fail", sw.ElapsedMilliseconds, ex.Message);
        }
    }

    private static async Task<DependencyCheck> CheckRedisAsync(IConnectionMultiplexer redis, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var latency = await redis.GetDatabase().PingAsync().WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            return DependencyCheck.From("redis", redis.IsConnected ? "ok" : "fail", sw.ElapsedMilliseconds, $"ping={latency.TotalMilliseconds:0}ms");
        }
        catch (Exception ex)
        {
            return DependencyCheck.From("redis", "fail", sw.ElapsedMilliseconds, ex.Message);
        }
    }

    private static async Task<DependencyCheck> CheckRabbitMqTcpAsync(IConfiguration config, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var endpoint = ResolveRabbitEndpoint(config);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Host, endpoint.Port).WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            return DependencyCheck.From(
                "rabbitMqTcp",
                client.Connected ? "ok" : "fail",
                sw.ElapsedMilliseconds,
                $"{endpoint.Host}:{endpoint.Port}");
        }
        catch (Exception ex)
        {
            return DependencyCheck.From(
                "rabbitMqTcp",
                "fail",
                sw.ElapsedMilliseconds,
                $"{endpoint.Host}:{endpoint.Port} - {ex.Message}");
        }
    }

    private static DependencyCheck CheckStaticAssets(IConfiguration config)
    {
        var listenPlainHttp = config.GetValue("Nightmare:ListenPlainHttp", false);
        return new DependencyCheck(
            "staticAssets",
            "ok",
            0,
            new Dictionary<string, string?>
            {
                ["blazorScriptPath"] = "/_framework/blazor.web.js",
                ["appCssPath"] = "/app.css",
                ["listenPlainHttp"] = listenPlainHttp.ToString(),
                ["verification"] = "Use deploy/smoke-test.sh to fetch these URLs from the running container.",
            });
    }

    private static (string Host, int Port) ResolveRabbitEndpoint(IConfiguration config)
    {
        var raw = config["RabbitMq:Host"]?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return ("localhost", 5672);

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return (uri.Host, uri.Port > 0 ? uri.Port : 5672);

        var port = 5672;
        var host = raw;
        var lastColon = raw.LastIndexOf(':');
        if (lastColon > 0 && lastColon < raw.Length - 1 && int.TryParse(raw[(lastColon + 1)..], out var parsedPort))
        {
            host = raw[..lastColon];
            port = parsedPort;
        }

        return (host, port);
    }

    private sealed record DependencyCheck(
        string Name,
        string Status,
        long DurationMs,
        IReadOnlyDictionary<string, string?> Details)
    {
        public static DependencyCheck From(string name, string status, long durationMs, string? detail = null)
        {
            var details = string.IsNullOrWhiteSpace(detail)
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { ["detail"] = detail };
            return new DependencyCheck(name, status, durationMs, details);
        }
    }
}
