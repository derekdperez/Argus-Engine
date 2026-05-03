using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.Infrastructure.FileStore;

public sealed class HttpRequestQueueArtifactSchemaInitializer(
    IServiceProvider services,
    ILogger<HttpRequestQueueArtifactSchemaInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ArgusDbContext>();

            await db.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE http_request_queue
                    ADD COLUMN IF NOT EXISTS request_headers_blob_id uuid NULL,
                    ADD COLUMN IF NOT EXISTS request_body_blob_id uuid NULL,
                    ADD COLUMN IF NOT EXISTS response_headers_blob_id uuid NULL,
                    ADD COLUMN IF NOT EXISTS response_body_blob_id uuid NULL,
                    ADD COLUMN IF NOT EXISTS redirect_chain_blob_id uuid NULL,
                    ADD COLUMN IF NOT EXISTS response_body_sha256 varchar(64) NULL,
                    ADD COLUMN IF NOT EXISTS response_body_preview varchar(4096) NULL,
                    ADD COLUMN IF NOT EXISTS response_body_truncated boolean NOT NULL DEFAULT false;

                CREATE INDEX IF NOT EXISTS ix_http_request_queue_response_body_sha256
                    ON http_request_queue (response_body_sha256);

                CREATE INDEX IF NOT EXISTS ix_http_request_queue_response_body_blob_id
                    ON http_request_queue (response_body_blob_id);

                CREATE INDEX IF NOT EXISTS ix_http_queue_active
                    ON http_request_queue (state, next_attempt_at_utc, priority DESC)
                    WHERE state IN ('Queued', 'Retry', 'InFlight');

                CREATE INDEX IF NOT EXISTS ix_http_queue_completed
                    ON http_request_queue (completed_at_utc)
                    WHERE state IN ('Succeeded', 'Failed');

                CREATE INDEX IF NOT EXISTS ix_http_queue_target_state
                    ON http_request_queue (target_id, state);

                CREATE INDEX IF NOT EXISTS ix_outbox_active
                    ON outbox_messages (state, next_attempt_at_utc)
                    WHERE state IN ('Pending', 'Failed', 'InFlight');

                CREATE INDEX IF NOT EXISTS ix_outbox_completed
                    ON outbox_messages (updated_at_utc)
                    WHERE state IN ('Succeeded', 'DeadLetter');
                """,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to ensure HTTP request artifact queue schema.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
