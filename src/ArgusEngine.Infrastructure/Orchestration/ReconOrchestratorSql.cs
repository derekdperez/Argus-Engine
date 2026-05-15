using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ArgusEngine.Infrastructure.Orchestration;

internal static class ReconOrchestratorSql
{
    public static async Task EnsureSchemaAsync(ArgusDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS recon_orchestrator_states (
                target_id uuid NOT NULL PRIMARY KEY REFERENCES recon_targets("Id") ON DELETE CASCADE,
                orchestrator_type character varying(64) NOT NULL DEFAULT 'ReconOrchestrator',
                status character varying(32) NOT NULL DEFAULT 'active',
                config_json jsonb NOT NULL DEFAULT '{{}}',
                state_json jsonb NOT NULL DEFAULT '{{}}',
                lease_owner character varying(256) NULL,
                lease_until_utc timestamp with time zone NULL,
                attached_by character varying(256) NOT NULL DEFAULT 'system',
                attached_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                started_at_utc timestamp with time zone NULL,
                last_tick_at_utc timestamp with time zone NULL,
                completed_at_utc timestamp with time zone NULL,
                updated_at_utc timestamp with time zone NOT NULL DEFAULT now()
            );

            CREATE INDEX IF NOT EXISTS ix_recon_orchestrator_states_status
                ON recon_orchestrator_states (status, updated_at_utc);

            CREATE INDEX IF NOT EXISTS ix_recon_orchestrator_states_lease
                ON recon_orchestrator_states (lease_until_utc);

            CREATE TABLE IF NOT EXISTS recon_orchestrator_provider_runs (
                id uuid NOT NULL PRIMARY KEY,
                target_id uuid NOT NULL REFERENCES recon_targets("Id") ON DELETE CASCADE,
                provider character varying(64) NOT NULL,
                status character varying(32) NOT NULL,
                requested_at_utc timestamp with time zone NOT NULL,
                started_at_utc timestamp with time zone NULL,
                completed_at_utc timestamp with time zone NULL,
                emitted_subdomain_count integer NOT NULL DEFAULT 0,
                retry_count integer NOT NULL DEFAULT 0,
                last_retried_at_utc timestamp with time zone NULL,
                timeout_at_utc timestamp with time zone NULL,
                status_reason text NULL,
                correlation_id uuid NULL,
                event_id uuid NULL,
                last_error text NULL,
                updated_at_utc timestamp with time zone NOT NULL DEFAULT now()
            );

            ALTER TABLE recon_orchestrator_provider_runs
                ADD COLUMN IF NOT EXISTS retry_count integer NOT NULL DEFAULT 0;
            ALTER TABLE recon_orchestrator_provider_runs
                ADD COLUMN IF NOT EXISTS last_retried_at_utc timestamp with time zone NULL;
            ALTER TABLE recon_orchestrator_provider_runs
                ADD COLUMN IF NOT EXISTS timeout_at_utc timestamp with time zone NULL;
            ALTER TABLE recon_orchestrator_provider_runs
                ADD COLUMN IF NOT EXISTS status_reason text NULL;

            CREATE UNIQUE INDEX IF NOT EXISTS ux_recon_provider_runs_target_provider
                ON recon_orchestrator_provider_runs (target_id, provider);

            CREATE INDEX IF NOT EXISTS ix_recon_provider_runs_status
                ON recon_orchestrator_provider_runs (target_id, status);

            CREATE INDEX IF NOT EXISTS ix_recon_provider_runs_retry
                ON recon_orchestrator_provider_runs (status, updated_at_utc, retry_count);

            CREATE TABLE IF NOT EXISTS recon_orchestrator_provider_discoveries (
                id uuid NOT NULL PRIMARY KEY,
                target_id uuid NOT NULL REFERENCES recon_targets("Id") ON DELETE CASCADE,
                provider character varying(64) NOT NULL,
                subdomain_key character varying(253) NOT NULL,
                status character varying(32) NOT NULL DEFAULT 'awaiting_persistence',
                persisted_asset_id uuid NULL REFERENCES stored_assets("Id") ON DELETE SET NULL,
                discovered_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                persisted_at_utc timestamp with time zone NULL,
                updated_at_utc timestamp with time zone NOT NULL DEFAULT now()
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_recon_provider_discoveries_target_provider_subdomain
                ON recon_orchestrator_provider_discoveries (target_id, provider, subdomain_key);

            CREATE INDEX IF NOT EXISTS ix_recon_provider_discoveries_status
                ON recon_orchestrator_provider_discoveries (target_id, provider, status);

            CREATE TABLE IF NOT EXISTS recon_orchestrator_subdomain_states (
                id uuid NOT NULL PRIMARY KEY,
                target_id uuid NOT NULL REFERENCES recon_targets("Id") ON DELETE CASCADE,
                subdomain_asset_id uuid NOT NULL REFERENCES stored_assets("Id") ON DELETE CASCADE,
                subdomain_key character varying(253) NOT NULL,
                status character varying(32) NOT NULL,
                confirmed_url_count bigint NOT NULL DEFAULT 0,
                unconfirmed_url_count bigint NOT NULL DEFAULT 0,
                pending_url_count bigint NOT NULL DEFAULT 0,
                in_flight_url_count bigint NOT NULL DEFAULT 0,
                failed_url_count bigint NOT NULL DEFAULT 0,
                last_checked_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                updated_at_utc timestamp with time zone NOT NULL DEFAULT now()
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_recon_subdomain_states_target_subdomain
                ON recon_orchestrator_subdomain_states (target_id, subdomain_key);

            CREATE INDEX IF NOT EXISTS ix_recon_subdomain_states_status
                ON recon_orchestrator_subdomain_states (target_id, status);

            CREATE TABLE IF NOT EXISTS recon_orchestrator_subdomain_seed_requests (
                id uuid NOT NULL PRIMARY KEY,
                target_id uuid NOT NULL REFERENCES recon_targets("Id") ON DELETE CASCADE,
                subdomain_asset_id uuid NOT NULL REFERENCES stored_assets("Id") ON DELETE CASCADE,
                subdomain_key character varying(253) NOT NULL,
                scheme character varying(8) NOT NULL,
                seed_url text NOT NULL,
                status character varying(32) NOT NULL DEFAULT 'requested',
                event_id uuid NOT NULL,
                correlation_id uuid NOT NULL,
                requested_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                dispatched_at_utc timestamp with time zone NULL,
                updated_at_utc timestamp with time zone NOT NULL DEFAULT now()
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_recon_seed_requests_target_subdomain_scheme
                ON recon_orchestrator_subdomain_seed_requests (target_id, subdomain_key, scheme);

            CREATE INDEX IF NOT EXISTS ix_recon_seed_requests_status
                ON recon_orchestrator_subdomain_seed_requests (target_id, status, updated_at_utc);

            CREATE TABLE IF NOT EXISTS recon_orchestrator_profile_assignments (
                id uuid NOT NULL PRIMARY KEY,
                target_id uuid NOT NULL REFERENCES recon_targets("Id") ON DELETE CASCADE,
                subdomain_key character varying(253) NOT NULL,
                machine_key character varying(256) NOT NULL,
                machine_name character varying(256) NULL,
                public_ip_address character varying(64) NULL,
                profile_index integer NOT NULL,
                device_type character varying(32) NOT NULL,
                browser character varying(32) NOT NULL,
                operating_system character varying(32) NOT NULL,
                hardware_age_years integer NOT NULL,
                user_agent text NOT NULL,
                accept_language character varying(128) NOT NULL,
                headers_json jsonb NOT NULL,
                header_order_seed integer NOT NULL,
                random_delay_enabled boolean NOT NULL,
                random_delay_min_ms integer NOT NULL,
                random_delay_max_ms integer NOT NULL,
                requests_per_minute_per_subdomain integer NOT NULL,
                request_count bigint NOT NULL DEFAULT 0,
                created_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                updated_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                last_used_at_utc timestamp with time zone NULL,
                last_request_url text NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_recon_profile_assignments_target_subdomain_machine
                ON recon_orchestrator_profile_assignments (target_id, subdomain_key, machine_key);

            CREATE INDEX IF NOT EXISTS ix_recon_profile_assignments_target_subdomain
                ON recon_orchestrator_profile_assignments (target_id, subdomain_key);
            """,
            cancellationToken).ConfigureAwait(false);
    }
}
