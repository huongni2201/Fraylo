using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public sealed class FacebookPostBatchConfiguration : IEntityTypeConfiguration<FacebookPostBatch>
{
    public void Configure(EntityTypeBuilder<FacebookPostBatch> builder)
    {
        builder.ToTable("FacebookPostBatch");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.RequiresRewrite).HasColumnName("requires_rewrite").HasDefaultValue(false);
        builder.Property(p => p.OriginalTextSnapshot).HasColumnName("original_text_snapshot");
        builder.Property(p => p.Name).HasColumnName("name").HasColumnType("TEXT").HasDefaultValue("").IsRequired();
        builder.Property(p => p.Paused).HasColumnName("paused").HasColumnType("INTEGER").HasDefaultValue(false).IsRequired();
        builder.Property(p => p.Revision).HasColumnName("revision").HasColumnType("INTEGER").HasDefaultValue(1L).IsRequired();

        builder.Property(p => p.Id).HasColumnName("id").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.ProfileId).HasColumnName("profile_id").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.RequestHash).HasColumnName("request_hash").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("TEXT").IsRequired();
        builder.HasOne<BrowserProfile>().WithMany().HasForeignKey(p => p.ProfileId).OnDelete(DeleteBehavior.Restrict);
    }
}
