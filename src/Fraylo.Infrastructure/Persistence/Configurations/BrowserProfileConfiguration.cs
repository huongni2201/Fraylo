using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public class BrowserProfileConfiguration : IEntityTypeConfiguration<BrowserProfile>
{
    public void Configure(EntityTypeBuilder<BrowserProfile> builder)
    {
        builder.ToTable("BrowserProfile");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .HasColumnName("id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(p => p.Kind)
            .HasColumnName("kind")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(p => p.AccountIdentity)
            .HasColumnName("account_identity")
            .HasColumnType("TEXT");

        builder.Property(p => p.VisibilityState)
            .HasColumnName("visibility_state")
            .HasColumnType("TEXT")
            .HasDefaultValue("Hidden")
            .IsRequired();

        builder.Property(p => p.SessionStatus)
            .HasColumnName("session_status")
            .HasColumnType("TEXT")
            .HasDefaultValue("Unverified")
            .IsRequired();

        builder.Property(p => p.OperationalState)
            .HasColumnName("operational_state")
            .HasColumnType("TEXT")
            .HasDefaultValue("Stopped")
            .IsRequired();

        builder.Property(p => p.ProviderKey)
            .HasColumnName("provider_key")
            .HasColumnType("TEXT");

        builder.Property(p => p.DisplayName)
            .HasColumnName("display_name")
            .HasColumnType("TEXT");

        builder.Property(p => p.Lifecycle)
            .HasColumnName("lifecycle")
            .HasColumnType("TEXT")
            .HasDefaultValue("Active")
            .IsRequired();

        builder.Property(p => p.Revision)
            .HasColumnName("revision")
            .HasColumnType("INTEGER")
            .HasDefaultValue(1L)
            .IsRequired();

        builder.Property(p => p.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(p => p.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("TEXT")
            .IsRequired();

    }
}
