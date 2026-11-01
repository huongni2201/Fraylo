using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public sealed class VideoRevisionConfiguration : IEntityTypeConfiguration<VideoRevision>
{
    public void Configure(EntityTypeBuilder<VideoRevision> builder)
    {
        builder.ToTable("VideoRevision");
        builder.HasKey(v => v.Id);
        builder.HasAlternateKey(v => new { v.Id, v.ProjectId });

        builder.Property(v => v.Id).HasColumnName("id").HasColumnType("TEXT").IsRequired();
        builder.Property(v => v.ProjectId).HasColumnName("project_id").HasColumnType("TEXT").IsRequired();
        builder.Property(v => v.Revision).HasColumnName("revision").HasColumnType("INTEGER").IsRequired();
        builder.Property(v => v.State).HasColumnName("state").HasColumnType("TEXT").IsRequired();
        builder.Property(v => v.AssetId).HasColumnName("asset_id").HasColumnType("TEXT");
        builder.Property(v => v.ContentHash).HasColumnName("content_hash").HasColumnType("TEXT");
        builder.Property(v => v.DurationSeconds).HasColumnName("duration_seconds").HasColumnType("REAL");
        builder.Property(v => v.Width).HasColumnName("width").HasColumnType("INTEGER");
        builder.Property(v => v.Height).HasColumnName("height").HasColumnType("INTEGER");
        builder.Property(v => v.ProvenanceJson).HasColumnName("provenance_json").HasColumnType("TEXT");
        builder.Property(v => v.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("TEXT").IsRequired();

        builder.HasOne(v => v.Project).WithMany().HasForeignKey(v => v.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(v => v.Asset).WithMany().HasForeignKey(v => v.AssetId).OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(v => new { v.ProjectId, v.Revision }).IsUnique().HasDatabaseName("uq_video_revision_project_revision");
        builder.HasIndex(v => v.AssetId).HasDatabaseName("ix_video_revision_asset");
    }
}
