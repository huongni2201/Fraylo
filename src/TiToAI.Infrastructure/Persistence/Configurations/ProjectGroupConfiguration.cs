using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TiToAI.Infrastructure.Persistence.Entities;

namespace TiToAI.Infrastructure.Persistence.Configurations;

public class ProjectGroupConfiguration : IEntityTypeConfiguration<ProjectGroup>
{
    public void Configure(EntityTypeBuilder<ProjectGroup> builder)
    {
        builder.ToTable("ProjectGroup");

        builder.HasKey(g => g.Id);

        builder.Property(g => g.Id)
            .HasColumnName("id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(g => g.Name)
            .HasColumnName("name")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(g => g.Description)
            .HasColumnName("description")
            .HasColumnType("TEXT")
            .HasDefaultValue(string.Empty)
            .IsRequired();

        builder.Property(g => g.ScriptGuidance)
            .HasColumnName("script_guidance")
            .HasColumnType("TEXT")
            .HasDefaultValue(string.Empty)
            .IsRequired();

        builder.Property(g => g.VideoGuidance)
            .HasColumnName("video_guidance")
            .HasColumnType("TEXT")
            .HasDefaultValue(string.Empty)
            .IsRequired();

        builder.Property(g => g.PromptVariablesJson)
            .HasColumnName("prompt_variables_json")
            .HasColumnType("TEXT")
            .HasDefaultValue("{}")
            .IsRequired();

        builder.Property(g => g.ModelAssetId)
            .HasColumnName("model_asset_id")
            .HasColumnType("TEXT");

        builder.Property(g => g.ProductAssetId)
            .HasColumnName("product_asset_id")
            .HasColumnType("TEXT");

        builder.Property(g => g.ReadyImageAssetId)
            .HasColumnName("ready_image_asset_id")
            .HasColumnType("TEXT");

        builder.Property(g => g.AudioAssetId)
            .HasColumnName("audio_asset_id")
            .HasColumnType("TEXT");

        builder.HasOne<Asset>()
            .WithMany()
            .HasForeignKey(g => g.ModelAssetId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<Asset>()
            .WithMany()
            .HasForeignKey(g => g.ProductAssetId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<Asset>()
            .WithMany()
            .HasForeignKey(g => g.ReadyImageAssetId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<Asset>()
            .WithMany()
            .HasForeignKey(g => g.AudioAssetId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Property(g => g.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(g => g.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.HasIndex(g => g.Name)
            .IsUnique()
            .HasDatabaseName("uq_project_group_name");
    }
}
