using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public class ActiveSlotOccupancyConfiguration : IEntityTypeConfiguration<ActiveSlotOccupancy>
{
    public void Configure(EntityTypeBuilder<ActiveSlotOccupancy> builder)
    {
        builder.ToTable("ActiveSlotOccupancy");

        // Primary key composite ngăn 2 job cùng chiếm một slot của cùng 1 account
        builder.HasKey(o => new { o.ProviderAccountId, o.TabSlotId });

        builder.Property(o => o.ProviderAccountId)
            .HasColumnName("provider_account_id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(o => o.TabSlotId)
            .HasColumnName("tab_slot_id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(o => o.JobId)
            .HasColumnName("job_id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(o => o.LeaseOwnerId)
            .HasColumnName("lease_owner_id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(o => o.LeaseToken)
            .HasColumnName("lease_token")
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(o => o.AcquiredAtUtc)
            .HasColumnName("acquired_at_utc")
            .HasColumnType("TEXT")
            .IsRequired();

        // 1 job chỉ được chiếm 1 slot
        builder.HasIndex(o => o.JobId)
            .IsUnique()
            .HasDatabaseName("uq_active_slot_occupancy_job");

        builder.HasOne(o => o.ProviderAccount)
            .WithMany()
            .HasForeignKey(o => o.ProviderAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(o => o.Job)
            .WithMany()
            .HasForeignKey(o => o.JobId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
