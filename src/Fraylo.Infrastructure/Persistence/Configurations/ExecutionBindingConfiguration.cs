using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public class ExecutionBindingConfiguration : IEntityTypeConfiguration<ExecutionBinding>
{
    public void Configure(EntityTypeBuilder<ExecutionBinding> builder)
    {
        builder.ToTable("ExecutionBinding");

        builder.HasKey(b => b.JobId);

        builder.Property(b => b.JobId)
            .HasColumnName("job_id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(b => b.ProviderAccountId)
            .HasColumnName("provider_account_id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(b => b.ProfileId)
            .HasColumnName("profile_id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(b => b.Identity)
            .HasColumnName("identity")
            .HasColumnType("TEXT");

        builder.Property(b => b.CapacityConfigRevision)
            .HasColumnName("capacity_config_revision")
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(b => b.TabSlotId)
            .HasColumnName("tab_slot_id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(b => b.AssignedAtUtc)
            .HasColumnName("assigned_at_utc")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(b => b.LeaseOwnerId)
            .HasColumnName("lease_owner_id")
            .HasColumnType("TEXT");

        builder.Property(b => b.LeaseToken)
            .HasColumnName("lease_token")
            .HasColumnType("INTEGER")
            .HasDefaultValue(0L)
            .IsRequired();

        builder.HasOne(b => b.Job)
            .WithOne(j => j.ExecutionBinding)
            .HasForeignKey<ExecutionBinding>(b => b.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(b => b.ProviderAccount)
            .WithMany()
            .HasForeignKey(b => b.ProviderAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(b => b.Profile)
            .WithMany()
            .HasForeignKey(b => b.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(b => new { b.ProviderAccountId, b.TabSlotId })
            .HasDatabaseName("ix_execution_binding_account_slot");

        builder.HasIndex(b => b.ProfileId)
            .HasDatabaseName("ix_execution_binding_profile");
    }
}
