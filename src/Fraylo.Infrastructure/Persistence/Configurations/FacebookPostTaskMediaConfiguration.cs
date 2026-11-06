using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public sealed class FacebookPostTaskMediaConfiguration : IEntityTypeConfiguration<FacebookPostTaskMedia>
{
    public void Configure(EntityTypeBuilder<FacebookPostTaskMedia> builder)
    {
        builder.ToTable("FacebookPostTaskMedia");
        builder.HasKey(p => new { p.TaskId, p.SortOrder });
        builder.Property(p => p.TaskId).HasColumnName("task_id").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.AssetId).HasColumnName("asset_id").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.Sha256).HasColumnName("sha256").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.SortOrder).HasColumnName("sort_order").HasColumnType("INTEGER").IsRequired();
        builder.HasOne<FacebookPostTask>().WithMany().HasForeignKey(p => p.TaskId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Asset>().WithMany().HasForeignKey(p => p.AssetId).OnDelete(DeleteBehavior.Restrict);
    }
}
