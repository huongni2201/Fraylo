using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public class ProviderAccountConfiguration : IEntityTypeConfiguration<ProviderAccount>
{
    public void Configure(EntityTypeBuilder<ProviderAccount> builder)
    {
        builder.ToTable("ProviderAccount");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .HasColumnName("id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(a => a.ProviderKey)
            .HasColumnName("provider_key")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(a => a.ProfileId)
            .HasColumnName("profile_id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(a => a.DisplayName)
            .HasColumnName("display_name")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(a => a.VerifiedIdentity)
            .HasColumnName("verified_identity")
            .HasColumnType("TEXT");

        builder.Property(a => a.IdentityVerifiedAtUtc)
            .HasColumnName("identity_verified_at_utc")
            .HasColumnType("TEXT");

        builder.Property(a => a.Enabled)
            .HasColumnName("enabled")
            .HasColumnType("INTEGER")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(a => a.Lifecycle)
            .HasColumnName("lifecycle")
            .HasColumnType("TEXT")
            .HasDefaultValue("Active")
            .IsRequired();

        builder.Property(a => a.MaxConcurrentJobs)
            .HasColumnName("max_concurrent_jobs")
            .HasColumnType("INTEGER")
            .HasDefaultValue(3)
            .IsRequired();

        builder.Property(a => a.MaxTabs)
            .HasColumnName("max_tabs")
            .HasColumnType("INTEGER")
            .HasDefaultValue(3)
            .IsRequired();

        builder.Property(a => a.PerTaskLimitsJson)
            .HasColumnName("per_task_limits_json")
            .HasColumnType("TEXT");

        builder.Property(a => a.Revision)
            .HasColumnName("revision")
            .HasColumnType("INTEGER")
            .HasDefaultValue(1L)
            .IsRequired();

        builder.Property(a => a.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(a => a.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.HasOne(a => a.Profile)
            .WithMany()
            .HasForeignKey(a => a.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => a.ProfileId)
            .IsUnique()
            .HasDatabaseName("ix_provider_account_profile");

        builder.HasIndex(a => a.ProviderKey)
            .HasDatabaseName("ix_provider_account_provider");

        builder.HasIndex(a => new { a.ProviderKey, a.VerifiedIdentity })
            .IsUnique()
            .HasFilter("\"verified_identity\" IS NOT NULL AND \"lifecycle\" = 'Active'")
            .HasDatabaseName("ix_provider_account_verified_identity");
    }
}
