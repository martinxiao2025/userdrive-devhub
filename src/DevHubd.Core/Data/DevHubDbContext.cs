using DevHubd.Core.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevHubd.Core.Data;

/// <summary>
/// EF Core 数据访问上下文：只负责 9 张既有表的<b>映射与读写</b>，
/// <b>不承担 schema 迁移</b>——DDL 与版本管理唯一归属 <see cref="Migrator"/>（<c>PRAGMA user_version</c>）。
/// 因此本上下文不包含 EF Migrations、不创建 <c>__EFMigrationsHistory</c>，
/// 也不会调用 <c>EnsureCreated</c>；表名/列名/索引/约束必须与 <see cref="Migrator"/> 的 DDL 保持一致。
/// </summary>
public sealed class DevHubDbContext : DbContext
{
    /// <summary>注入由 <c>AddDbContextFactory</c> 或测试工厂构造的选项。</summary>
    /// <param name="options">上下文选项。</param>
    public DevHubDbContext(DbContextOptions<DevHubDbContext> options)
        : base(options)
    {
    }

    /// <summary>任务物化视图。</summary>
    public DbSet<TaskViewEntity> TaskViews => Set<TaskViewEntity>();

    /// <summary>队列项。</summary>
    public DbSet<QueueItemEntity> QueueItems => Set<QueueItemEntity>();

    /// <summary>执行尝试。</summary>
    public DbSet<AttemptEntity> Attempts => Set<AttemptEntity>();

    /// <summary>回合结果。</summary>
    public DbSet<ResultEntity> Results => Set<ResultEntity>();

    /// <summary>通知。</summary>
    public DbSet<NoticeEntity> Notices => Set<NoticeEntity>();

    /// <summary>provider 状态。</summary>
    public DbSet<ProviderStateEntity> ProviderStates => Set<ProviderStateEntity>();

    /// <summary>调度状态（单例）。</summary>
    public DbSet<SchedulerStateEntity> SchedulerStates => Set<SchedulerStateEntity>();

    /// <summary>写入范围租约。</summary>
    public DbSet<ScopeLockEntity> ScopeLocks => Set<ScopeLockEntity>();

    /// <summary>审计流水。</summary>
    public DbSet<AuditEntity> Audits => Set<AuditEntity>();

    /// <summary>角色声明快照。</summary>
    public DbSet<RoleViewEntity> RoleViews => Set<RoleViewEntity>();

    /// <summary>不确定项问题单。</summary>
    public DbSet<InputRequestEntity> InputRequests => Set<InputRequestEntity>();

    /// <summary>
    /// 显式映射既有 schema：表名、列名、主键、唯一约束、索引全部按 <see cref="Migrator"/> 的 DDL 书写。
    /// 布尔列以 INTEGER 0/1 存储（<c>HasConversion&lt;int&gt;</c>），字符串时间保持 ISO-8601 原样。
    /// CHECK 约束只用于模型自描述，不会被用来建表（迁移由 <see cref="Migrator"/> 独占）。
    /// </summary>
    /// <param name="modelBuilder">模型构建器。</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureTaskView(modelBuilder);
        ConfigureQueueItem(modelBuilder);
        ConfigureAttempt(modelBuilder);
        ConfigureResult(modelBuilder);
        ConfigureNotice(modelBuilder);
        ConfigureProviderState(modelBuilder);
        ConfigureSchedulerState(modelBuilder);
        ConfigureScopeLock(modelBuilder);
        ConfigureAudit(modelBuilder);
        ConfigureRoleView(modelBuilder);
        ConfigureInputRequest(modelBuilder);
    }

    /// <summary>映射 <c>input_request</c>（schema v5）。</summary>
    /// <param name="modelBuilder">模型构建器。</param>
    private static void ConfigureInputRequest(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<InputRequestEntity>();
        entity.ToTable(
            "input_request",
            table => table.HasCheckConstraint(
                "ck_input_request_status",
                "status IN ('open','answered','expired','cancelled')"));

        entity.HasKey(item => item.Id);

        entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(item => item.WorkspaceId).HasColumnName("workspace_id");
        entity.Property(item => item.TaskId).HasColumnName("task_id");
        entity.Property(item => item.QueueItemId).HasColumnName("queue_item_id");
        entity.Property(item => item.RoleId).HasColumnName("role_id");
        entity.Property(item => item.Status).HasColumnName("status");
        entity.Property(item => item.Question).HasColumnName("question");
        entity.Property(item => item.Context).HasColumnName("context");
        entity.Property(item => item.Options).HasColumnName("options");
        entity.Property(item => item.MissingInputs).HasColumnName("missing_inputs");
        entity.Property(item => item.AllowFreeText).HasColumnName("allow_free_text").HasConversion<int>();
        entity.Property(item => item.DedupeKey).HasColumnName("dedupe_key");
        entity.Property(item => item.CreatedAt).HasColumnName("created_at");
        entity.Property(item => item.ExpiresAt).HasColumnName("expires_at");
        entity.Property(item => item.AnsweredAt).HasColumnName("answered_at");
        entity.Property(item => item.Answer).HasColumnName("answer");
        entity.Property(item => item.AnswerOptionId).HasColumnName("answer_option_id");
        entity.Property(item => item.AnswerSource).HasColumnName("answer_source");
        entity.Property(item => item.AnsweredBy).HasColumnName("answered_by");

        entity.HasIndex(item => item.DedupeKey)
            .IsUnique()
            .HasFilter("status = 'open'")
            .HasDatabaseName("idx_input_dedupe_open");
        entity.HasIndex(item => new { item.Status, item.CreatedAt }).HasDatabaseName("idx_input_open");
        entity.HasIndex(item => new { item.WorkspaceId, item.TaskId, item.Status }).HasDatabaseName("idx_input_task");
    }

    /// <summary>映射 <c>role_view</c>（schema v5）。</summary>
    /// <param name="modelBuilder">模型构建器。</param>
    private static void ConfigureRoleView(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<RoleViewEntity>();
        entity.ToTable(
            "role_view",
            table => table.HasCheckConstraint(
                "ck_role_view_delivery",
                "delivery IN ('auto','dispatch_only')"));

        entity.HasKey(item => item.Id);

        entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(item => item.Name).HasColumnName("name");
        entity.Property(item => item.Description).HasColumnName("description");
        entity.Property(item => item.SourcePath).HasColumnName("source_path");
        entity.Property(item => item.ContentHash).HasColumnName("content_hash");
        entity.Property(item => item.Skills).HasColumnName("skills");
        entity.Property(item => item.Tools).HasColumnName("tools");
        entity.Property(item => item.Delivery).HasColumnName("delivery");
        entity.Property(item => item.RequiresApproval).HasColumnName("requires_approval").HasConversion<int>();
        entity.Property(item => item.CanRequestInput).HasColumnName("can_request_input").HasConversion<int>();
        entity.Property(item => item.MaxParallel).HasColumnName("max_parallel");
        entity.Property(item => item.MaxAttempts).HasColumnName("max_attempts");
        entity.Property(item => item.MaxRounds).HasColumnName("max_rounds");
        entity.Property(item => item.MaxToolCalls).HasColumnName("max_tool_calls");
        entity.Property(item => item.MaxDurationSeconds).HasColumnName("max_duration_seconds");
        entity.Property(item => item.MaxInputTokens).HasColumnName("max_input_tokens");
        entity.Property(item => item.Payload).HasColumnName("payload");
        entity.Property(item => item.LoadedAt).HasColumnName("loaded_at");
        entity.Property(item => item.UpdatedAt).HasColumnName("updated_at");
    }

    /// <summary>映射 <c>task_view</c>。</summary>
    /// <param name="modelBuilder">模型构建器。</param>
    private static void ConfigureTaskView(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<TaskViewEntity>();
        entity.ToTable(
            "task_view",
            table => table.HasCheckConstraint(
                "ck_task_view_status",
                "status IN ('ready','blocked','done','cancelled','missing')"));

        entity.HasKey(item => new { item.WorkspaceId, item.Id });

        entity.Property(item => item.WorkspaceId).HasColumnName("workspace_id").ValueGeneratedNever();
        entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(item => item.SourcePath).HasColumnName("source_path");
        entity.Property(item => item.ContentHash).HasColumnName("content_hash");
        entity.Property(item => item.Title).HasColumnName("title");
        entity.Property(item => item.Status).HasColumnName("status");
        entity.Property(item => item.Priority).HasColumnName("priority");
        entity.Property(item => item.WriteScope).HasColumnName("write_scope");
        entity.Property(item => item.Payload).HasColumnName("payload");
        entity.Property(item => item.RoleId).HasColumnName("role_id");
        entity.Property(item => item.DependsOn).HasColumnName("depends_on");
        entity.Property(item => item.ApprovalState).HasColumnName("approval_state");
        entity.Property(item => item.RerunPendingHash).HasColumnName("rerun_pending").HasDefaultValue(string.Empty);
        entity.Property(item => item.SeenAt).HasColumnName("seen_at");
        entity.Property(item => item.UpdatedAt).HasColumnName("updated_at");

        entity.HasIndex(item => new { item.WorkspaceId, item.Status }).HasDatabaseName("idx_task_view_status");
    }

    /// <summary>映射 <c>queue_item</c>。</summary>
    /// <param name="modelBuilder">模型构建器。</param>
    private static void ConfigureQueueItem(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<QueueItemEntity>();
        entity.ToTable(
            "queue_item",
            table => table.HasCheckConstraint(
                "ck_queue_item_status",
                "status IN ('pending','claimed','running','succeeded','failed','dead','cancelled')"));

        entity.HasKey(item => item.Id);

        entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(item => item.WorkspaceId).HasColumnName("workspace_id");
        entity.Property(item => item.TaskId).HasColumnName("task_id");
        entity.Property(item => item.TaskHash).HasColumnName("task_hash");
        entity.Property(item => item.DedupeKey).HasColumnName("dedupe_key");
        entity.Property(item => item.Status).HasColumnName("status");
        entity.Property(item => item.Priority).HasColumnName("priority");
        entity.Property(item => item.Attempt).HasColumnName("attempt");
        entity.Property(item => item.MaxAttempts).HasColumnName("max_attempts");
        entity.Property(item => item.PreferredProvider).HasColumnName("preferred_provider");
        entity.Property(item => item.RoleId).HasColumnName("role_id");
        entity.Property(item => item.HoldKind).HasColumnName("hold_kind");
        entity.Property(item => item.HoldRef).HasColumnName("hold_ref");
        entity.Property(item => item.Forced).HasColumnName("forced").HasConversion<int>();
        entity.Property(item => item.AvailableAt).HasColumnName("available_at");
        entity.Property(item => item.LeaseOwner).HasColumnName("lease_owner");
        entity.Property(item => item.LeaseExpiresAt).HasColumnName("lease_expires_at");
        entity.Property(item => item.LastErrorCode).HasColumnName("last_error_code");
        entity.Property(item => item.LastError).HasColumnName("last_error");
        entity.Property(item => item.CreatedAt).HasColumnName("created_at");
        entity.Property(item => item.UpdatedAt).HasColumnName("updated_at");

        entity.HasIndex(item => item.DedupeKey).IsUnique();
        entity.HasIndex(item => new { item.WorkspaceId, item.Status, item.Priority, item.AvailableAt })
            .HasDatabaseName("idx_queue_ready");
        entity.HasIndex(item => new { item.WorkspaceId, item.TaskId, item.Status })
            .HasDatabaseName("idx_queue_task");
        entity.HasIndex(item => new { item.RoleId, item.Status }).HasDatabaseName("idx_queue_role");
    }

    /// <summary>映射 <c>attempt</c>。</summary>
    /// <param name="modelBuilder">模型构建器。</param>
    private static void ConfigureAttempt(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<AttemptEntity>();
        entity.ToTable(
            "attempt",
            table => table.HasCheckConstraint(
                "ck_attempt_outcome",
                "outcome IN ('succeeded','failed','abandoned')"));

        entity.HasKey(item => item.Id);

        entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(item => item.QueueItemId).HasColumnName("queue_item_id");
        entity.Property(item => item.AttemptNo).HasColumnName("attempt_no");
        entity.Property(item => item.Provider).HasColumnName("provider");
        entity.Property(item => item.Model).HasColumnName("model");
        entity.Property(item => item.StartedAt).HasColumnName("started_at");
        entity.Property(item => item.EndedAt).HasColumnName("ended_at");
        entity.Property(item => item.Outcome).HasColumnName("outcome");
        entity.Property(item => item.ErrorKind).HasColumnName("error_kind");
        entity.Property(item => item.ErrorCode).HasColumnName("error_code");
        entity.Property(item => item.ToolCalls).HasColumnName("tool_calls");
        entity.Property(item => item.InputTokens).HasColumnName("input_tokens");
        entity.Property(item => item.OutputTokens).HasColumnName("output_tokens");
        entity.Property(item => item.UsageEstimated).HasColumnName("usage_estimated").HasConversion<int>();
        entity.Property(item => item.UsageAvailable).HasColumnName("usage_available").HasConversion<int>();
        entity.Property(item => item.RoundsExhausted).HasColumnName("rounds_exhausted").HasConversion<int>();
        entity.Property(item => item.FilesChanged).HasColumnName("files_changed");
        entity.Property(item => item.Summary).HasColumnName("summary");

        entity.HasIndex(item => new { item.QueueItemId, item.AttemptNo }).IsUnique();

        // 显式声明既有外键（attempt.queue_item_id → queue_item.id），
        // 让 EF 知道写入顺序并对齐 SQLite 的 NO ACTION 语义；不会生成任何 DDL。
        entity.HasOne<QueueItemEntity>()
            .WithMany()
            .HasForeignKey(item => item.QueueItemId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    /// <summary>映射 <c>result</c>。</summary>
    /// <param name="modelBuilder">模型构建器。</param>
    private static void ConfigureResult(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ResultEntity>();
        entity.ToTable(
            "result",
            table => table.HasCheckConstraint(
                "ck_result_outcome",
                "outcome IN ('delivered','awaiting_review','failed')"));

        entity.HasKey(item => item.Id);

        entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(item => item.TaskId).HasColumnName("task_id");
        entity.Property(item => item.QueueItemId).HasColumnName("queue_item_id");
        entity.Property(item => item.Provider).HasColumnName("provider");
        entity.Property(item => item.Outcome).HasColumnName("outcome");
        entity.Property(item => item.Summary).HasColumnName("summary");
        entity.Property(item => item.Artifacts).HasColumnName("artifacts");
        entity.Property(item => item.CreatedAt).HasColumnName("created_at");
        entity.Property(item => item.ReadAt).HasColumnName("read_at");

        entity.HasIndex(item => new { item.ReadAt, item.CreatedAt }).HasDatabaseName("idx_result_unread");

        // 显式声明既有外键（result.queue_item_id → queue_item.id）；不会生成任何 DDL。
        entity.HasOne<QueueItemEntity>()
            .WithMany()
            .HasForeignKey(item => item.QueueItemId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    /// <summary>映射 <c>notice</c>。</summary>
    /// <param name="modelBuilder">模型构建器。</param>
    private static void ConfigureNotice(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<NoticeEntity>();
        entity.ToTable(
            "notice",
            table => table.HasCheckConstraint(
                "ck_notice_kind",
                "kind IN ('result','failure','breaker','auth','provider')"));

        entity.HasKey(item => item.Seq);

        entity.Property(item => item.Seq).HasColumnName("seq").ValueGeneratedOnAdd();
        entity.Property(item => item.DedupeKey).HasColumnName("dedupe_key");
        entity.Property(item => item.Kind).HasColumnName("kind");
        entity.Property(item => item.TaskId).HasColumnName("task_id");
        entity.Property(item => item.Title).HasColumnName("title");
        entity.Property(item => item.Body).HasColumnName("body");
        entity.Property(item => item.CreatedAt).HasColumnName("created_at");
        entity.Property(item => item.AckedAt).HasColumnName("acked_at");

        entity.HasIndex(item => item.DedupeKey).IsUnique();
    }

    /// <summary>映射 <c>provider_state</c>。</summary>
    /// <param name="modelBuilder">模型构建器。</param>
    private static void ConfigureProviderState(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ProviderStateEntity>();
        entity.ToTable(
            "provider_state",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_provider_state_state",
                    "state IN ('ready','down','no_model','quota','auth')");
                table.HasCheckConstraint(
                    "ck_provider_state_breaker",
                    "breaker IN ('closed','open','half_open')");
            });

        entity.HasKey(item => item.Provider);

        entity.Property(item => item.Provider).HasColumnName("provider").ValueGeneratedNever();
        entity.Property(item => item.State).HasColumnName("state");
        entity.Property(item => item.Detail).HasColumnName("detail");
        entity.Property(item => item.Breaker).HasColumnName("breaker");
        entity.Property(item => item.BreakerReason).HasColumnName("breaker_reason");
        entity.Property(item => item.BreakerOpenedAt).HasColumnName("breaker_opened_at");
        entity.Property(item => item.ConsecutiveFailures).HasColumnName("consecutive_failures");
        entity.Property(item => item.CheckedAt).HasColumnName("checked_at");
    }

    /// <summary>映射 <c>scheduler_state</c>。</summary>
    /// <param name="modelBuilder">模型构建器。</param>
    private static void ConfigureSchedulerState(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SchedulerStateEntity>();
        entity.ToTable(
            "scheduler_state",
            table =>
            {
                table.HasCheckConstraint("ck_scheduler_state_id", "id = 1");
                table.HasCheckConstraint(
                    "ck_scheduler_state_mode",
                    "mode IN ('auto','dispatch_only')");
            });

        entity.HasKey(item => item.Id);

        entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(item => item.Enabled).HasColumnName("enabled").HasConversion<int>();
        entity.Property(item => item.Mode).HasColumnName("mode");
        entity.Property(item => item.PauseReason).HasColumnName("pause_reason");
        entity.Property(item => item.IntervalSeconds).HasColumnName("interval_seconds");
        entity.Property(item => item.LastScanAt).HasColumnName("last_scan_at");
        entity.Property(item => item.NextScanAt).HasColumnName("next_scan_at");
        entity.Property(item => item.Counters).HasColumnName("counters");
        entity.Property(item => item.UpdatedAt).HasColumnName("updated_at");
    }

    /// <summary>映射 <c>scope_lock</c>。</summary>
    /// <param name="modelBuilder">模型构建器。</param>
    private static void ConfigureScopeLock(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ScopeLockEntity>();
        entity.ToTable("scope_lock");

        entity.HasKey(item => new { item.WorkspaceId, item.Scope });

        entity.Property(item => item.WorkspaceId).HasColumnName("workspace_id").ValueGeneratedNever();
        entity.Property(item => item.Scope).HasColumnName("scope").ValueGeneratedNever();
        entity.Property(item => item.OwnerId).HasColumnName("owner_id");
        entity.Property(item => item.AcquiredAt).HasColumnName("acquired_at");
        entity.Property(item => item.ExpiresAt).HasColumnName("expires_at");

        entity.HasIndex(item => item.ExpiresAt).HasDatabaseName("idx_scope_expiry");
    }

    /// <summary>映射 <c>audit</c>。</summary>
    /// <param name="modelBuilder">模型构建器。</param>
    private static void ConfigureAudit(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<AuditEntity>();
        entity.ToTable("audit");

        entity.HasKey(item => item.Id);

        entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(item => item.At).HasColumnName("at");
        entity.Property(item => item.Actor).HasColumnName("actor");
        entity.Property(item => item.Action).HasColumnName("action");
        entity.Property(item => item.Subject).HasColumnName("subject");
        entity.Property(item => item.Provider).HasColumnName("provider");
        entity.Property(item => item.Detail).HasColumnName("detail");
        entity.Property(item => item.TraceId).HasColumnName("trace_id");

        entity.HasIndex(item => item.At).HasDatabaseName("idx_audit_at");
    }
}
