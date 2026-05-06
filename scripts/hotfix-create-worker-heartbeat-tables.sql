-- Argus Engine DB hotfix: create operational worker heartbeat tables missing from existing databases.
-- Safe to run more than once.

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

CREATE INDEX IF NOT EXISTS ix_worker_heartbeats_worker_key
    ON worker_heartbeats ("WorkerKey");

CREATE INDEX IF NOT EXISTS ix_worker_heartbeats_last_heartbeat_utc
    ON worker_heartbeats ("LastHeartbeatUtc");

CREATE TABLE IF NOT EXISTS worker_cancellations (
    "MessageId" uuid NOT NULL PRIMARY KEY,
    "RequestedAtUtc" timestamp with time zone NOT NULL DEFAULT now(),
    "Reason" text NULL
);

COMMIT;
