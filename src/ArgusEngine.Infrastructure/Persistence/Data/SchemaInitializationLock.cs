using Microsoft.EntityFrameworkCore;

namespace ArgusEngine.Infrastructure.Data;

internal static class SchemaInitializationLock
{
    private const string LockSql = "SELECT pg_advisory_lock(18653214017668471);";
    private const string UnlockSql = "SELECT pg_advisory_unlock(18653214017668471);";

    public static async Task ExecuteWithLockAsync(
        DbContext db,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await db.Database.ExecuteSqlRawAsync(LockSql, cancellationToken).ConfigureAwait(false);
            await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(UnlockSql, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }
}
