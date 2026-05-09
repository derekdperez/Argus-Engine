#!/usr/bin/env bash
set -euo pipefail

python3 - <<'PY'
from pathlib import Path
import re

web = Path("src/ArgusEngine.CommandCenter.Web/Program.cs")
gateway = Path("src/ArgusEngine.CommandCenter.Gateway/Program.cs")

if not web.exists():
    raise SystemExit(f"Missing {web}")
if not gateway.exists():
    raise SystemExit(f"Missing {gateway}")

# ---------------------------------------------------------------------------
# CommandCenter.Web:
# - Trust X-Forwarded-* from command-center-gateway.
# - Make server-side HttpClients call the gateway for /api routes instead of
#   recursively calling command-center-web.
# This is source-only and works with --hot; no compose env change is required.
# ---------------------------------------------------------------------------
text = web.read_text()

if "using Microsoft.AspNetCore.HttpOverrides;" not in text:
    text = text.replace(
        "using Radzen;",
        "using Radzen;\nusing Microsoft.AspNetCore.HttpOverrides;",
        1,
    )

configure_block = '''builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedHost |
        ForwardedHeaders.XForwardedProto;

    // command-center-gateway is another Docker service with a dynamic container
    // IP. Trust forwarded headers from the internal compose network.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
'''

if "builder.Services.Configure<ForwardedHeadersOptions>" not in text:
    text = text.replace(
        "builder.Services.AddHttpContextAccessor();",
        "builder.Services.AddHttpContextAccessor();\n" + configure_block,
        1,
    )

if "app.UseForwardedHeaders();" not in text:
    text = text.replace(
        "var app = builder.Build();",
        "var app = builder.Build();\n\napp.UseForwardedHeaders();",
        1,
    )

new_resolver = '''static Uri ResolveRequestBaseAddress(IServiceProvider services)
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var configuredGatewayBaseUrl =
        configuration["CommandCenter:GatewayBaseUrl"] ??
        configuration["Argus:CommandCenter:GatewayBaseUrl"];

    if (!string.IsNullOrWhiteSpace(configuredGatewayBaseUrl))
    {
        return CreateAbsoluteUri(configuredGatewayBaseUrl, "configured Command Center gateway base URL");
    }

    var request = services.GetRequiredService<IHttpContextAccessor>().HttpContext?.Request;
    if (request is not null)
    {
        // When the Blazor app is reached through command-center-gateway, its
        // server-side typed HttpClients must call the gateway for /api/* routes.
        // Otherwise they call command-center-web itself and can fault the circuit.
        if (request.Headers.ContainsKey("X-Forwarded-Host") ||
            request.Headers.ContainsKey("X-Forwarded-Proto"))
        {
            return new Uri("http://command-center-gateway:8080/");
        }

        var pathBase = request.PathBase.HasValue ? request.PathBase.Value : "";
        return new Uri($"{request.Scheme}://{request.Host}{pathBase}/");
    }

    var navigation = services.GetRequiredService<NavigationManager>();
    return new Uri(navigation.BaseUri);
}

static Uri CreateAbsoluteUri(string value, string description)
{
    var normalized = value.EndsWith('/', StringComparison.Ordinal) ? value : value + "/";
    if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
    {
        throw new InvalidOperationException($"Invalid {description}: '{value}'.");
    }

    return uri;
}
'''

if "http://command-center-gateway:8080/" not in text:
    text, count = re.subn(
        r"static\s+Uri\s+ResolveRequestBaseAddress\s*\(\s*IServiceProvider\s+services\s*\)\s*\{[\s\S]*\}\s*$",
        new_resolver,
        text,
        count=1,
    )
    if count != 1:
        raise SystemExit("Could not replace ResolveRequestBaseAddress in CommandCenter.Web Program.cs")

web.write_text(text)

# ---------------------------------------------------------------------------
# CommandCenter.Gateway:
# Make websocket relay shutdown more tolerant so browser-side Blazor does not
# see avoidable 1006 disconnects when one side closes or reloads.
# ---------------------------------------------------------------------------
gateway_text = gateway.read_text()

new_ws = '''static async Task ForwardWebSocketAsync(HttpContext context, Uri serviceBaseAddress, CancellationToken cancellationToken)
{
    var targetUri = BuildWebSocketUri(serviceBaseAddress, context.Request.PathBase.Add(context.Request.Path), context.Request.QueryString);

    using var upstream = new ClientWebSocket();
    upstream.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

    foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols)
    {
        upstream.Options.AddSubProtocol(protocol);
    }

    await upstream.ConnectAsync(targetUri, cancellationToken).ConfigureAwait(false);

    using var downstream = await context.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol).ConfigureAwait(false);
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.RequestAborted);

    var downstreamToUpstream = PumpWebSocketAsync(downstream, upstream, linkedCts.Token);
    var upstreamToDownstream = PumpWebSocketAsync(upstream, downstream, linkedCts.Token);

    try
    {
        var completed = await Task.WhenAny(downstreamToUpstream, upstreamToDownstream).ConfigureAwait(false);
        await completed.ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (linkedCts.IsCancellationRequested || context.RequestAborted.IsCancellationRequested)
    {
        // Normal shutdown/reload path.
    }
    catch (WebSocketException)
    {
        // The browser reports 1006 when either side drops the TCP connection
        // without a close frame. Treat that as a transport disconnect.
    }
    finally
    {
        linkedCts.Cancel();

        await CloseQuietlyAsync(
            downstream,
            WebSocketCloseStatus.NormalClosure,
            "Command Center gateway closing downstream websocket",
            CancellationToken.None).ConfigureAwait(false);

        await CloseQuietlyAsync(
            upstream,
            WebSocketCloseStatus.NormalClosure,
            "Command Center gateway closing upstream websocket",
            CancellationToken.None).ConfigureAwait(false);
    }
}

'''

new_pump = '''static async Task PumpWebSocketAsync(WebSocket source, WebSocket destination, CancellationToken cancellationToken)
{
    var buffer = new byte[64 * 1024];

    try
    {
        while (!cancellationToken.IsCancellationRequested &&
               source.State == WebSocketState.Open &&
               destination.State == WebSocketState.Open)
        {
            var result = await source.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await CloseQuietlyAsync(
                    destination,
                    result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                    result.CloseStatusDescription ?? "Peer requested websocket close",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (destination.State != WebSocketState.Open)
            {
                return;
            }

            await destination
                .SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, cancellationToken)
                .ConfigureAwait(false);
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // Expected during relay shutdown.
    }
    catch (WebSocketException)
    {
        // Expected when the peer drops the connection without a close frame.
    }
}

static async Task CloseQuietlyAsync(
    WebSocket webSocket,
    WebSocketCloseStatus closeStatus,
    string closeDescription,
    CancellationToken cancellationToken)
{
    try
    {
        if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
        {
            await webSocket.CloseOutputAsync(closeStatus, closeDescription, cancellationToken).ConfigureAwait(false);
        }
    }
    catch (WebSocketException)
    {
        webSocket.Abort();
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        webSocket.Abort();
    }
}

'''

if "Command Center gateway closing downstream websocket" not in gateway_text:
    gateway_text, count = re.subn(
        r"static\s+async\s+Task\s+ForwardWebSocketAsync\s*\([\s\S]*?\n\}\s*\n\s*static\s+Uri\s+BuildWebSocketUri",
        new_ws + "static Uri BuildWebSocketUri",
        gateway_text,
        count=1,
    )
    if count != 1:
        raise SystemExit("Could not replace ForwardWebSocketAsync in Gateway Program.cs")

if "static async Task CloseQuietlyAsync" not in gateway_text:
    gateway_text, count = re.subn(
        r"static\s+async\s+Task\s+PumpWebSocketAsync\s*\([\s\S]*?\n\}\s*\n\s*static\s+void\s+CopyHeaders",
        new_pump + "static void CopyHeaders",
        gateway_text,
        count=1,
    )
    if count != 1:
        raise SystemExit("Could not replace PumpWebSocketAsync in Gateway Program.cs")

gateway.write_text(gateway_text)

print("Patched:")
print(f"  {web}")
print(f"  {gateway}")
PY

echo
echo "Review with:"
echo "  git diff -- src/ArgusEngine.CommandCenter.Web/Program.cs src/ArgusEngine.CommandCenter.Gateway/Program.cs"
