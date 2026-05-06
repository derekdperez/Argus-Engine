-- Runtime schema hotfix for actively deployed Argus CommandCenter.
-- Safe to run more than once.
--
-- Why this exists:
-- Existing deployments can have databases created by older builds. EF EnsureCreated()
-- does not evolve an existing schema, so new columns/tables used by the latest model
-- must be patched in-place.

BEGIN;

CREATE TABLE IF NOT EXISTS worker_heartbeats (
    "HostName" character varying(256) NOT NULL PRIMARY KEY,
    "WorkerKey" character varying(64) NOT NULL,
    "LastHeartbeatUtc" timestamp with time zone NOT NULL DEFAULT now(),
    "ActiveConsumerCount" integer NOT NULL DEFAULT 0,
    "ProcessId" integer NOT NULL DEFAULT 0,
    "Version" text NULL,
    "IsHealthy" boolean NOT NULL DEFAULT true,
    "HealthMessage" text NULL
);

ALTER TABLE worker_heartbeats
    ADD COLUMN IF NOT EXISTS "WorkerKey" character varying(64) NOT NULL DEFAULT 'unknown',
    ADD COLUMN IF NOT EXISTS "LastHeartbeatUtc" timestamp with time zone NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS "ActiveConsumerCount" integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS "ProcessId" integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS "Version" text NULL,
    ADD COLUMN IF NOT EXISTS "IsHealthy" boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS "HealthMessage" text NULL;

CREATE INDEX IF NOT EXISTS ix_worker_heartbeats_worker_key
    ON worker_heartbeats ("WorkerKey");

CREATE INDEX IF NOT EXISTS ix_worker_heartbeats_last_heartbeat_utc
    ON worker_heartbeats ("LastHeartbeatUtc");

CREATE TABLE IF NOT EXISTS worker_cancellations (
    "MessageId" uuid NOT NULL PRIMARY KEY,
    "RequestedAtUtc" timestamp with time zone NOT NULL DEFAULT now(),
    "Reason" text NULL
);

CREATE TABLE IF NOT EXISTS bus_journal (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    direction character varying(16) NOT NULL,
    message_type character varying(256) NOT NULL,
    consumer_type character varying(512) NULL,
    payload_json text NOT NULL,
    occurred_at_utc timestamp with time zone NOT NULL DEFAULT now(),
    host_name character varying(256) NOT NULL DEFAULT '',
    "Status" character varying(32) NOT NULL DEFAULT 'Completed',
    "DurationMs" double precision NULL,
    "Error" text NULL,
    "MessageId" uuid NULL
);

ALTER TABLE bus_journal
    ADD COLUMN IF NOT EXISTS host_name character varying(256) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS "Status" character varying(32) NOT NULL DEFAULT 'Completed',
    ADD COLUMN IF NOT EXISTS "DurationMs" double precision NULL,
    ADD COLUMN IF NOT EXISTS "Error" text NULL,
    ADD COLUMN IF NOT EXISTS "MessageId" uuid NULL;

CREATE INDEX IF NOT EXISTS ix_bus_journal_occurred_at_utc
    ON bus_journal (occurred_at_utc);

CREATE INDEX IF NOT EXISTS ix_bus_journal_message_id
    ON bus_journal ("MessageId");

COMMIT;
