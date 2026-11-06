using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        builder.ToTable("Asset");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .HasColumnName("id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(a => a.ProjectId)
            .HasColumnName("project_id")
            .HasColumnType("TEXT");

        builder.Property(a => a.StorageRootId)
            .HasColumnName("storage_root_id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(a => a.RelativePath)
            .HasColumnName("relative_path")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(a => a.AssetType)
            .HasColumnName("asset_type")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(a => a.MimeType)
            .HasColumnName("mime_type")
            .HasColumnType("TEXT");

        builder.Property(a => a.ByteSize)
            .HasColumnName("byte_size")
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(a => a.Sha256Hash)
            .HasColumnName("sha256_hash")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(a => a.Status)
            .HasColumnName("status")
            .HasColumnType("TEXT")
            .HasDefaultValue("Linked")
            .IsRequired();

        builder.Property(a => a.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.HasOne(a => a.Project)
            .WithMany()
            .HasForeignKey(a => a.ProjectId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(a => a.ProjectId)
            .HasDatabaseName("ix_asset_project");
    }
}
