using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public sealed class PublishRecordConfiguration : IEntityTypeConfiguration<PublishRecord>
{
    public void Configure(EntityTypeBuilder<PublishRecord> builder)
    {
        builder.ToTable("PublishRecord");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id).HasColumnName("id").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.ProjectId).HasColumnName("project_id").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.VideoRevisionId).HasColumnName("video_revision_id").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.AccountId).HasColumnName("account_id").HasColumnType("TEXT");
        builder.Property(p => p.CaptionSnapshot).HasColumnName("caption_snapshot").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.ExternalPostUrl).HasColumnName("external_post_url").HasColumnType("TEXT");
        builder.Property(p => p.ExternalPostId).HasColumnName("external_post_id").HasColumnType("TEXT");
        builder.Property(p => p.State).HasColumnName("state").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.UserEnteredAtUtc).HasColumnName("user_entered_at_utc").HasColumnType("TEXT");
        builder.Property(p => p.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("TEXT").IsRequired();

        builder.HasOne(p => p.Project).WithMany().HasForeignKey(p => p.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(p => p.VideoRevision).WithMany()
            .HasForeignKey(p => new { p.VideoRevisionId, p.ProjectId })
            .HasPrincipalKey(v => new { v.Id, v.ProjectId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => new { p.ProjectId, p.CreatedAtUtc }).HasDatabaseName("ix_publish_record_project_created");
        builder.HasIndex(p => p.AccountId).HasDatabaseName("ix_publish_record_account");
        builder.HasIndex(p => p.ExternalPostId).IsUnique().HasFilter("external_post_id IS NOT NULL").HasDatabaseName("uq_publish_record_external_post");
    }
}
