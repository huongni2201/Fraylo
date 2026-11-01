using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public class BrowserHandoffCheckpointConfiguration : IEntityTypeConfiguration<BrowserHandoffCheckpoint>
{
    public void Configure(EntityTypeBuilder<BrowserHandoffCheckpoint> builder)
    {
        builder.ToTable("BrowserHandoffCheckpoint");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .HasColumnName("id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(c => c.ProfileId)
            .HasColumnName("profile_id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(c => c.ActiveJobId)
            .HasColumnName("active_job_id")
            .HasColumnType("TEXT");

        builder.Property(c => c.PreviousState)
            .HasColumnName("previous_state")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(c => c.TargetVisibilityState)
            .HasColumnName("target_visibility_state")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(c => c.ReasonCode)
            .HasColumnName("reason_code")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(c => c.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(c => c.ResolvedAtUtc)
            .HasColumnName("resolved_at_utc")
            .HasColumnType("TEXT");

        builder.HasOne(c => c.Profile)
            .WithMany()
            .HasForeignKey(c => c.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => c.ProfileId)
            .HasDatabaseName("ix_browser_handoff_checkpoint_profile");
    }
}
