using ArgusEngine.CommandCenter.Models;
using ArgusEngine.CommandCenter.Services.DeveloperAutomation;

namespace ArgusEngine.CommandCenter.Endpoints;

public static class DeveloperAutomationEndpoints
{
    public static IEndpointRouteBuilder MapDeveloperAutomationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/developer-automation");

        group.MapGet(
                "/status",
                (GitHubDeveloperAutomationClient automation) => Results.Ok(automation.GetStatus()))
            .WithName("DeveloperAutomationStatus");

        group.MapPost(
                "/bugfix",
                async (
                    DeveloperAutomationRequestDto request,
                    GitHubDeveloperAutomationClient automation,
                    CancellationToken cancellationToken) =>
                    await QueueAsync("bugfix", request, automation, cancellationToken).ConfigureAwait(false))
            .WithName("QueueAiBugFixAutomation");

        group.MapPost(
                "/feature",
                async (
                    DeveloperAutomationRequestDto request,
                    GitHubDeveloperAutomationClient automation,
                    CancellationToken cancellationToken) =>
                    await QueueAsync("feature", request, automation, cancellationToken).ConfigureAwait(false))
            .WithName("QueueAiFeatureAutomation");

        return app;
    }

    private static async Task<IResult> QueueAsync(
        string mode,
        DeveloperAutomationRequestDto request,
        GitHubDeveloperAutomationClient automation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Description))
        {
            return Results.BadRequest("Description is required.");
        }

        try
        {
            var result = await automation.QueueAsync(mode, request, cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (HttpRequestException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
