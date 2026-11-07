using Fraylo.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public sealed class FacebookRewriteChunkConfiguration : IEntityTypeConfiguration<FacebookRewriteChunk>
{
    public void Configure(EntityTypeBuilder<FacebookRewriteChunk> builder)
    {
        builder.ToTable("FacebookRewriteChunk");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id");
        builder.Property(c => c.BatchId).HasColumnName("batch_id").IsRequired();
        builder.Property(c => c.Ordinal).HasColumnName("ordinal");
        builder.Property(c => c.TaskIdsJson).HasColumnName("task_ids_json").IsRequired();
        builder.Property(c => c.AppliedAtUtc).HasColumnName("applied_at_utc");
        builder.Property(c => c.ApplyError).HasColumnName("apply_error");
        builder.HasOne(c => c.Batch).WithMany().HasForeignKey(c => c.BatchId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => new { c.BatchId, c.Ordinal }).IsUnique().HasDatabaseName("ux_facebook_rewrite_chunk");
    }
}
