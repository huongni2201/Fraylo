using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public class SceneConfiguration : IEntityTypeConfiguration<Scene>
{
    public void Configure(EntityTypeBuilder<Scene> builder)
    {
        builder.ToTable("Scene");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .HasColumnName("id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(s => s.ProjectId)
            .HasColumnName("project_id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(s => s.SequenceOrder)
            .HasColumnName("sequence_order")
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(s => s.Name)
            .HasColumnName("name")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(s => s.TargetDurationSeconds)
            .HasColumnName("target_duration_seconds")
            .HasColumnType("REAL")
            .IsRequired();

        builder.Property(s => s.PromptText)
            .HasColumnName("prompt_text")
            .HasColumnType("TEXT");

        builder.Property(s => s.Status)
            .HasColumnName("status")
            .HasColumnType("TEXT")
            .HasDefaultValue("Empty")
            .IsRequired();

        builder.Property(s => s.AudioAssetId)
            .HasColumnName("audio_asset_id")
            .HasColumnType("TEXT");

        builder.Property(s => s.ImageAssetId)
            .HasColumnName("image_asset_id")
            .HasColumnType("TEXT");

        builder.Property(s => s.VideoAssetId)
            .HasColumnName("video_asset_id")
            .HasColumnType("TEXT");

        builder.Property(s => s.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(s => s.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.HasOne(s => s.Project)
            .WithMany()
            .HasForeignKey(s => s.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(s => s.AudioAsset)
            .WithMany()
            .HasForeignKey(s => s.AudioAssetId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(s => s.ImageAsset)
            .WithMany()
            .HasForeignKey(s => s.ImageAssetId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(s => s.VideoAsset)
            .WithMany()
            .HasForeignKey(s => s.VideoAssetId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasAlternateKey(s => new { s.Id, s.ProjectId })
            .HasName("uq_scene_id_project");

        builder.HasIndex(s => new { s.ProjectId, s.SequenceOrder })
            .IsUnique()
            .HasDatabaseName("uq_scene_project_sequence");
    }
}
