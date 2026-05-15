ALTER TABLE http_request_queue_settings
    ADD COLUMN IF NOT EXISTS proxy_routing_enabled boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS proxy_sticky_subdomains_enabled boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS proxy_assignment_salt text NULL DEFAULT 'argus-proxy-v1',
    ADD COLUMN IF NOT EXISTS proxy_servers_json text NULL DEFAULT '[]';
