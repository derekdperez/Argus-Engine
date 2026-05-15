-- Repairs legacy/live databases where http_request_queue gained NOT NULL
-- retry bookkeeping columns but the startup backfill inserts legacy rows
-- without explicitly setting those columns.
--
-- This makes the existing backfill safe:
--   INSERT INTO http_request_queue (..., priority, created_at_utc, ...)
-- by letting PostgreSQL fill retry defaults.

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.tables
        WHERE table_schema = 'public'
          AND table_name = 'http_request_queue'
    ) THEN
        ALTER TABLE public.http_request_queue
            ALTER COLUMN attempt_count SET DEFAULT 0,
            ALTER COLUMN max_attempts SET DEFAULT 3,
            ALTER COLUMN redirect_count SET DEFAULT 0,
            ALTER COLUMN response_body_truncated SET DEFAULT false;

        UPDATE public.http_request_queue
        SET attempt_count = 0
        WHERE attempt_count IS NULL;

        UPDATE public.http_request_queue
        SET max_attempts = 3
        WHERE max_attempts IS NULL;

        UPDATE public.http_request_queue
        SET redirect_count = 0
        WHERE redirect_count IS NULL;

        UPDATE public.http_request_queue
