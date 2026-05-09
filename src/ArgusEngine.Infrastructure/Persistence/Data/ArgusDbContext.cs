using Microsoft.EntityFrameworkCore;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Application.Sagas;

namespace ArgusEngine.Infrastructure.Data;

public sealed class ArgusDbContext(DbContextOptions<ArgusDbContext> options) : DbContext(options)
{
    public DbSet<ReconTarget> Targets => Set<ReconTarget>();
    public DbSet<StoredAsset> Assets => Set<StoredAsset>();
    public DbSet<AssetRelationship> AssetRelationships => Set<AssetRelationship>();
    public DbSet<BusJournalEntry> BusJournal => Set<BusJournalEntry>();
    public DbSet<WorkerSwitch> WorkerSwitches => Set<WorkerSwitch>();
    public DbSet<HighValueFinding> HighValueFindings => Set<HighValueFinding>();
    public DbSet<HttpRequestQueueItem> HttpRequestQueue => Set<HttpRequestQueueItem>();
    public DbSet<HttpRequestQueueSettings> HttpRequestQueueSettings => Set<HttpRequestQueueSettings>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<AssetTag> AssetTags => Set<AssetTag>();
    public DbSet<TechnologyDetection> TechnologyDetections => Set<TechnologyDetection>();
    public DbSet<TechnologyObservation> TechnologyObservations => Set<TechnologyObservation>();
    public DbSet<TechnologyDetectionRun> TechnologyDetectionRuns => Set<TechnologyDetectionRun>();
    public DbSet<TechnologyObservationEvidence> TechnologyObservationEvidence => Set<TechnologyObservationEvidence>();
    public DbSet<CloudResourceUsageSample> CloudResourceUsageSamples => Set<CloudResourceUsageSample>();
    public DbSet<WorkerScaleTarget> WorkerScaleTargets => Set<WorkerScaleTarget>();
    public DbSet<WorkerScalingSetting> WorkerScalingSettings => Set<WorkerScalingSetting>();
    public DbSet<SystemError> SystemErrors => Set<SystemError>();
    public DbSet<Ec2WorkerMachine> Ec2WorkerMachines => Set<Ec2WorkerMachine>();
    public DbSet<TargetScanState> TargetScanStates => Set<TargetScanState>();
    public DbSet<WorkerHeartbeat> WorkerHeartbeats => Set<WorkerHeartbeat>();
    public DbSet<WorkerCancellation> WorkerCancellations => Set<WorkerCancellation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReconTarget>(e =>
        {
            e.ToTable("recon_targets");
            e.HasKey(x => x.Id);
            e.Property(x => x.RootDomain).HasMaxLength(253).IsRequired();
            e.HasIndex(x => x.RootDomain).IsUnique();
        });

        modelBuilder.Entity<TargetScanState>(e =>
        {
            e.ToTable("target_scan_states");
            e.HasKey(x => x.CorrelationId);
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.CurrentState).HasColumnName("current_state").HasMaxLength(64).IsRequired();
            e.Property(x => x.TargetDomain).HasColumnName("target_domain").HasMaxLength(253).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at_utc");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at_utc");
        });

        modelBuilder.Entity<StoredAsset>(e =>
        {
            e.ToTable("stored_assets");
            e.HasKey(x => x.Id);
            e.Property(x => x.CanonicalKey).HasMaxLength(2048).IsRequired();
            e.Property(x => x.RawValue).HasMaxLength(4096).IsRequired();
            e.Property(x => x.Category).HasColumnName("asset_category").HasConversion<short>().HasColumnType("smallint");
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(512);
            e.Property(x => x.LastSeenAtUtc).HasColumnName("last_seen_at_utc");
            e.Property(x => x.Confidence).HasColumnName("confidence").HasPrecision(5, 4);
            e.Property(x => x.DiscoveredBy).HasColumnName("discovered_by").HasMaxLength(128).IsRequired();
            e.Property(x => x.DiscoveryContext).HasColumnName("discovery_context").HasMaxLength(512).IsRequired();
            e.Property(x => x.LifecycleStatus).HasMaxLength(32).IsRequired();
            e.Property(x => x.TypeDetailsJson).HasColumnName("type_details_json");
            e.Property(x => x.FinalUrl).HasColumnName("final_url").HasMaxLength(4096);
            e.Property(x => x.RedirectCount).HasColumnName("redirect_count");
            e.Property(x => x.RedirectChainJson).HasColumnName("redirect_chain_json").HasColumnType("jsonb");
            e.HasIndex(x => new { x.TargetId, x.CanonicalKey }).IsUnique();
            e.HasIndex(x => new { x.TargetId, x.Kind });
            e.HasIndex(x => new { x.TargetId, x.Category });
            e.HasOne(x => x.Target)
                .WithMany()
                .HasForeignKey(x => x.TargetId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Tag>(e =>
        {
            e.ToTable("tags");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Slug).HasColumnName("slug").HasMaxLength(256).IsRequired();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(256).IsRequired();
            e.Property(x => x.TagType).HasColumnName("tag_type").HasMaxLength(64).IsRequired();
            e.Property(x => x.Source).HasColumnName("source").HasMaxLength(128).IsRequired();
            e.Property(x => x.SourceKey).HasColumnName("source_key").HasMaxLength(256);
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(1024);
            e.Property(x => x.Website).HasColumnName("website").HasMaxLength(1024);
            e.Property(x => x.MetadataJson).HasColumnName("metadata_json").HasColumnType("jsonb");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => new { x.TagType, x.Source });
        });

        modelBuilder.Entity<AssetTag>(e =>
        {
            e.ToTable("asset_tags");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TargetId).HasColumnName("target_id");
            e.Property(x => x.AssetId).HasColumnName("asset_id");
            e.Property(x => x.TagId).HasColumnName("tag_id");
            e.Property(x => x.Confidence).HasColumnName("confidence").HasPrecision(5, 4);
            e.Property(x => x.Source).HasColumnName("source").HasMaxLength(128).IsRequired();
            e.Property(x => x.EvidenceJson).HasColumnName("evidence_json").HasColumnType("jsonb");
            e.Property(x => x.FirstSeenAtUtc).HasColumnName("first_seen_at_utc");
            e.Property(x => x.LastSeenAtUtc).HasColumnName("last_seen_at_utc");
            e.HasIndex(x => new { x.AssetId, x.TagId }).IsUnique();
            e.HasIndex(x => new { x.TargetId, x.TagId });
            e.HasOne(x => x.Asset).WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Tag).WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TechnologyDetection>(e =>
        {
            e.ToTable("technology_detections");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TargetId).HasColumnName("target_id");
            e.Property(x => x.AssetId).HasColumnName("asset_id");
            e.Property(x => x.TagId).HasColumnName("tag_id");
            e.Property(x => x.TechnologyName).HasColumnName("technology_name").HasMaxLength(256).IsRequired();
            e.Property(x => x.EvidenceSource).HasColumnName("evidence_source").HasMaxLength(64).IsRequired();
            e.Property(x => x.EvidenceKey).HasColumnName("evidence_key").HasMaxLength(512);
            e.Property(x => x.Pattern).HasColumnName("pattern").HasMaxLength(2048);
            e.Property(x => x.MatchedText).HasColumnName("matched_text").HasMaxLength(512);
            e.Property(x => x.Version).HasColumnName("version").HasMaxLength(128);
            e.Property(x => x.Confidence).HasColumnName("confidence").HasPrecision(5, 4);
            e.Property(x => x.EvidenceHash).HasColumnName("evidence_hash").HasMaxLength(64).IsRequired();
            e.Property(x => x.DetectedAtUtc).HasColumnName("detected_at_utc");
            e.HasIndex(x => new { x.AssetId, x.TagId, x.EvidenceHash }).IsUnique();
            e.HasIndex(x => new { x.TargetId, x.TagId });
            e.HasIndex(x => x.DetectedAtUtc);
        });

        modelBuilder.Entity<TechnologyObservation>(e =>
        {
            e.ToTable("technology_observations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.RunId).HasColumnName("run_id");
            e.Property(x => x.TargetId).HasColumnName("target_id");
            e.Property(x => x.AssetId).HasColumnName("asset_id");
            e.Property(x => x.FingerprintId).HasColumnName("fingerprint_id");
            e.Property(x => x.CatalogHash).HasColumnName("catalog_hash");
            e.Property(x => x.TechnologyName).HasColumnName("technology_name");
            e.Property(x => x.Vendor).HasColumnName("vendor");
            e.Property(x => x.Product).HasColumnName("product");
            e.Property(x => x.Version).HasColumnName("version");
            e.Property(x => x.ConfidenceScore).HasColumnName("confidence_score").HasPrecision(5, 4);
            e.Property(x => x.SourceType).HasColumnName("source_type");
            e.Property(x => x.DetectionMode).HasColumnName("detection_mode");
            e.Property(x => x.DedupeKey).HasColumnName("dedupe_key");
            e.Property(x => x.FirstSeenUtc).HasColumnName("first_seen_utc");
            e.Property(x => x.LastSeenUtc).HasColumnName("last_seen_utc");
            e.Property(x => x.MetadataJson).HasColumnName("metadata_json").HasColumnType("jsonb");
            e.HasIndex(x => new { x.TargetId, x.AssetId, x.DedupeKey }).IsUnique();
            e.HasIndex(x => new { x.TargetId, x.TechnologyName });
            e.HasIndex(x => new { x.AssetId, x.LastSeenUtc });
        });

        modelBuilder.Entity<TechnologyDetectionRun>(e =>
        {
            e.ToTable("technology_detection_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TargetId).HasColumnName("target_id");
            e.Property(x => x.CatalogHash).HasColumnName("catalog_hash");
            e.Property(x => x.Mode).HasColumnName("mode");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc");
            e.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
            e.HasIndex(x => new { x.TargetId, x.CreatedAtUtc });
            e.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<TechnologyObservationEvidence>(e =>
        {
            e.ToTable("technology_observation_evidence");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ObservationId).HasColumnName("observation_id");
            e.HasOne<TechnologyObservation>().WithMany().HasForeignKey(x => x.ObservationId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.SignalId).HasColumnName("signal_id");
            e.Property(x => x.EvidenceType).HasColumnName("evidence_type");
            e.Property(x => x.EvidenceKey).HasColumnName("evidence_key");
            e.Property(x => x.MatchedValueRedacted).HasColumnName("matched_value_redacted");
            e.Property(x => x.ArtifactId).HasColumnName("artifact_id");
            e.Property(x => x.EvidenceHash).HasColumnName("evidence_hash");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            e.HasIndex(x => new { x.ObservationId, x.EvidenceHash }).IsUnique();
            e.HasIndex(x => x.ObservationId);
        });

        modelBuilder.Entity<AssetRelationship>(e =>
        {
            e.ToTable("asset_relationships", t => t.HasCheckConstraint("ck_asset_relationship_no_self", "parent_asset_id <> child_asset_id"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TargetId).HasColumnName("target_id");
            e.Property(x => x.ParentAssetId).HasColumnName("parent_asset_id");
            e.Property(x => x.ChildAssetId).HasColumnName("child_asset_id");
            e.Property(x => x.RelationshipType).HasColumnName("relationship_type").HasConversion<short>().HasColumnType("smallint");
            e.Property(x => x.IsPrimary).HasColumnName("is_primary");
            e.Property(x => x.Confidence).HasColumnName("confidence").HasPrecision(5, 4);
            e.Property(x => x.DiscoveredBy).HasColumnName("discovered_by").HasMaxLength(128).IsRequired();
            e.Property(x => x.DiscoveryContext).HasColumnName("discovery_context").HasMaxLength(512).IsRequired();
            e.Property(x => x.PropertiesJson).HasColumnName("properties_json").HasColumnType("jsonb");
            e.Property(x => x.FirstSeenAtUtc).HasColumnName("first_seen_at_utc");
            e.Property(x => x.LastSeenAtUtc).HasColumnName("last_seen_at_utc");
            e.HasIndex(x => new { x.TargetId, x.ParentAssetId, x.ChildAssetId, x.RelationshipType }).IsUnique();
            e.HasOne(x => x.Target)
                .WithMany()
                .HasForeignKey(x => x.TargetId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ParentAsset)
                .WithMany()
                .HasForeignKey(x => x.ParentAssetId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ChildAsset)
                .WithMany()
                .HasForeignKey(x => x.ChildAssetId)
                .OnDelete(DeleteBehavior.Cascade);
        });


        modelBuilder.Entity<BusJournalEntry>(e =>
        {
            e.ToTable("bus_journal");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.Direction).HasColumnName("direction").HasMaxLength(16).IsRequired();
            e.Property(x => x.MessageType).HasColumnName("message_type").HasMaxLength(256).IsRequired();
            e.Property(x => x.ConsumerType).HasColumnName("consumer_type").HasMaxLength(2048);
            e.Property(x => x.PayloadJson).HasColumnName("payload_json").IsRequired();
            e.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            e.Property(x => x.HostName).HasMaxLength(256).IsRequired().HasColumnName("host_name");
            e.Property(x => x.Status).HasMaxLength(32).IsRequired().HasDefaultValue("Completed");
            e.Property(x => x.DurationMs);
            e.Property(x => x.Error);
            e.Property(x => x.MessageId);
            e.HasIndex(x => x.OccurredAtUtc);
            e.HasIndex(x => x.MessageId);
        });

        modelBuilder.Entity<WorkerHeartbeat>(e =>
        {
            e.ToTable("worker_heartbeats");
            e.HasKey(x => new { x.HostName, x.WorkerKey });
            e.Property(x => x.HostName).HasMaxLength(256);
            e.Property(x => x.WorkerKey).HasMaxLength(64);
        });

        modelBuilder.Entity<WorkerSwitch>(e =>
        {
            e.ToTable("worker_switches");
            e.HasKey(x => x.WorkerKey);
            e.Property(x => x.WorkerKey).HasMaxLength(64);
        });

        modelBuilder.Entity<WorkerScaleTarget>(e =>
        {
            e.ToTable("worker_scale_targets");
            e.HasKey(x => x.ScaleKey);
            e.Property(x => x.ScaleKey).HasColumnName("scale_key").HasMaxLength(64);
            e.Property(x => x.DesiredCount).HasColumnName("desired_count");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            e.ToTable(t => t.HasCheckConstraint("ck_worker_scale_targets_desired_count_nonnegative", "desired_count >= 0"));
        });

        modelBuilder.Entity<WorkerScalingSetting>(e =>
        {
            e.ToTable("worker_scaling_settings");
            e.HasKey(x => x.ScaleKey);
            e.Property(x => x.ScaleKey).HasColumnName("scale_key").HasMaxLength(64);
            e.Property(x => x.MinTasks).HasColumnName("min_tasks");
            e.Property(x => x.MaxTasks).HasColumnName("max_tasks");
            e.Property(x => x.TargetBacklogPerTask).HasColumnName("target_backlog_per_task");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_worker_scaling_settings_min_nonnegative", "min_tasks >= 0");
                t.HasCheckConstraint("ck_worker_scaling_settings_max_gte_min", "max_tasks >= min_tasks");
                t.HasCheckConstraint("ck_worker_scaling_settings_target_positive", "target_backlog_per_task > 0");
            });
        });

        modelBuilder.Entity<Ec2WorkerMachine>(e =>
        {
            e.ToTable("ec2_worker_machines", t =>
            {
                t.HasCheckConstraint("ck_ec2_worker_machines_spider_nonnegative", "spider_workers >= 0");
                t.HasCheckConstraint("ck_ec2_worker_machines_enum_nonnegative", "enum_workers >= 0");
                t.HasCheckConstraint("ck_ec2_worker_machines_portscan_nonnegative", "portscan_workers >= 0");
                t.HasCheckConstraint("ck_ec2_worker_machines_highvalue_nonnegative", "highvalue_workers >= 0");
                t.HasCheckConstraint("ck_ec2_worker_machines_techid_nonnegative", "technology_identification_workers >= 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
            e.Property(x => x.InstanceId).HasColumnName("instance_id").HasMaxLength(64);
            e.Property(x => x.AwsState).HasColumnName("aws_state").HasMaxLength(64).IsRequired();
            e.Property(x => x.PublicIpAddress).HasColumnName("public_ip_address").HasMaxLength(64);
            e.Property(x => x.PrivateIpAddress).HasColumnName("private_ip_address").HasMaxLength(64);
            e.Property(x => x.InstanceType).HasColumnName("instance_type").HasMaxLength(64);
            e.Property(x => x.LastCommandId).HasColumnName("last_command_id").HasMaxLength(128);
            e.Property(x => x.LastCommandStatus).HasColumnName("last_command_status").HasMaxLength(64);
            e.Property(x => x.StatusMessage).HasColumnName("status_message").HasMaxLength(1024);
            e.Property(x => x.SpiderWorkers).HasColumnName("spider_workers");
            e.Property(x => x.EnumWorkers).HasColumnName("enum_workers");
            e.Property(x => x.PortScanWorkers).HasColumnName("portscan_workers");
            e.Property(x => x.HighValueWorkers).HasColumnName("highvalue_workers");
            e.Property(x => x.TechnologyIdentificationWorkers).HasColumnName("technology_identification_workers");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            e.Property(x => x.LastAppliedAtUtc).HasColumnName("last_applied_at_utc");
            e.HasIndex(x => x.InstanceId).IsUnique();
            e.HasIndex(x => x.AwsState);
        });

        modelBuilder.Entity<CloudResourceUsageSample>(e =>
        {
            e.ToTable("cloud_resource_usage_samples");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.SampledAtUtc).HasColumnName("sampled_at_utc");
            e.Property(x => x.ResourceKind).HasColumnName("resource_kind").HasMaxLength(64).IsRequired();
            e.Property(x => x.ResourceId).HasColumnName("resource_id").HasMaxLength(256).IsRequired();
            e.Property(x => x.ResourceName).HasColumnName("resource_name").HasMaxLength(256).IsRequired();
            e.Property(x => x.RunningCount).HasColumnName("running_count");
            e.Property(x => x.MetadataJson).HasColumnName("metadata_json").HasColumnType("jsonb");
            e.HasIndex(x => new { x.ResourceKind, x.ResourceId, x.SampledAtUtc });
        });

        modelBuilder.Entity<HttpRequestQueueItem>(e =>
        {
            e.ToTable("http_request_queue");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.AssetId).HasColumnName("asset_id");
            e.Property(x => x.TargetId).HasColumnName("target_id");
            e.Property(x => x.AssetKind).HasColumnName("asset_kind");
            e.Property(x => x.Method).HasColumnName("method").HasMaxLength(16).IsRequired();
            e.Property(x => x.RequestUrl).HasColumnName("request_url").HasMaxLength(4096).IsRequired();
            e.Property(x => x.DomainKey).HasColumnName("domain_key").HasMaxLength(253).IsRequired();
            e.Property(x => x.State).HasColumnName("state").HasMaxLength(32).IsRequired();
            e.Property(x => x.Priority).HasColumnName("priority");
            e.Property(x => x.AttemptCount).HasColumnName("attempt_count");
            e.Property(x => x.MaxAttempts).HasColumnName("max_attempts");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            e.Property(x => x.NextAttemptAtUtc).HasColumnName("next_attempt_at_utc");
            e.Property(x => x.LockedBy).HasColumnName("locked_by").HasMaxLength(256);
            e.Property(x => x.LockedUntilUtc).HasColumnName("locked_until_utc");
            e.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc");
            e.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
            e.Property(x => x.DurationMs).HasColumnName("duration_ms");
            e.Property(x => x.LastHttpStatus).HasColumnName("last_http_status");
            e.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(2048);
            e.Property(x => x.RequestHeadersJson).HasColumnName("request_headers_json");
            e.Property(x => x.RequestBody).HasColumnName("request_body");
            e.Property(x => x.ResponseHeadersJson).HasColumnName("response_headers_json");
            e.Property(x => x.ResponseBody).HasColumnName("response_body");
            e.Property(x => x.ResponseContentType).HasColumnName("response_content_type").HasMaxLength(256);
            e.Property(x => x.ResponseContentLength).HasColumnName("response_content_length");
            e.Property(x => x.FinalUrl).HasColumnName("final_url").HasMaxLength(4096);
            e.Property(x => x.RedirectCount).HasColumnName("redirect_count");
            e.Property(x => x.RedirectChainJson).HasColumnName("redirect_chain_json").HasColumnType("jsonb");
            e.Property(x => x.ResponseBodyTruncated).HasColumnName("response_body_truncated").HasDefaultValue(false);
            e.HasIndex(x => x.AssetId).IsUnique();
            e.HasIndex(x => new { x.State, x.NextAttemptAtUtc });
            e.HasIndex(x => new { x.DomainKey, x.StartedAtUtc });
            e.HasOne(x => x.Asset)
                .WithMany()
                .HasForeignKey(x => x.AssetId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<HttpRequestQueueSettings>(e =>
        {
            e.ToTable("http_request_queue_settings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.Property(x => x.GlobalRequestsPerMinute).HasColumnName("global_requests_per_minute");
            e.Property(x => x.PerDomainRequestsPerMinute).HasColumnName("per_domain_requests_per_minute");
            e.Property(x => x.MaxConcurrency).HasColumnName("max_concurrency");
            e.Property(x => x.RequestTimeoutSeconds).HasColumnName("request_timeout_seconds");
            e.Property(x => x.RotateUserAgents).HasColumnName("rotate_user_agents");
            e.Property(x => x.CustomUserAgentsJson).HasColumnName("custom_user_agents_json").HasColumnType("jsonb");
            e.Property(x => x.RandomizeHeaderOrder).HasColumnName("randomize_header_order");
            e.Property(x => x.UseRandomJitter).HasColumnName("use_random_jitter");
            e.Property(x => x.MinJitterMs).HasColumnName("min_jitter_ms");
            e.Property(x => x.MaxJitterMs).HasColumnName("max_jitter_ms");
            e.Property(x => x.SpoofReferer).HasColumnName("spoof_referer");
            e.Property(x => x.CustomHeadersJson).HasColumnName("custom_headers_json").HasColumnType("jsonb");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
        });

        modelBuilder.Entity<HighValueFinding>(e =>
        {
            e.ToTable("high_value_findings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TargetId).HasColumnName("target_id");
            e.Property(x => x.SourceAssetId).HasColumnName("source_asset_id");
            e.Property(x => x.FindingType).HasColumnName("finding_type").HasMaxLength(64).IsRequired();
            e.Property(x => x.Severity).HasColumnName("severity").HasMaxLength(32).IsRequired();
            e.Property(x => x.PatternName).HasColumnName("pattern_name").HasMaxLength(256).IsRequired();
            e.Property(x => x.Category).HasColumnName("category").HasMaxLength(128);
            e.Property(x => x.MatchedText).HasColumnName("matched_text");
            e.Property(x => x.SourceUrl).HasColumnName("source_url").HasMaxLength(4096).IsRequired();
            e.Property(x => x.WorkerName).HasColumnName("worker_name").HasMaxLength(128).IsRequired();
            e.Property(x => x.ImportanceScore).HasColumnName("importance_score");
            e.Property(x => x.DiscoveredAtUtc).HasColumnName("discovered_at_utc");
            e.Property(x => x.IsHighValue).HasColumnName("is_high_value");
            e.Property(x => x.InvestigationStatus).HasColumnName("investigation_status").HasMaxLength(32).IsRequired();
            e.Property(x => x.InvestigationUpdatedAtUtc).HasColumnName("investigation_updated_at_utc");
            e.HasIndex(x => x.TargetId);
            e.HasIndex(x => x.DiscoveredAtUtc);
            e.HasOne(x => x.Target)
                .WithMany()
                .HasForeignKey(x => x.TargetId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.MessageType).HasColumnName("message_type").HasMaxLength(512).IsRequired();
            e.Property(x => x.PayloadJson).HasColumnName("payload_json").IsRequired();
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.CausationId).HasColumnName("causation_id");
            e.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            e.Property(x => x.Producer).HasColumnName("producer").HasMaxLength(128).IsRequired();
            e.Property(x => x.State).HasColumnName("state").HasMaxLength(32).IsRequired();
            e.Property(x => x.AttemptCount).HasColumnName("attempt_count");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            e.Property(x => x.NextAttemptAtUtc).HasColumnName("next_attempt_at_utc");
            e.Property(x => x.DispatchedAtUtc).HasColumnName("dispatched_at_utc");
            e.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(2048);
            e.Property(x => x.LockedBy).HasColumnName("locked_by").HasMaxLength(256);
            e.Property(x => x.LockedUntilUtc).HasColumnName("locked_until_utc");
            e.HasIndex(x => new { x.State, x.NextAttemptAtUtc });
            e.HasIndex(x => x.EventId).IsUnique();
        });

        modelBuilder.Entity<InboxMessage>(e =>
        {
            e.ToTable("inbox_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.Consumer).HasColumnName("consumer").HasMaxLength(256).IsRequired();
            e.Property(x => x.ProcessedAtUtc).HasColumnName("processed_at_utc");
            e.HasIndex(x => new { x.EventId, x.Consumer }).IsUnique();
        });
        modelBuilder.Entity<WorkerCancellation>(entity =>
        {
            entity.ToTable("worker_cancellations");
            entity.HasKey(e => e.MessageId);
            entity.Property(e => e.RequestedAtUtc).IsRequired();
        });
    }
}
