using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NightmareV2.CommandCenter.Models;
using NightmareV2.Application.Workers;
using NightmareV2.Application.Events;
using NightmareV2.Contracts.Events;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace NightmareV2.CommandCenter.Endpoints;

public static class WorkerOpsEndpoints
{
    public static void MapWorkerOpsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/ops");

        group.MapPost("/subdomain-enum/restart", async (RestartToolRequest request, IEventOutbox outbox, ITargetLookup targetLookup) =>
        {
            var targetIds = request.AllTargets 
                ? await targetLookup.GetAllTargetIdsAsync() 
                : request.TargetIds ?? Array.Empty<string>();

            // Fixed CA1829: Use .Length or .Count instead of Enumerable.Count()
            if (targetIds is string[] array)
            {
                foreach (var id in array) await outbox.PublishAsync(new SubdomainEnumerationRequested(id));
            }
            else
            {
                foreach (var id in targetIds) await outbox.PublishAsync(new SubdomainEnumerationRequested(id));
            }
            
            return Results.Accepted();
        });

        group.MapPost("/spider/restart", async (RestartToolRequest request, IEventOutbox outbox, ITargetLookup targetLookup) =>
        {
            var targetIds = request.AllTargets 
                ? await targetLookup.GetAllTargetIdsAsync() 
                : request.TargetIds ?? Array.Empty<string>();

            foreach (var id in targetIds)
            {
                await outbox.PublishAsync(new ScannableContentAvailable(id, NightmareV2.Contracts.ScannableContentSource.UserRequest));
            }
            return Results.Accepted();
        });
    }
}
