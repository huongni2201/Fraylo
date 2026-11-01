using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ToTable("Job", t =>
        {
            t.HasCheckConstraint("CK_Job_TaskType_Scene",
                "(task_type = 'Script' AND scene_id IS NULL) OR task_type IN ('Image', 'VideoPrompt', 'VideoRender')");
        });

        builder.HasKey(j => j.Id);

        builder.Property(j => j.Id)
            .HasColumnName("id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(j => j.ProjectId)
            .HasColumnName("project_id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(j => j.SceneId)
            .HasColumnName("scene_id")
            .HasColumnType("TEXT");

        builder.Property(j => j.TaskType)
            .HasColumnName("task_type")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(j => j.State)
            .HasColumnName("state")
            .HasColumnType("TEXT")
            .HasDefaultValue("Queued")
            .IsRequired();

        builder.Property(j => j.ReasonCode)
            .HasColumnName("reason_code")
            .HasColumnType("TEXT");

        builder.Property(j => j.ProviderKey)
            .HasColumnName("provider_key")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(j => j.ConfigSnapshotJson)
            .HasColumnName("config_snapshot_json")
            .HasColumnType("TEXT");

        builder.Property(j => j.PromptSnapshotText)
            .HasColumnName("prompt_snapshot_text")
            .HasColumnType("TEXT");

        builder.Property(j => j.StorageRootId)
            .HasColumnName("storage_root_id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(j => j.Attempts)
            .HasColumnName("attempts")
            .HasColumnType("INTEGER")
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(j => j.MaxAttempts)
            .HasColumnName("max_attempts")
            .HasColumnType("INTEGER")
            .HasDefaultValue(3)
            .IsRequired();

        builder.Property(j => j.LeaseOwnerId)
            .HasColumnName("lease_owner_id")
            .HasColumnType("TEXT");

        builder.Property(j => j.FencingToken)
            .HasColumnName("fencing_token")
            .HasColumnType("INTEGER")
            .HasDefaultValue(0L)
            .IsConcurrencyToken()
            .IsRequired();

        builder.Property(j => j.LeaseExpiresAtUtc)
            .HasColumnName("lease_expires_at_utc")
            .HasColumnType("TEXT");

        builder.Property(j => j.RetryAfterUtc)
            .HasColumnName("retry_after_utc")
            .HasColumnType("TEXT");

        builder.Property(j => j.SideEffectSubmitted)
            .HasColumnName("side_effect_submitted")
            .HasColumnType("INTEGER")
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(j => j.SideEffectAtUtc)
            .HasColumnName("side_effect_at_utc")
            .HasColumnType("TEXT");

        builder.Property(j => j.ResultReference)
            .HasColumnName("result_reference")
            .HasColumnType("TEXT");

        builder.Property(j => j.ResultText)
            .HasColumnName("result_text")
            .HasColumnType("TEXT");

        builder.Property(j => j.CancelRequested)
            .HasColumnName("cancel_requested")
            .HasColumnType("INTEGER")
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(j => j.LastHeartbeatAtUtc)
            .HasColumnName("last_heartbeat_at_utc")
            .HasColumnType("TEXT");

        builder.Property(j => j.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(j => j.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(j => j.Revision)
            .HasColumnName("revision")
            .HasColumnType("INTEGER")
            .HasDefaultValue(1L)
            .IsRequired();

        builder.HasOne(j => j.Project)
            .WithMany()
            .HasForeignKey(j => j.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(j => j.Scene)
            .WithMany()
            .HasForeignKey(j => new { j.SceneId, j.ProjectId })
            .HasPrincipalKey(s => new { s.Id, s.ProjectId })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(j => j.ExecutionBinding)
            .WithOne(b => b.Job)
            .HasForeignKey<ExecutionBinding>(b => b.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(j => new { j.State, j.LeaseExpiresAtUtc, j.RetryAfterUtc })
            .HasDatabaseName("ix_job_state_lease_retry");

        builder.HasIndex(j => j.ProjectId)
            .HasDatabaseName("ix_job_project");
    }
}
