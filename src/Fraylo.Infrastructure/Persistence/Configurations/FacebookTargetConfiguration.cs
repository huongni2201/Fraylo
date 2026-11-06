using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public sealed class FacebookTargetConfiguration : IEntityTypeConfiguration<FacebookTarget>
{
    public void Configure(EntityTypeBuilder<FacebookTarget> builder)
    {
        builder.ToTable("FacebookTarget");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.ProfileId).HasColumnName("profile_id").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.Name).HasColumnName("name").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.Url).HasColumnName("url").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.Lifecycle).HasColumnName("lifecycle").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("TEXT").IsRequired();
        builder.HasOne<BrowserProfile>().WithMany().HasForeignKey(p => p.ProfileId).OnDelete(DeleteBehavior.Restrict);
        builder.HasAlternateKey(p => new { p.Id, p.ProfileId });
        builder.HasIndex(p => new { p.ProfileId, p.Url }).IsUnique().HasDatabaseName("uq_facebook_target_profile_url");
    }
}
