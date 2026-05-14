using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.CommandCenter.Maintenance.Api.Endpoints;

public static class UiPreferencesEndpoints
{
    public static IEndpointRouteBuilder MapUiPreferencesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/ui-preferences/{preferenceKey}", GetPreferenceAsync);
        app.MapPut("/api/ui-preferences/{preferenceKey}", PutPreferenceAsync).DisableAntiforgery();
        return app;
    }

    private static async Task<IResult> GetPreferenceAsync(
        string preferenceKey,
        HttpRequest request,
        ArgusDbContext db,
        CancellationToken ct)
    {
        if (!IsValidPreferenceKey(preferenceKey))
            return Results.BadRequest(new { error = "Invalid preference key." });

        var userKey = ResolveUserKey(request, overrideKey: null);
        var row = await db.UserUiPreferences.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserKey == userKey && x.PreferenceKey == preferenceKey, ct)
            .ConfigureAwait(false);
        if (row is null)
            return Results.NotFound();

        using var doc = JsonDocument.Parse(row.PreferenceJson);
        return Results.Ok(new UiPreferenceResponse(preferenceKey, userKey, row.UpdatedAtUtc, doc.RootElement.Clone()));
    }

    private static async Task<IResult> PutPreferenceAsync(
        string preferenceKey,
        UiPreferenceUpsertRequest? body,
        HttpRequest request,
        ArgusDbContext db,
        CancellationToken ct)
    {
        if (!IsValidPreferenceKey(preferenceKey))
            return Results.BadRequest(new { error = "Invalid preference key." });
        if (body is null || body.Value.ValueKind is JsonValueKind.Undefined)
            return Results.BadRequest(new { error = "A JSON value is required." });

        var userKey = ResolveUserKey(request, body.UserKey);
        var preferenceJson = body.Value.GetRawText();
        if (preferenceJson.Length > 128_000)
            return Results.BadRequest(new { error = "Preference payload is too large." });

        var row = await db.UserUiPreferences
            .FirstOrDefaultAsync(x => x.UserKey == userKey && x.PreferenceKey == preferenceKey, ct)
            .ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        if (row is null)
        {
            row = new UserUiPreference
            {
                UserKey = userKey,
                PreferenceKey = preferenceKey,
                PreferenceJson = preferenceJson,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.UserUiPreferences.Add(row);
        }
        else
        {
            row.PreferenceJson = preferenceJson;
            row.UpdatedAtUtc = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Results.Ok(new UiPreferenceResponse(preferenceKey, userKey, row.UpdatedAtUtc, body.Value.Clone()));
    }

    private static bool IsValidPreferenceKey(string preferenceKey)
    {
        if (string.IsNullOrWhiteSpace(preferenceKey) || preferenceKey.Length > 128)
            return false;

        foreach (var c in preferenceKey)
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.')
                continue;

            return false;
        }

        return true;
    }

    private static string ResolveUserKey(HttpRequest request, string? overrideKey)
    {
        if (!string.IsNullOrWhiteSpace(overrideKey))
            return NormalizeUserKey(overrideKey);

        var claimsPrincipal = request.HttpContext.User;
        var userId =
            claimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? claimsPrincipal.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(userId))
            return NormalizeUserKey($"auth:{userId}");

        var forwardedFor = request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
            forwardedFor = forwardedFor.Split(',')[0].Trim();

        var remoteIp = forwardedFor
            ?? request.HttpContext.Connection.RemoteIpAddress?.ToString()
            ?? "";
        var userAgent = request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(remoteIp) && string.IsNullOrWhiteSpace(userAgent))
            return "anonymous";

        var raw = $"{remoteIp}|{userAgent}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var shortHash = Convert.ToHexString(hash[..16]).ToLowerInvariant();
        return $"anon:{shortHash}";
    }

    private static string NormalizeUserKey(string raw)
    {
        var value = raw.Trim();
        if (value.Length <= 256)
            return value;

        return value[..256];
    }

    public sealed record UiPreferenceUpsertRequest(JsonElement Value, string? UserKey = null);
    public sealed record UiPreferenceResponse(string PreferenceKey, string UserKey, DateTimeOffset UpdatedAtUtc, JsonElement Value);
}

