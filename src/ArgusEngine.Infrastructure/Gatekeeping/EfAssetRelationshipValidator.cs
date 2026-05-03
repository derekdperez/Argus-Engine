using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ArgusEngine.Application.Assets;
using ArgusEngine.Contracts;
using ArgusEngine.Infrastructure.Persistence.Data;

namespace ArgusEngine.Infrastructure.Gatekeeping;

public sealed class EfAssetRelationshipValidator(ArgusDbContext db) : IAssetRelationshipValidator
{
    public bool IsAllowed(AssetKind parentKind, AssetKind childKind, AssetRelationshipType relationshipType) =>
        AssetRelationshipRules.IsAllowed(parentKind, childKind, relationshipType);

    public async Task<bool> WouldCreateCycleAsync(
        Guid targetId,
        Guid parentAssetId,
        Guid childAssetId,
        CancellationToken cancellationToken = default)
    {
        if (parentAssetId == childAssetId)
            return true;

        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        if (db.Database.CurrentTransaction is { } currentTransaction)
            cmd.Transaction = currentTransaction.GetDbTransaction();

        cmd.CommandText = """
            WITH RECURSIVE descendants AS (
                SELECT child_asset_id
                FROM asset_relationships
                WHERE target_id = @target_id
                  AND parent_asset_id = @candidate_child_id

                UNION

                SELECT ar.child_asset_id
                FROM asset_relationships ar
                JOIN descendants d ON ar.parent_asset_id = d.child_asset_id
                WHERE ar.target_id = @target_id
            )
            SELECT EXISTS (
                SELECT 1
                FROM descendants
                WHERE child_asset_id = @candidate_parent_id
            );
            """;

        AddParameter(cmd, "target_id", targetId);
        AddParameter(cmd, "candidate_child_id", childAssetId);
        AddParameter(cmd, "candidate_parent_id", parentAssetId);

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is bool b && b;
    }

    private static void AddParameter(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
