using Microsoft.EntityFrameworkCore;
using ArgusEngine.Application.Assets;
using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.CommandCenter.Discovery.Api.Endpoints;

public static class AssetGraphEndpoints
{
    public static IEndpointRouteBuilder MapAssetGraphEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/targets/{targetId:guid}/assets/root",
                async (Guid targetId, IAssetGraphService graph, CancellationToken ct) =>
                {
                    var root = await graph.GetRootAssetAsync(targetId, ct).ConfigureAwait(false);
                    return root is null ? Results.NotFound() : Results.Ok(root);
                })
            .WithName("GetTargetRootAsset");

        app.MapGet(
                "/api/targets/{targetId:guid}/assets/{assetId:guid}",
                async (Guid targetId, Guid assetId, IAssetGraphService graph, CancellationToken ct) =>
                {
                    var asset = await graph.GetAssetAsync(targetId, assetId, ct).ConfigureAwait(false);
                    return asset is null ? Results.NotFound() : Results.Ok(asset);
                })
            .WithName("GetAsset");

        app.MapGet(
                "/api/targets/{targetId:guid}/assets/{assetId:guid}/children",
                async (Guid targetId, Guid assetId, IAssetGraphService graph, CancellationToken ct) =>
                    Results.Ok(await graph.GetChildrenAsync(targetId, assetId, ct).ConfigureAwait(false)))
            .WithName("GetAssetChildren");

        app.MapGet(
                "/api/targets/{targetId:guid}/assets/{assetId:guid}/parents",
                async (Guid targetId, Guid assetId, IAssetGraphService graph, CancellationToken ct) =>
                    Results.Ok(await graph.GetParentsAsync(targetId, assetId, ct).ConfigureAwait(false)))
            .WithName("GetAssetParents");

        app.MapGet(
                "/api/targets/{targetId:guid}/assets/{assetId:guid}/ancestors",
                async (Guid targetId, Guid assetId, int? maxDepth, IAssetGraphService graph, CancellationToken ct) =>
                    Results.Ok(await graph.GetAncestorsAsync(targetId, assetId, maxDepth ?? 10, ct).ConfigureAwait(false)))
            .WithName("GetAssetAncestors");

        app.MapGet(
                "/api/targets/{targetId:guid}/assets/{assetId:guid}/descendants",
                async (Guid targetId, Guid assetId, int? maxDepth, IAssetGraphService graph, CancellationToken ct) =>
                    Results.Ok(await graph.GetDescendantsAsync(targetId, assetId, maxDepth ?? 10, ct).ConfigureAwait(false)))
            .WithName("GetAssetDescendants");

        app.MapGet(
                "/api/targets/{targetId:guid}/asset-tree",
                async (Guid targetId, int? maxDepth, IAssetGraphService graph, CancellationToken ct) =>
                {
                    var root = await graph.GetRootAssetAsync(targetId, ct).ConfigureAwait(false);
                    if (root is null)
                        return Results.NotFound();

                    var tree = await graph.GetDescendantsAsync(targetId, root.Id, maxDepth ?? 10, ct).ConfigureAwait(false);
                    return Results.Ok(tree);
                })
            .WithName("GetTargetAssetTree");

        app.MapPost(
                "/api/targets/{targetId:guid}/asset-relationships",
                async (
                    Guid targetId,
                    CreateAssetRelationshipRequest request,
                    IAssetGraphService graph,
                    CancellationToken ct) =>
                {
                    var result = await graph.UpsertRelationshipAsync(
                            new AssetRelationshipDiscovered(
                                targetId,
                                request.ParentAssetId,
                                request.ChildAssetId,
                                request.RelationshipType,
                                request.IsPrimary,
                                request.Confidence <= 0 ? 1.0m : request.Confidence,
                                string.IsNullOrWhiteSpace(request.DiscoveredBy) ? "command-center" : request.DiscoveredBy,
                                request.DiscoveryContext ?? "",
                                request.PropertiesJson ?? "",
                                request.CorrelationId == Guid.Empty ? Guid.NewGuid() : request.CorrelationId,
                                DateTimeOffset.UtcNow,
                                EventId: Guid.NewGuid(),
                                Producer: "command-center"),
                            ct)
                        .ConfigureAwait(false);

                    if (result.RejectedReason is not null)
                        return Results.BadRequest(result);

                    return result.Inserted
                        ? Results.Created($"/api/targets/{targetId}/asset-relationships/{result.RelationshipId}", result)
                        : Results.Ok(result);
                })
            .WithName("CreateAssetRelationship");

        app.MapDelete(
                "/api/targets/{targetId:guid}/asset-relationships/{relationshipId:guid}",
                async (Guid targetId, Guid relationshipId, ArgusDbContext db, CancellationToken ct) =>
                {
                    var deleted = await db.AssetRelationships
                        .Where(r => r.TargetId == targetId && r.Id == relationshipId)
                        .ExecuteDeleteAsync(ct)
                        .ConfigureAwait(false);
                    return deleted == 0 ? Results.NotFound() : Results.NoContent();
                })
            .WithName("DeleteAssetRelationship");

        app.MapGet(
                "/api/targets/{targetId:guid}/asset-graph/metrics",
                async (Guid targetId, ArgusDbContext db, CancellationToken ct) =>
                {
                    var byKind = await db.Assets.AsNoTracking()
                        .Where(a => a.TargetId == targetId)
                        .GroupBy(a => a.Kind)
                        .Select(g => new CountByName(g.Key.ToString(), g.LongCount()))
                        .OrderBy(x => x.Name)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    var byCategory = await db.Assets.AsNoTracking()
                        .Where(a => a.TargetId == targetId)
                        .GroupBy(a => a.Category)
                        .Select(g => new CountByName(g.Key.ToString(), g.LongCount()))
                        .OrderBy(x => x.Name)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    var byRelationship = await db.AssetRelationships.AsNoTracking()
                        .Where(r => r.TargetId == targetId)
                        .GroupBy(r => r.RelationshipType)
                        .Select(g => new CountByName(g.Key.ToString(), g.LongCount()))
                        .OrderBy(x => x.Name)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    var orphanCount = await db.Assets.AsNoTracking()
                        .Where(a => a.TargetId == targetId && a.Kind != AssetKind.Target)
                        .Where(a => !db.AssetRelationships.Any(r => r.TargetId == targetId && r.ChildAssetId == a.Id))
                        .LongCountAsync(ct)
                        .ConfigureAwait(false);

                    var duplicateRelationshipCollapseCount = await db.AssetRelationships.AsNoTracking()
                        .Where(r => r.TargetId == targetId)
                        .GroupBy(r => new { r.ParentAssetId, r.ChildAssetId, r.RelationshipType })
                        .Where(g => g.Count() > 1)
                        .LongCountAsync(ct)
                        .ConfigureAwait(false);

                    return Results.Ok(
                        new AssetGraphMetricsResponse(
                            byKind,
                            byCategory,
                            byRelationship,
                            orphanCount,
                            duplicateRelationshipCollapseCount));
                })
            .WithName("GetAssetGraphMetrics");
        return app;
    }

    public sealed record CreateAssetRelationshipRequest(
        Guid ParentAssetId,
        Guid ChildAssetId,
        AssetRelationshipType RelationshipType,
        bool IsPrimary = false,
        decimal Confidence = 1.0m,
        string DiscoveredBy = "command-center",
        string? DiscoveryContext = "",
        string? PropertiesJson = "",
        Guid CorrelationId = default);

    public sealed record CountByName(string Name, long Count);

    public sealed record AssetGraphMetricsResponse(
        IReadOnlyList<CountByName> AssetsByKind,
        IReadOnlyList<CountByName> AssetsByCategory,
        IReadOnlyList<CountByName> RelationshipsByType,
        long AssetsWithoutParents,
        long DuplicateRelationshipRows);
}


