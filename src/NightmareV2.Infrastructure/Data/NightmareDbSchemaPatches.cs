using Microsoft.EntityFrameworkCore;
using NightmareV2.Domain.Entities;

namespace NightmareV2.Infrastructure.Data;

/// <summary>
/// EF <c>EnsureCreated</c> does not add new columns to existing databases; run these patches after it on upgrade.
/// </summary>
public static class NightmareDbSchemaPatches
{
    public static async Task ApplyAfterEnsureCreatedAsync(NightmareDbContext db, CancellationToken cancellationToken = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(542017296183746291);", cancellationToken)
            .ConfigureAwait(false);

        await NormalizeStoredAssetIdColumnAsync(db, cancellationToken).ConfigureAwait(false);

        await db.Database.ExecuteSqlRawAsync(
                """
                CREATE EXTENSION IF NOT EXISTS pgcrypto;
                """,
                cancellationToken)
            .ConfigureAwait(false);

        await db.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE bus_journal ADD COLUMN IF NOT EXISTS host_name character varying(256) NOT NULL DEFAULT '';
                """,
                cancellationToken)
            .ConfigureAwait(false);

        await db.Database.ExecuteSqlRawAsync(
                """
                DO $patch$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'public' AND table_name = 'bus_journal' AND column_name = 'consumer_type'
                    ) THEN
                        ALTER TABLE bus_journal ALTER COLUMN consumer_type TYPE character varying(512);
                    ELSIF EXISTS (
                        SELECT 1 FROM information_schema.tables
                        WHERE table_schema = 'public' AND table_name = 'bus_journal'
                    ) THEN
                        ALTER TABLE bus_journal ADD COLUMN consumer_type character varying(512);
                    END IF;
                END
                $patch$;
                """,
                cancellationToken)
            .ConfigureAwait(false);

        await db.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE stored_assets ADD COLUMN IF NOT EXISTS discovery_context character varying(512) NOT NULL DEFAULT '';
                """,
                cancellationToken)
            .ConfigureAwait(false);

        await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS cloud_resource_usage_samples (
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    sampled_at_utc timestamp with time zone NOT NULL,
                    resource_kind character varying(64) NOT NULL,
                    resource_id character varying(256) NOT NULL,
                    resource_name character varying(256) NOT NULL,
                    running_count integer NOT NULL,
                    metadata_json jsonb NULL
                );

                CREATE INDEX IF NOT EXISTS ix_cloud_resource_usage_kind_resource_sampled
                    ON cloud_resource_usage_samples (resource_kind, resource_id, sampled_at_utc);
                """,
                cancellationToken)
            .ConfigureAwait(false);

        await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS worker_scale_targets (
                    scale_key character varying(64) PRIMARY KEY,
                    desired_count integer NOT NULL,
                    updated_at_utc timestamp with time zone NOT NULL,
                    CONSTRAINT ck_worker_scale_targets_desired_count_nonnegative CHECK (desired_count >= 0)
                );
                """,
                cancellationToken)
            .ConfigureAwait(false);

        await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS worker_scaling_settings (
                    scale_key character varying(64) PRIMARY KEY,
                    min_tasks integer NOT NULL,
                    max_tasks integer NOT NULL,
                    target_backlog_per_task integer NOT NULL,
                    updated_at_utc timestamp with time zone NOT NULL,
                    CONSTRAINT ck_worker_scaling_settings_min_nonnegative CHECK (min_tasks >= 0),
                    CONSTRAINT ck_worker_scaling_settings_max_gte_min CHECK (max_tasks >= min_tasks),
                    CONSTRAINT ck_worker_scaling_settings_target_positive CHECK (target_backlog_per_task > 0)
                );
                """,
                cancellationToken)
            .ConfigureAwait(false);

        await db.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE stored_assets
                    ADD COLUMN IF NOT EXISTS asset_category smallint NOT NULL DEFAULT 0,
                    ADD COLUMN IF NOT EXISTS display_name character varying(512) NULL,
                    ADD COLUMN IF NOT EXISTS last_seen_at_utc timestamp with time zone NULL,
                    ADD COLUMN IF NOT EXISTS confidence numeric(5,4) NOT NULL DEFAULT 1.0,
                    ADD COLUMN IF NOT EXISTS final_url character varying(4096) NULL,
                    ADD COLUMN IF NOT EXISTS redirect_count integer NOT NULL DEFAULT 0;

                CREATE INDEX IF NOT EXISTS ix_stored_assets_target_kind
                    ON stored_assets ("TargetId", "Kind");

                CREATE INDEX IF NOT EXISTS ix_stored_assets_target_category
                    ON stored_assets ("TargetId", asset_category);

                CREATE TABLE IF NOT EXISTS asset_relationships (
                    id uuid PRIMARY KEY,
                    target_id uuid NOT NULL REFERENCES recon_targets("Id") ON DELETE CASCADE,
                    parent_asset_id uuid NOT NULL REFERENCES stored_assets("Id") ON DELETE CASCADE,
                    child_asset_id uuid NOT NULL REFERENCES stored_assets("Id") ON DELETE CASCADE,
                    relationship_type smallint NOT NULL,
                    is_primary boolean NOT NULL DEFAULT false,
                    confidence numeric(5,4) NOT NULL DEFAULT 1.0,
                    discovered_by character varying(128) NOT NULL,
                    discovery_context character varying(512) NOT NULL DEFAULT '',
                    properties_json jsonb NULL,
                    first_seen_at_utc timestamp with time zone NOT NULL,
                    last_seen_at_utc timestamp with time zone NOT NULL,
                    CONSTRAINT ck_asset_relationship_no_self CHECK (parent_asset_id <> child_asset_id)
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ux_asset_relationship_unique
                    ON asset_relationships (target_id, parent_asset_id, child_asset_id, relationship_type);

                CREATE INDEX IF NOT EXISTS ix_asset_relationship_parent
                    ON asset_relationships (target_id, parent_asset_id, relationship_type);

                CREATE INDEX IF NOT EXISTS ix_asset_relationship_child
                    ON asset_relationships (target_id, child_asset_id, relationship_type);

                CREATE UNIQUE INDEX IF NOT EXISTS ux_asset_relationship_primary_parent
                    ON asset_relationships (target_id, child_asset_id)
                    WHERE is_primary = true AND relationship_type = 0;
                """,
                cancellationToken)
            .ConfigureAwait(false);


        await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS tags (
                    id uuid NOT NULL PRIMARY KEY,
                    slug character varying(256) NOT NULL,
                    name character varying(256) NOT NULL,
                    tag_type character varying(64) NOT NULL,
                    source character varying(128) NOT NULL,
                    source_key character varying(256) NULL,
                    description character varying(1024) NULL,
                    website character varying(1024) NULL,
                    metadata_json jsonb NULL,
                    is_active boolean NOT NULL DEFAULT true,
                    created_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                    updated_at_utc timestamp with time zone NOT NULL DEFAULT now()
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ux_tags_slug ON tags (slug);
                CREATE INDEX IF NOT EXISTS ix_tags_type_source ON tags (tag_type, source);

                CREATE TABLE IF NOT EXISTS asset_tags (
                    id uuid NOT NULL PRIMARY KEY,
                    target_id uuid NOT NULL REFERENCES recon_targets("Id") ON DELETE CASCADE,
                    asset_id uuid NOT NULL REFERENCES stored_assets("Id") ON DELETE CASCADE,
                    tag_id uuid NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
                    confidence numeric(5,4) NOT NULL DEFAULT 1.0,
                    source character varying(128) NOT NULL,
                    evidence_json jsonb NULL,
                    first_seen_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                    last_seen_at_utc timestamp with time zone NOT NULL DEFAULT now()
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ux_asset_tags_asset_tag ON asset_tags (asset_id, tag_id);
                CREATE INDEX IF NOT EXISTS ix_asset_tags_target_tag ON asset_tags (target_id, tag_id);

                CREATE TABLE IF NOT EXISTS technology_detections (
                    id uuid NOT NULL PRIMARY KEY,
                    target_id uuid NOT NULL REFERENCES recon_targets("Id") ON DELETE CASCADE,
                    asset_id uuid NOT NULL REFERENCES stored_assets("Id") ON DELETE CASCADE,
                    tag_id uuid NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
                    technology_name character varying(256) NOT NULL,
                    evidence_source character varying(64) NOT NULL,
                    evidence_key character varying(512) NULL,
                    pattern character varying(2048) NULL,
                    matched_text character varying(512) NULL,
                    version character varying(128) NULL,
                    confidence numeric(5,4) NOT NULL DEFAULT 1.0,
                    evidence_hash character varying(64) NOT NULL,
                    detected_at_utc timestamp with time zone NOT NULL DEFAULT now()
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ux_technology_detections_asset_tag_hash
                    ON technology_detections (asset_id, tag_id, evidence_hash);
                CREATE INDEX IF NOT EXISTS ix_technology_detections_target_tag
                    ON technology_detections (target_id, tag_id);
                CREATE INDEX IF NOT EXISTS ix_technology_detections_detected_at
                    ON technology_detections (detected_at_utc DESC);
                """,
                cancellationToken)
            .ConfigureAwait(false);


        await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS high_value_findings (
                    id uuid NOT NULL PRIMARY KEY,
                    target_id uuid NOT NULL REFERENCES recon_targets("Id") ON DELETE CASCADE,
                    source_asset_id uuid NULL,
                    finding_type character varying(64) NOT NULL,
                    severity character varying(32) NOT NULL,
                    pattern_name character varying(256) NOT NULL,
                    category character varying(128) NULL,
                    matched_text text NULL,
                    source_url character varying(4096) NOT NULL,
                    worker_name character varying(128) NOT NULL,
                    importance_score integer NULL,
                    discovered_at_utc timestamp with time zone NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_high_value_findings_target_id ON high_value_findings (target_id);
                CREATE INDEX IF NOT EXISTS ix_high_value_findings_discovered_at ON high_value_findings (discovered_at_utc DESC);
                """,
                cancellationToken)
            .ConfigureAwait(false);

        await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS outbox_messages (
                    id uuid NOT NULL PRIMARY KEY,
                    message_type character varying(512) NOT NULL,
                    payload_json text NOT NULL,
                    event_id uuid NOT NULL,
                    correlation_id uuid NOT NULL,
                    causation_id uuid NOT NULL,
                    occurred_at_utc timestamp with time zone NOT NULL,
                    producer character varying(128) NOT NULL,
                    state character varying(32) NOT NULL DEFAULT 'Pending',
                    attempt_count integer NOT NULL DEFAULT 0,
                    created_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                    updated_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                    next_attempt_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                    dispatched_at_utc timestamp with time zone NULL,
                    last_error character varying(2048) NULL,
                    locked_by character varying(256) NULL,
                    locked_until_utc timestamp with time zone NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ux_outbox_messages_event_id ON outbox_messages (event_id);
                CREATE INDEX IF NOT EXISTS ix_outbox_messages_state_next_attempt ON outbox_messages (state, next_attempt_at_utc);
                CREATE INDEX IF NOT EXISTS ix_outbox_messages_created_at ON outbox_messages (created_at_utc DESC);

                CREATE TABLE IF NOT EXISTS inbox_messages (
                    id uuid NOT NULL PRIMARY KEY,
                    event_id uuid NOT NULL,
                    consumer character varying(256) NOT NULL,
                    processed_at_utc timestamp with time zone NOT NULL DEFAULT now()
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ux_inbox_messages_event_consumer ON inbox_messages (event_id, consumer);
                """,
                cancellationToken)
            .ConfigureAwait(false);



        await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS http_request_queue_settings (
                    id integer NOT NULL PRIMARY KEY,
                    enabled boolean NOT NULL DEFAULT true,
                    global_requests_per_minute integer NOT NULL DEFAULT 120,
                    per_domain_requests_per_minute integer NOT NULL DEFAULT 6,
                    max_concurrency integer NOT NULL DEFAULT 8,
                    request_timeout_seconds integer NOT NULL DEFAULT 30,
                    updated_at_utc timestamp with time zone NOT NULL DEFAULT now()
                );

                INSERT INTO http_request_queue_settings (
                    id,
                    enabled,
                    global_requests_per_minute,
                    per_domain_requests_per_minute,
                    max_concurrency,
                    request_timeout_seconds,
                    updated_at_utc
                )
                VALUES (1, true, 120, 6, 8, 30, now())
                ON CONFLICT (id) DO NOTHING;

                CREATE TABLE IF NOT EXISTS http_request_queue (
                    id uuid NOT NULL PRIMARY KEY,
                    asset_id uuid NOT NULL REFERENCES stored_assets("Id") ON DELETE CASCADE,
                    target_id uuid NOT NULL,
                    asset_kind integer NOT NULL,
                    method character varying(16) NOT NULL DEFAULT 'GET',
                    request_url character varying(4096) NOT NULL,
                    domain_key character varying(253) NOT NULL,
                    state character varying(32) NOT NULL DEFAULT 'Queued',
                    priority integer NOT NULL DEFAULT 0,
                    attempt_count integer NOT NULL DEFAULT 0,
                    max_attempts integer NOT NULL DEFAULT 3,
                    created_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                    updated_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                    next_attempt_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                    locked_by character varying(256) NULL,
                    locked_until_utc timestamp with time zone NULL,
                    started_at_utc timestamp with time zone NULL,
                    completed_at_utc timestamp with time zone NULL,
                    duration_ms bigint NULL,
                    last_http_status integer NULL,
                    last_error character varying(2048) NULL,
                    request_headers_json text NULL,
                    request_body text NULL,
                    response_headers_json text NULL,
                    response_body text NULL,
                    response_content_type character varying(256) NULL,
                    response_content_length bigint NULL,
                    final_url character varying(4096) NULL,
                    redirect_count integer NOT NULL DEFAULT 0
                );

                ALTER TABLE http_request_queue
                    ADD COLUMN IF NOT EXISTS redirect_count integer NOT NULL DEFAULT 0;

                CREATE UNIQUE INDEX IF NOT EXISTS ux_http_request_queue_asset_id ON http_request_queue (asset_id);
                CREATE INDEX IF NOT EXISTS ix_http_request_queue_state_next_attempt ON http_request_queue (state, next_attempt_at_utc);
                CREATE INDEX IF NOT EXISTS ix_http_request_queue_domain_started ON http_request_queue (domain_key, started_at_utc);
                CREATE INDEX IF NOT EXISTS ix_http_request_queue_created_at ON http_request_queue (created_at_utc DESC);
                """,
                cancellationToken)
            .ConfigureAwait(false);

        await BackfillLegacyDiscoveredAssetsAsync(db, cancellationToken).ConfigureAwait(false);
        await BackfillAssetCategoriesAndRootsAsync(db, cancellationToken).ConfigureAwait(false);
        await BackfillAssetRelationshipsAsync(db, cancellationToken).ConfigureAwait(false);
        await BackfillHttpRequestQueueAsync(db, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }



    private static async Task NormalizeStoredAssetIdColumnAsync(NightmareDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
                """
                DO $patch$
                BEGIN
                    -- A prior compatibility change mapped StoredAsset.Id to an unquoted lower-case id column.
                    -- Existing and fresh EnsureCreated databases use the quoted "Id" column, and the HTTP queue
                    -- foreign key also references stored_assets("Id"). Rename accidental lower-case deployments back
                    -- so EF queries, queue joins, and cascade deletes all use one schema shape.
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'public' AND table_name = 'stored_assets' AND column_name = 'id'
                    ) AND NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'public' AND table_name = 'stored_assets' AND column_name = 'Id'
                    ) THEN
                        ALTER TABLE stored_assets RENAME COLUMN id TO "Id";
                    END IF;
                END
                $patch$;
                """,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task BackfillAssetCategoriesAndRootsAsync(NightmareDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE stored_assets
                SET asset_category = CASE
                    WHEN "Kind" = -1 THEN 0
                    WHEN "Kind" IN (0, 1) THEN 1
                    WHEN "Kind" IN (2, 3, 4, 20) THEN 2
                    WHEN "Kind" = 21 THEN 3
                    WHEN "Kind" IN (10, 11, 12, 33) THEN 4
                    WHEN "Kind" IN (13, 14) THEN 5
                    WHEN "Kind" IN (30, 31, 32) THEN 6
                    ELSE asset_category
                END,
                display_name = COALESCE(display_name, "RawValue"),
                last_seen_at_utc = COALESCE(last_seen_at_utc, "DiscoveredAtUtc")
                WHERE "Kind" IS NOT NULL;

                INSERT INTO stored_assets (
                    "Id",
                    "TargetId",
                    "Kind",
                    "CanonicalKey",
                    "RawValue",
                    "Depth",
                    "DiscoveredBy",
                    discovery_context,
                    "DiscoveredAtUtc",
                    "LifecycleStatus",
                    "TypeDetailsJson",
                    asset_category,
                    display_name,
                    last_seen_at_utc,
                    confidence
                )
                SELECT
                    gen_random_uuid(),
                    t."Id",
                    -1,
                    'target:' || lower(trim(trailing '.' from t."RootDomain")),
                    lower(trim(trailing '.' from t."RootDomain")),
                    0,
                    'schema-backfill',
                    'Root target asset backfilled from recon_targets',
                    COALESCE(t."CreatedAtUtc", now()),
                    'Confirmed',
                    NULL,
                    0,
                    lower(trim(trailing '.' from t."RootDomain")),
                    now(),
                    1.0
                FROM recon_targets t
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM stored_assets a
                    WHERE a."TargetId" = t."Id"
                      AND a."CanonicalKey" = 'target:' || lower(trim(trailing '.' from t."RootDomain"))
                );
                """,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task BackfillAssetRelationshipsAsync(NightmareDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
                """
                WITH roots AS (
                    SELECT a."Id" AS root_asset_id, a."TargetId" AS target_id
                    FROM stored_assets a
                    WHERE a."Kind" = -1
                ),
                host_assets AS (
                    SELECT a."Id" AS child_asset_id, a."TargetId" AS target_id, a."DiscoveredAtUtc" AS discovered_at
                    FROM stored_assets a
                    WHERE a."Kind" IN (0, 1)
                )
                INSERT INTO asset_relationships (
                    id,
                    target_id,
                    parent_asset_id,
                    child_asset_id,
                    relationship_type,
                    is_primary,
                    confidence,
                    discovered_by,
                    discovery_context,
                    properties_json,
                    first_seen_at_utc,
                    last_seen_at_utc
                )
                SELECT
                    gen_random_uuid(),
                    h.target_id,
                    r.root_asset_id,
                    h.child_asset_id,
                    0,
                    true,
                    1.0,
                    'schema-backfill',
                    'Root-to-host relationship backfilled for existing asset',
                    NULL,
                    COALESCE(h.discovered_at, now()),
                    now()
                FROM host_assets h
                JOIN roots r ON r.target_id = h.target_id
                WHERE r.root_asset_id <> h.child_asset_id
                ON CONFLICT (target_id, parent_asset_id, child_asset_id, relationship_type) DO UPDATE
                SET last_seen_at_utc = EXCLUDED.last_seen_at_utc;
                """,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task BackfillHttpRequestQueueAsync(NightmareDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
                """
                WITH asset_projection AS (
                    SELECT
                        COALESCE((to_jsonb(a) ->> 'Id')::uuid, (to_jsonb(a) ->> 'id')::uuid) AS asset_id,
                        COALESCE((to_jsonb(a) ->> 'TargetId')::uuid, (to_jsonb(a) ->> 'target_id')::uuid) AS target_id,
                        COALESCE((to_jsonb(a) ->> 'Kind')::integer, (to_jsonb(a) ->> 'kind')::integer) AS asset_kind,
                        COALESCE(to_jsonb(a) ->> 'RawValue', to_jsonb(a) ->> 'raw_value') AS raw_value,
                        COALESCE(to_jsonb(a) ->> 'LifecycleStatus', to_jsonb(a) ->> 'lifecycle_status') AS lifecycle_status,
                        COALESCE((to_jsonb(a) ->> 'DiscoveredAtUtc')::timestamp with time zone, (to_jsonb(a) ->> 'discovered_at_utc')::timestamp with time zone) AS discovered_at_utc
                    FROM stored_assets a
                )
                INSERT INTO http_request_queue (
                    id,
                    asset_id,
                    target_id,
                    asset_kind,
                    method,
                    request_url,
                    domain_key,
                    state,
                    priority,
                    created_at_utc,
                    updated_at_utc,
                    next_attempt_at_utc
                )
                SELECT
                    gen_random_uuid(),
                    a.asset_id,
                    a.target_id,
                    a.asset_kind,
                    'GET',
                    CASE
                        WHEN a.asset_kind IN (0, 1) THEN 'https://' || trim(trailing '/' from a.raw_value) || '/'
                        WHEN position('://' in a.raw_value) > 0 THEN a.raw_value
                        ELSE 'https://' || a.raw_value
                    END,
                    lower(
                        CASE
                            WHEN a.asset_kind IN (0, 1) THEN trim(trailing '/' from a.raw_value)
                            ELSE regexp_replace(regexp_replace(a.raw_value, '^[a-zA-Z][a-zA-Z0-9+.-]*://', ''), '[:/].*$', '')
                        END
                    ),
                    'Queued',
                    0,
                    COALESCE(a.discovered_at_utc, now()),
                    now(),
                    now()
                FROM asset_projection a
                WHERE a.asset_id IS NOT NULL
                  AND a.target_id IS NOT NULL
                  AND a.asset_kind IS NOT NULL
                  AND a.raw_value IS NOT NULL
                  AND a.lifecycle_status = 'Queued'
                  AND a.asset_kind IN (0, 1, 10, 11, 12, 33)
                  AND NOT EXISTS (
                      SELECT 1 FROM http_request_queue q WHERE q.asset_id = a.asset_id
                  );
                """,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Normalize legacy statuses after introducing Queued as the default initial status.</summary>
    private static async Task BackfillLegacyDiscoveredAssetsAsync(NightmareDbContext db, CancellationToken cancellationToken)
    {
        await db.Assets
            .Where(a => a.LifecycleStatus == "Discovered")
            .ExecuteUpdateAsync(
                s => s.SetProperty(a => a.LifecycleStatus, AssetLifecycleStatus.Queued),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
