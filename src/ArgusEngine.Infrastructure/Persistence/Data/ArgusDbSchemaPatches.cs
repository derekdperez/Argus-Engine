using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ArgusEngine.Domain.Entities;

namespace ArgusEngine.Infrastructure.Data;

/// <summary>
/// EF <c>EnsureCreated</c> does not add new columns to existing databases; run these patches after it on upgrade.
/// </summary>
public static partial class ArgusDbSchemaPatches
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Applying database schema patches after EnsureCreated...")]
    static partial void LogApplyingPatches(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Acquiring advisory lock for schema patches...")]
    static partial void LogAcquiringAdvisoryLock(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Normalizing stored asset ID columns...")]
    static partial void LogNormalizingAssetIds(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Normalizing column casing...")]
    static partial void LogNormalizingCasing(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ensuring pgcrypto extension...")]
    static partial void LogEnsuringPgCrypto(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Patching bus_journal schema...")]
    static partial void LogPatchingBusJournal(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Patching stored_assets schema...")]
    static partial void LogPatchingStoredAssets(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ensuring asset_relationships table and constraints...")]
    static partial void LogEnsuringAssetRelationships(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ensuring tags and detections tables...")]
    static partial void LogEnsuringTagsAndDetections(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ensuring technology fingerprint detection tables...")]
    static partial void LogEnsuringTechnologyFingerprintTables(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ensuring high_value_findings and outbox tables...")]
    static partial void LogEnsuringHighValueAndOutbox(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ensuring system_errors table...")]
    static partial void LogEnsuringSystemErrors(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ensuring ai_bug_fix_runs table...")]
    static partial void LogEnsuringAiBugFixRuns(ILogger logger);


    [LoggerMessage(Level = LogLevel.Debug, Message = "Patching worker_heartbeats primary key...")]
    static partial void LogPatchingHeartbeats(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Executing backfill tasks...")]
    static partial void LogExecutingBackfills(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database schema patches applied successfully.")]
    static partial void LogPatchesApplied(ILogger logger);

    public static async Task ApplyAfterEnsureCreatedAsync(ArgusDbContext db, ILogger logger, CancellationToken cancellationToken = default)
    {
        LogApplyingPatches(logger);

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        
        LogAcquiringAdvisoryLock(logger);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(542017296183746291);", cancellationToken)
            .ConfigureAwait(false);

        LogNormalizingAssetIds(logger);
        await NormalizeStoredAssetIdColumnAsync(db, logger, cancellationToken).ConfigureAwait(false);

        LogNormalizingCasing(logger);
        await NormalizeColumnCasingAsync(db, logger, cancellationToken).ConfigureAwait(false);

        LogEnsuringPgCrypto(logger);
        await db.Database.ExecuteSqlRawAsync(
                """
                CREATE EXTENSION IF NOT EXISTS pgcrypto;
                """,
                cancellationToken)
            .ConfigureAwait(false);

        await EnsureCoreTablesAsync(db, cancellationToken).ConfigureAwait(false);

        LogPatchingBusJournal(logger);
        await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS bus_journal (
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    direction character varying(16) NOT NULL,
                    message_type character varying(256) NOT NULL,
                    consumer_type character varying(2048) NULL,
                    payload_json text NOT NULL,
                    occurred_at_utc timestamp with time zone NOT NULL,
                    host_name character varying(256) NOT NULL DEFAULT '',
                    "Status" character varying(32) NOT NULL DEFAULT 'Completed',
                    "DurationMs" double precision NULL,
                    "Error" text NULL,
                    "MessageId" uuid NULL
                );
                CREATE INDEX IF NOT EXISTS ix_bus_journal_occurred_at_utc ON bus_journal (occurred_at_utc);
                CREATE INDEX IF NOT EXISTS ix_bus_journal_message_id ON bus_journal ("MessageId");

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
                        ALTER TABLE bus_journal ALTER COLUMN consumer_type TYPE character varying(2048);
                    ELSIF EXISTS (
                        SELECT 1 FROM information_schema.tables
                        WHERE table_schema = 'public' AND table_name = 'bus_journal'
                    ) THEN
                        ALTER TABLE bus_journal ADD COLUMN consumer_type character varying(2048);
                    END IF;
                END
                $patch$;
                """,
                cancellationToken)
            .ConfigureAwait(false);

        LogPatchingStoredAssets(logger);
        await db.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE stored_assets ADD COLUMN IF NOT EXISTS asset_category smallint NOT NULL DEFAULT 0;
                ALTER TABLE stored_assets ADD COLUMN IF NOT EXISTS display_name character varying(512) NULL;
                ALTER TABLE stored_assets ADD COLUMN IF NOT EXISTS last_seen_at_utc timestamp with time zone NULL;
                ALTER TABLE stored_assets ADD COLUMN IF NOT EXISTS final_url character varying(4096) NULL;
                ALTER TABLE stored_assets ADD COLUMN IF NOT EXISTS redirect_count integer NOT NULL DEFAULT 0;
                ALTER TABLE stored_assets ADD COLUMN IF NOT EXISTS redirect_chain_json jsonb NULL;
                ALTER TABLE stored_assets ADD COLUMN IF NOT EXISTS confidence numeric(5,4) NOT NULL DEFAULT 1.0;
                ALTER TABLE stored_assets ADD COLUMN IF NOT EXISTS discovered_by character varying(128) NOT NULL DEFAULT 'legacy';
                ALTER TABLE stored_assets ADD COLUMN IF NOT EXISTS discovery_context character varying(512) NOT NULL DEFAULT '';
                ALTER TABLE stored_assets ADD COLUMN IF NOT EXISTS type_details_json text NULL;
                """,
                cancellationToken)
            .ConfigureAwait(false);

        LogEnsuringAssetRelationships(logger);
        await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS asset_relationships (
                    id uuid NOT NULL PRIMARY KEY,
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

                ALTER TABLE asset_relationships ADD COLUMN IF NOT EXISTS is_primary boolean NOT NULL DEFAULT false;
                ALTER TABLE asset_relationships ADD COLUMN IF NOT EXISTS confidence numeric(5,4) NOT NULL DEFAULT 1.0;
                ALTER TABLE asset_relationships ADD COLUMN IF NOT EXISTS discovered_by character varying(128) NOT NULL DEFAULT 'legacy';
                ALTER TABLE asset_relationships ADD COLUMN IF NOT EXISTS discovery_context character varying(512) NOT NULL DEFAULT '';
                ALTER TABLE asset_relationships ADD COLUMN IF NOT EXISTS properties_json jsonb NULL;
                ALTER TABLE asset_relationships ADD COLUMN IF NOT EXISTS first_seen_at_utc timestamp with time zone NOT NULL DEFAULT now();
                ALTER TABLE asset_relationships ADD COLUMN IF NOT EXISTS last_seen_at_utc timestamp with time zone NOT NULL DEFAULT now();

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

        LogEnsuringTagsAndDetections(logger);
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

        LogEnsuringTechnologyFingerprintTables(logger);
        await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS technology_catalog_loads (
                    id uuid NOT NULL PRIMARY KEY,
                    catalog_hash text NOT NULL,
                    fingerprint_count integer NOT NULL,
                    resource_path text NOT NULL,
                    loaded_by_service text NOT NULL,
                    loaded_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                    validation_status text NOT NULL,
                    validation_errors_json jsonb NOT NULL DEFAULT '[]'::jsonb
                );

                CREATE INDEX IF NOT EXISTS ix_technology_catalog_loads_hash
                    ON technology_catalog_loads (catalog_hash);
                CREATE INDEX IF NOT EXISTS ix_technology_catalog_loads_loaded_at
                    ON technology_catalog_loads (loaded_at_utc DESC);

                CREATE TABLE IF NOT EXISTS technology_fingerprint_overrides (
                    id uuid NOT NULL PRIMARY KEY,
                    fingerprint_id text NOT NULL,
                    tenant_id uuid NULL,
                    enabled boolean NOT NULL DEFAULT true,
                    confidence_adjustment numeric(5,4) NULL,
                    reason text NULL,
                    created_by text NOT NULL DEFAULT '',
                    created_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                    updated_at_utc timestamp with time zone NOT NULL DEFAULT now()
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ux_technology_fingerprint_overrides_scope
                    ON technology_fingerprint_overrides (fingerprint_id, (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)));
                CREATE INDEX IF NOT EXISTS ix_technology_fingerprint_overrides_fingerprint
                    ON technology_fingerprint_overrides (fingerprint_id);

                CREATE TABLE IF NOT EXISTS technology_detection_runs (
                    id uuid NOT NULL PRIMARY KEY,
                    target_id uuid NOT NULL REFERENCES recon_targets("Id") ON DELETE CASCADE,
                    catalog_hash text NOT NULL,
                    mode text NOT NULL,
                    status text NOT NULL,
                    created_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                    started_at_utc timestamp with time zone NULL,
                    completed_at_utc timestamp with time zone NULL
                );

                CREATE INDEX IF NOT EXISTS ix_technology_detection_runs_target_created
                    ON technology_detection_runs (target_id, created_at_utc DESC);
                CREATE INDEX IF NOT EXISTS ix_technology_detection_runs_status
                    ON technology_detection_runs (status);

                CREATE TABLE IF NOT EXISTS technology_observations (
                    id uuid NOT NULL PRIMARY KEY,
                    run_id uuid NOT NULL REFERENCES technology_detection_runs(id) ON DELETE CASCADE,
                    target_id uuid NOT NULL REFERENCES recon_targets("Id") ON DELETE CASCADE,
                    asset_id uuid NOT NULL REFERENCES stored_assets("Id") ON DELETE CASCADE,
                    fingerprint_id text NOT NULL,
                    catalog_hash text NOT NULL,
                    technology_name text NOT NULL,
                    vendor text NULL,
                    product text NULL,
                    version text NULL,
                    confidence_score numeric(5,4) NOT NULL,
                    source_type text NOT NULL,
                    detection_mode text NOT NULL,
                    dedupe_key text NOT NULL,
                    first_seen_utc timestamp with time zone NOT NULL DEFAULT now(),
                    last_seen_utc timestamp with time zone NOT NULL DEFAULT now(),
                    metadata_json jsonb NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ux_technology_observations_target_asset_dedupe
                    ON technology_observations (target_id, asset_id, dedupe_key);
                CREATE INDEX IF NOT EXISTS ix_technology_observations_target_name
                    ON technology_observations (target_id, technology_name);
                CREATE INDEX IF NOT EXISTS ix_technology_observations_asset_last_seen
                    ON technology_observations (asset_id, last_seen_utc DESC);

                CREATE TABLE IF NOT EXISTS technology_observation_evidence (
                    id uuid NOT NULL PRIMARY KEY,
                    observation_id uuid NOT NULL REFERENCES technology_observations(id) ON DELETE CASCADE,
                    signal_id text NOT NULL,
                    evidence_type text NOT NULL,
                    evidence_key text NULL,
                    matched_value_redacted text NULL,
                    artifact_id uuid NULL,
                    evidence_hash text NOT NULL,
                    created_at_utc timestamp with time zone NOT NULL DEFAULT now()
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ux_technology_observation_evidence_hash
                    ON technology_observation_evidence (observation_id, evidence_hash);
                CREATE INDEX IF NOT EXISTS ix_technology_observation_evidence_observation
                    ON technology_observation_evidence (observation_id);
                """,
                cancellationToken)
            .ConfigureAwait(false);

        LogEnsuringHighValueAndOutbox(logger);
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
                ALTER TABLE high_value_findings
                    ADD COLUMN IF NOT EXISTS is_high_value boolean NOT NULL DEFAULT true;
                ALTER TABLE high_value_findings
                    ADD COLUMN IF NOT EXISTS investigation_status character varying(32) NOT NULL DEFAULT 'Pending';
                ALTER TABLE high_value_findings
                    ADD COLUMN IF NOT EXISTS investigation_updated_at_utc timestamp with time zone NULL;
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
                """,
                cancellationToken)
            .ConfigureAwait(false);

        await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS inbox_messages (
                    id uuid NOT NULL PRIMARY KEY,
                    event_id uuid NOT NULL,
                    consumer character varying(256) NOT NULL,
                    processed_at_utc timestamp with time zone NOT NULL,
                    CONSTRAINT ux_inbox_messages_event_consumer UNIQUE (event_id, consumer)
                );
                """,
                cancellationToken)
            .ConfigureAwait(false);

        LogEnsuringSystemErrors(logger);
        await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS system_errors (
                    "Id" uuid NOT NULL PRIMARY KEY,
                    "Timestamp" timestamp with time zone NOT NULL,
                    "Component" character varying(128) NOT NULL,
                    "LogLevel" character varying(32) NOT NULL,
                    "LoggerName" character varying(512) NOT NULL,
                    "Message" text NOT NULL,
                    "Exception" text NULL,
                    "MachineName" character varying(256) NOT NULL,
                    "MetadataJson" jsonb NULL
                );
                CREATE INDEX IF NOT EXISTS ix_system_errors_timestamp ON system_errors ("Timestamp" DESC);
                CREATE INDEX IF NOT EXISTS ix_system_errors_component ON system_errors ("Component");
                """,
                cancellationToken)
            .ConfigureAwait(false);

        LogEnsuringAiBugFixRuns(logger);
        await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS ai_bug_fix_runs (
                    id uuid PRIMARY KEY,
                    created_at_utc timestamptz NOT NULL DEFAULT now(),
                    updated_at_utc timestamptz NOT NULL DEFAULT now(),
                    requested_by text NULL,
                    requested_from_ip text NULL,
                    source_url text NULL,
                    status text NOT NULL,
                    status_message text NULL,
                    error_count integer NOT NULL DEFAULT 0,
                    component_scope text[] NOT NULL DEFAULT ARRAY[]::text[],
                    error_snapshot_json jsonb NOT NULL DEFAULT '[]'::jsonb,
                    prompt_text text NOT NULL DEFAULT '',
                    prompt_sha256 text NOT NULL DEFAULT '',
                    github_owner text NOT NULL DEFAULT 'derekdperez',
                    github_repo text NOT NULL DEFAULT 'Argus-Engine',
                    github_branch text NULL,
                    github_pr_number integer NULL,
                    github_pr_url text NULL,
                    github_workflow_run_id bigint NULL,
                    github_workflow_url text NULL,
                    github_merge_sha text NULL,
                    deployment_run_id text NULL,
                    deployment_url text NULL,
                    deployment_completed_at_utc timestamptz NULL,
                    smoke_test_result_json jsonb NULL,
                    failure_detail text NULL
                );

                CREATE INDEX IF NOT EXISTS ix_ai_bug_fix_runs_created_at
                    ON ai_bug_fix_runs (created_at_utc DESC);

                CREATE INDEX IF NOT EXISTS ix_ai_bug_fix_runs_status
                    ON ai_bug_fix_runs (status);
                """,
                cancellationToken)
            .ConfigureAwait(false);


        LogPatchingHeartbeats(logger);
        await db.Database.ExecuteSqlRawAsync(
                """
                DO $patch$
                DECLARE
                    pk_name text;
                BEGIN
                    -- Find the actual primary key constraint name for worker_heartbeats
                    SELECT constraint_name INTO pk_name
                    FROM information_schema.table_constraints 
                    WHERE table_name = 'worker_heartbeats' AND constraint_type = 'PRIMARY KEY'
                    LIMIT 1;

                    IF pk_name IS NOT NULL THEN
                        -- Check if it's already a composite key (2 columns)
                        IF (
                            SELECT count(*) FROM information_schema.key_column_usage
                            WHERE constraint_name = pk_name AND table_name = 'worker_heartbeats'
                        ) = 1 THEN
                            -- It's a single-column PK (legacy), drop and upgrade to composite
                            EXECUTE 'ALTER TABLE worker_heartbeats DROP CONSTRAINT ' || quote_ident(pk_name);
                            ALTER TABLE worker_heartbeats ADD PRIMARY KEY ("HostName", "WorkerKey");
                        END IF;
                    ELSE
                        -- No primary key found at all, add the composite one
                        ALTER TABLE worker_heartbeats ADD PRIMARY KEY ("HostName", "WorkerKey");
                    END IF;
                END
                $patch$;
                """,
                cancellationToken)
            .ConfigureAwait(false);

        LogExecutingBackfills(logger);
        await BackfillAssetCategoriesAndRootsAsync(db, logger, cancellationToken).ConfigureAwait(false);
        await BackfillAssetRelationshipsAsync(db, logger, cancellationToken).ConfigureAwait(false);
        await EnsureHttpRequestQueueDefaultsAsync(db, logger, cancellationToken).ConfigureAwait(false);
        await BackfillHttpRequestQueueAsync(db, logger, cancellationToken).ConfigureAwait(false);
        await BackfillLegacyDiscoveredAssetsAsync(db, logger, cancellationToken).ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        LogPatchesApplied(logger);
    }

    private static async Task NormalizeStoredAssetIdColumnAsync(ArgusDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
                """
                DO $patch$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'public' AND table_name = 'stored_assets' AND column_name = 'id'
                    ) THEN
                        ALTER TABLE stored_assets RENAME COLUMN id TO "Id";
                    END IF;

                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'public' AND table_name = 'recon_targets' AND column_name = 'id'
                    ) THEN
                        ALTER TABLE recon_targets RENAME COLUMN id TO "Id";
                    END IF;
                END
                $patch$;
                """,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task NormalizeColumnCasingAsync(ArgusDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
                """
                DO $patch$
                BEGIN
                    -- Stored Assets normalization
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'stored_assets' AND column_name = 'DiscoveredBy') THEN
                        ALTER TABLE stored_assets RENAME COLUMN "DiscoveredBy" TO discovered_by;
                    END IF;
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'stored_assets' AND column_name = 'TypeDetailsJson') THEN
                        ALTER TABLE stored_assets RENAME COLUMN "TypeDetailsJson" TO type_details_json;
                    END IF;

                    -- Asset Relationships normalization
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'asset_relationships' AND column_name = 'DiscoveredBy') THEN
                        ALTER TABLE asset_relationships RENAME COLUMN "DiscoveredBy" TO discovered_by;
                    END IF;
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'asset_relationships' AND column_name = 'PropertiesJson') THEN
                        ALTER TABLE asset_relationships RENAME COLUMN "PropertiesJson" TO properties_json;
                    END IF;

                    -- Bus Journal normalization
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'bus_journal' AND column_name = 'OccurredAtUtc') THEN
                        ALTER TABLE bus_journal RENAME COLUMN "OccurredAtUtc" TO occurred_at_utc;
                    END IF;
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'bus_journal' AND column_name = 'MessageType') THEN
                        ALTER TABLE bus_journal RENAME COLUMN "MessageType" TO message_type;
                    END IF;
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'bus_journal' AND column_name = 'Producer') THEN
                        ALTER TABLE bus_journal RENAME COLUMN "Producer" TO producer;
                    END IF;
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'bus_journal' AND column_name = 'PayloadJson') THEN
                        ALTER TABLE bus_journal RENAME COLUMN "PayloadJson" TO payload_json;
                    END IF;
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'bus_journal' AND column_name = 'Id') THEN
                        ALTER TABLE bus_journal RENAME COLUMN "Id" TO id;
                    END IF;
                END
                $patch$;
                """,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task BackfillAssetCategoriesAndRootsAsync(ArgusDbContext db, ILogger logger, CancellationToken cancellationToken)
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
                last_seen_at_utc = COALESCE(last_seen_at_utc, "DiscoveredAtUtc"),
                redirect_count = COALESCE(redirect_count, 0)
                WHERE "Kind" IS NOT NULL;

                INSERT INTO stored_assets (
                    "Id",
                    "TargetId",
                    "Kind",
                    "CanonicalKey",
                    "RawValue",
                    "Depth",
                    discovered_by,
                    discovery_context,
                    "DiscoveredAtUtc",
                    "LifecycleStatus",
                    type_details_json,
                    asset_category,
                    display_name,
                    last_seen_at_utc,
                    confidence,
                    redirect_count,
                    final_url,
                    redirect_chain_json
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
                    1.0,
                    0,
                    NULL,
                    NULL
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

    private static async Task BackfillAssetRelationshipsAsync(ArgusDbContext db, ILogger logger, CancellationToken cancellationToken)
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

    private static async Task EnsureCoreTablesAsync(ArgusDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS recon_targets (
                    "Id" uuid NOT NULL PRIMARY KEY,
                    "RootDomain" character varying(253) NOT NULL,
                    "GlobalMaxDepth" integer NOT NULL,
                    "CreatedAtUtc" timestamp with time zone NOT NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_recon_targets_RootDomain"
                    ON recon_targets ("RootDomain");

                CREATE TABLE IF NOT EXISTS stored_assets (
                    "Id" uuid NOT NULL PRIMARY KEY,
                    "TargetId" uuid NOT NULL REFERENCES recon_targets("Id") ON DELETE CASCADE,
                    "Kind" integer NOT NULL,
                    "CanonicalKey" character varying(2048) NOT NULL,
                    "RawValue" character varying(4096) NOT NULL,
                    asset_category smallint NOT NULL DEFAULT 0,
                    display_name character varying(512) NULL,
                    "Depth" integer NOT NULL DEFAULT 0,
                    discovered_by character varying(128) NOT NULL DEFAULT 'legacy',
                    discovery_context character varying(512) NOT NULL DEFAULT '',
                    "DiscoveredAtUtc" timestamp with time zone NOT NULL DEFAULT now(),
                    last_seen_at_utc timestamp with time zone NULL,
                    confidence numeric(5,4) NOT NULL DEFAULT 1.0,
                    "LifecycleStatus" character varying(32) NOT NULL DEFAULT 'Queued',
                    type_details_json text NULL,
                    final_url character varying(4096) NULL,
                    redirect_count integer NOT NULL DEFAULT 0,
                    redirect_chain_json jsonb NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_stored_assets_TargetId_CanonicalKey"
                    ON stored_assets ("TargetId", "CanonicalKey");
                CREATE INDEX IF NOT EXISTS "IX_stored_assets_TargetId_Kind"
                    ON stored_assets ("TargetId", "Kind");
                CREATE INDEX IF NOT EXISTS ix_stored_assets_target_category
                    ON stored_assets ("TargetId", asset_category);

                CREATE TABLE IF NOT EXISTS http_request_queue (
                    id uuid NOT NULL PRIMARY KEY,
                    asset_id uuid NOT NULL REFERENCES stored_assets("Id") ON DELETE CASCADE,
                    target_id uuid NOT NULL,
                    asset_kind integer NOT NULL,
                    method character varying(16) NOT NULL,
                    request_url character varying(4096) NOT NULL,
                    domain_key character varying(253) NOT NULL,
                    state character varying(32) NOT NULL,
                    priority integer NOT NULL DEFAULT 0,
                    attempt_count integer NOT NULL DEFAULT 0,
                    max_attempts integer NOT NULL DEFAULT 3,
                    created_at_utc timestamp with time zone NOT NULL,
                    updated_at_utc timestamp with time zone NOT NULL,
                    next_attempt_at_utc timestamp with time zone NOT NULL,
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
                    redirect_count integer NOT NULL DEFAULT 0,
                    redirect_chain_json jsonb NULL,
                    request_headers_blob_id uuid NULL,
                    request_body_blob_id uuid NULL,
                    response_headers_blob_id uuid NULL,
                    response_body_blob_id uuid NULL,
                    redirect_chain_blob_id uuid NULL,
                    response_body_sha256 character varying(64) NULL,
                    response_body_preview character varying(4096) NULL,
                    response_body_truncated boolean NOT NULL DEFAULT false
                );

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_http_request_queue_asset_id"
                    ON http_request_queue (asset_id);
                CREATE INDEX IF NOT EXISTS "IX_http_request_queue_State_NextAttemptAtUtc"
                    ON http_request_queue (state, next_attempt_at_utc);
                CREATE INDEX IF NOT EXISTS ix_http_request_queue_domain_started
                    ON http_request_queue (domain_key, started_at_utc);

                CREATE TABLE IF NOT EXISTS http_request_queue_settings (
                    id integer NOT NULL PRIMARY KEY,
                    enabled boolean NOT NULL,
                    global_requests_per_minute integer NOT NULL,
                    per_domain_requests_per_minute integer NOT NULL,
                    max_concurrency integer NOT NULL,
                    request_timeout_seconds integer NOT NULL,
                    rotate_user_agents boolean NOT NULL DEFAULT false,
                    custom_user_agents_json jsonb NULL,
                    randomize_header_order boolean NOT NULL DEFAULT false,
                    use_random_jitter boolean NOT NULL DEFAULT false,
                    min_jitter_ms integer NOT NULL DEFAULT 0,
                    max_jitter_ms integer NOT NULL DEFAULT 1000,
                    spoof_referer boolean NOT NULL DEFAULT false,
                    custom_headers_json jsonb NULL,
                    updated_at_utc timestamp with time zone NOT NULL DEFAULT now()
                );

                CREATE TABLE IF NOT EXISTS worker_heartbeats (
                    "HostName" character varying(256) NOT NULL,
                    "WorkerKey" character varying(64) NOT NULL,
                    "LastHeartbeatUtc" timestamp with time zone NOT NULL,
                    "ActiveConsumerCount" integer NOT NULL DEFAULT 0,
                    "ProcessId" integer NOT NULL DEFAULT 0,
                    "Version" text NULL,
                    "IsHealthy" boolean NOT NULL DEFAULT true,
                    "HealthMessage" text NULL,
                    CONSTRAINT "PK_worker_heartbeats" PRIMARY KEY ("HostName", "WorkerKey")
                );

                CREATE TABLE IF NOT EXISTS worker_cancellations (
                    "MessageId" uuid NOT NULL PRIMARY KEY,
                    "RequestedAtUtc" timestamp with time zone NOT NULL,
                    "Reason" text NULL
                );

                CREATE TABLE IF NOT EXISTS worker_switches (
                    "WorkerKey" character varying(64) NOT NULL PRIMARY KEY,
                    "IsEnabled" boolean NOT NULL DEFAULT true,
                    "UpdatedAtUtc" timestamp with time zone NOT NULL DEFAULT now()
                );

                CREATE TABLE IF NOT EXISTS worker_scale_targets (
                    scale_key character varying(64) NOT NULL PRIMARY KEY,
                    desired_count integer NOT NULL DEFAULT 0,
                    updated_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                    CONSTRAINT ck_worker_scale_targets_desired_count_nonnegative CHECK (desired_count >= 0)
                );

                CREATE TABLE IF NOT EXISTS worker_scaling_settings (
                    scale_key character varying(64) NOT NULL PRIMARY KEY,
                    min_tasks integer NOT NULL DEFAULT 0,
                    max_tasks integer NOT NULL DEFAULT 0,
                    target_backlog_per_task integer NOT NULL DEFAULT 25,
                    updated_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                    CONSTRAINT ck_worker_scaling_settings_min_nonnegative CHECK (min_tasks >= 0),
                    CONSTRAINT ck_worker_scaling_settings_max_gte_min CHECK (max_tasks >= min_tasks),
                    CONSTRAINT ck_worker_scaling_settings_target_positive CHECK (target_backlog_per_task > 0)
                );

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
                    processed_at_utc timestamp with time zone NOT NULL,
                    CONSTRAINT ux_inbox_messages_event_consumer UNIQUE (event_id, consumer)
                );

                CREATE TABLE IF NOT EXISTS system_errors (
                    "Id" uuid NOT NULL PRIMARY KEY,
                    "Timestamp" timestamp with time zone NOT NULL,
                    "Component" character varying(128) NOT NULL,
                    "LogLevel" character varying(32) NOT NULL,
                    "LoggerName" character varying(512) NOT NULL,
                    "Message" text NOT NULL,
                    "Exception" text NULL,
                    "MachineName" character varying(256) NOT NULL,
                    "MetadataJson" jsonb NULL
                );

                CREATE INDEX IF NOT EXISTS ix_system_errors_timestamp ON system_errors ("Timestamp" DESC);
                CREATE INDEX IF NOT EXISTS ix_system_errors_component ON system_errors ("Component");

                CREATE TABLE IF NOT EXISTS cloud_resource_usage_samples (
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    sampled_at_utc timestamp with time zone NOT NULL,
                    resource_kind character varying(64) NOT NULL,
                    resource_id character varying(256) NOT NULL,
                    resource_name character varying(256) NOT NULL,
                    running_count integer NOT NULL DEFAULT 0,
                    metadata_json jsonb NULL
                );

                CREATE INDEX IF NOT EXISTS ix_cloud_resource_usage_samples_kind_resource_sampled
                    ON cloud_resource_usage_samples (resource_kind, resource_id, sampled_at_utc);

                CREATE TABLE IF NOT EXISTS ec2_worker_machines (
                    id uuid NOT NULL PRIMARY KEY,
                    name character varying(128) NOT NULL,
                    instance_id character varying(64) NULL,
                    aws_state character varying(64) NOT NULL,
                    public_ip_address character varying(64) NULL,
                    private_ip_address character varying(64) NULL,
                    instance_type character varying(64) NULL,
                    last_command_id character varying(128) NULL,
                    last_command_status character varying(64) NULL,
                    status_message character varying(1024) NULL,
                    spider_workers integer NOT NULL DEFAULT 0,
                    enum_workers integer NOT NULL DEFAULT 0,
                    portscan_workers integer NOT NULL DEFAULT 0,
                    highvalue_workers integer NOT NULL DEFAULT 0,
                    technology_identification_workers integer NOT NULL DEFAULT 0,
                    created_at_utc timestamp with time zone NOT NULL,
                    updated_at_utc timestamp with time zone NOT NULL,
                    last_applied_at_utc timestamp with time zone NULL,
                    CONSTRAINT ck_ec2_worker_machines_spider_nonnegative CHECK (spider_workers >= 0),
                    CONSTRAINT ck_ec2_worker_machines_enum_nonnegative CHECK (enum_workers >= 0),
                    CONSTRAINT ck_ec2_worker_machines_portscan_nonnegative CHECK (portscan_workers >= 0),
                    CONSTRAINT ck_ec2_worker_machines_highvalue_nonnegative CHECK (highvalue_workers >= 0),
                    CONSTRAINT ck_ec2_worker_machines_techid_nonnegative CHECK (technology_identification_workers >= 0)
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ix_ec2_worker_machines_instance_id
                    ON ec2_worker_machines (instance_id)
                    WHERE instance_id IS NOT NULL;
                CREATE INDEX IF NOT EXISTS ix_ec2_worker_machines_aws_state ON ec2_worker_machines (aws_state);

                CREATE TABLE IF NOT EXISTS target_scan_states (
                    correlation_id uuid NOT NULL PRIMARY KEY,
                    current_state character varying(64) NOT NULL,
                    target_domain character varying(253) NOT NULL,
                    created_at_utc timestamp with time zone NOT NULL,
                    updated_at_utc timestamp with time zone NOT NULL
                );
                """,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task EnsureHttpRequestQueueDefaultsAsync(ArgusDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE http_request_queue
                    ADD COLUMN IF NOT EXISTS attempt_count integer NOT NULL DEFAULT 0,
                    ADD COLUMN IF NOT EXISTS max_attempts integer NOT NULL DEFAULT 3,
                    ADD COLUMN IF NOT EXISTS redirect_count integer NOT NULL DEFAULT 0,
                    ADD COLUMN IF NOT EXISTS response_body_truncated boolean NOT NULL DEFAULT false;

                ALTER TABLE http_request_queue
                    ALTER COLUMN attempt_count SET DEFAULT 0,
                    ALTER COLUMN max_attempts SET DEFAULT 3,
                    ALTER COLUMN redirect_count SET DEFAULT 0,
                    ALTER COLUMN response_body_truncated SET DEFAULT false;

                UPDATE http_request_queue
                SET
                    attempt_count = COALESCE(attempt_count, 0),
                    max_attempts = COALESCE(max_attempts, 3),
                    redirect_count = COALESCE(redirect_count, 0),
                    response_body_truncated = COALESCE(response_body_truncated, false);

                ALTER TABLE http_request_queue
                    ALTER COLUMN attempt_count SET NOT NULL,
                    ALTER COLUMN max_attempts SET NOT NULL,
                    ALTER COLUMN redirect_count SET NOT NULL,
                    ALTER COLUMN response_body_truncated SET NOT NULL;
                """,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task BackfillHttpRequestQueueAsync(ArgusDbContext db, ILogger logger, CancellationToken cancellationToken)
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
                    attempt_count,
                    max_attempts,
                    created_at_utc,
                    updated_at_utc,
                    next_attempt_at_utc,
                    redirect_count,
                    response_body_truncated
                )
                SELECT
                    gen_random_uuid(),
                    a.asset_id,
                    a.target_id,
                    a.asset_kind,
                    'GET',
                    CASE
                        WHEN a.asset_kind IN (0, 1) THEN 'https://' || trim(trailing '/' from a.raw_value) || '/'
                        WHEN a.raw_value ~* '^https?://' THEN a.raw_value
                        ELSE 'https://' || a.raw_value
                    END,
                    lower(
                        CASE
                            WHEN a.asset_kind IN (0, 1) THEN trim(trailing '/' from a.raw_value)
                            ELSE regexp_replace(regexp_replace(a.raw_value, '^https?://', '', 'i'), '[:/].*$', '')
                        END
                    ),
                    'Queued',
                    0,
                    0,
                    3,
                    COALESCE(a.discovered_at_utc, now()),
                    now(),
                    now(),
                    0,
                    false
                FROM asset_projection a
                WHERE a.asset_id IS NOT NULL
                  AND a.target_id IS NOT NULL
                  AND a.asset_kind IS NOT NULL
                  AND a.raw_value IS NOT NULL
                  AND a.lifecycle_status = 'Queued'
                  AND a.asset_kind IN (0, 1, 10, 11, 12, 33)
                  AND (
                      a.asset_kind IN (0, 1)
                      OR a.raw_value ~* '^https?://'
                      OR a.raw_value !~* '^[a-zA-Z][a-zA-Z0-9+.-]*:'
                  )
                  AND NOT EXISTS (
                      SELECT 1 FROM http_request_queue q WHERE q.asset_id = a.asset_id
                  );
                """,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Normalize legacy statuses after introducing Queued as the default initial status.</summary>
    private static async Task BackfillLegacyDiscoveredAssetsAsync(ArgusDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        await db.Assets
            .Where(a => a.LifecycleStatus == "Discovered")
            .ExecuteUpdateAsync(
                s => s.SetProperty(a => a.LifecycleStatus, AssetLifecycleStatus.Queued),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
