using Microsoft.AspNetCore.Mvc;
using ArgusEngine.CommandCenter.DataMaintenance;

namespace ArgusEngine.CommandCenter.Endpoints;

public static class HttpArtifactBackfillEndpoints
{
    private const string ConfirmationPhrase = "BACKFILL HTTP ARTIFACTS";

    public static IEndpointRouteBuilder MapHttpArtifactBackfillEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/maintenance/backfill-http-artifacts", async (
            [FromBody] HttpArtifactBackfillRequest request,
            HttpQueueArtifactBackfillService service,
            CancellationToken ct) =>
        {
            if (!string.Equals(request.Confirmation, ConfirmationPhrase, StringComparison.Ordinal))
            {
                return Results.BadRequest(new
                {
                    error = $"Confirmation phrase must be exactly '{ConfirmationPhrase}'."
                });
            }

            var result = await service.RunOnceAsync(ct).ConfigureAwait(false);
            return Results.Ok(result);
        });

        return app;
    }

    public sealed record HttpArtifactBackfillRequest(string Confirmation);
}
