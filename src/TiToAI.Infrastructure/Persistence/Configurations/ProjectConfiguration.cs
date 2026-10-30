using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TiToAI.Infrastructure.Persistence.Entities;

namespace TiToAI.Infrastructure.Persistence.Configurations;

public class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("Project");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .HasColumnName("id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(p => p.Name)
            .HasColumnName("title")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(p => p.Brief)
            .HasColumnName("brief")
            .HasColumnType("TEXT")
            .HasDefaultValue(string.Empty)
            .IsRequired();

        builder.Property(p => p.Status)
            .HasColumnName("status")
            .HasColumnType("TEXT")
            .HasDefaultValue("Draft")
            .IsRequired();

        builder.Property(p => p.CurrentVideoRevisionId)
            .HasColumnName("current_video_revision_id")
            .HasColumnType("TEXT");

        builder.Property(p => p.GroupId)
            .HasColumnName("group_id")
            .HasColumnType("TEXT");

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

        builder.HasOne<VideoRevision>()
            .WithMany()
            .HasForeignKey(p => new { p.CurrentVideoRevisionId, p.Id })
            .HasPrincipalKey(v => new { v.Id, v.ProjectId })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<ProjectGroup>()
            .WithMany()
            .HasForeignKey(p => p.GroupId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(p => p.Status)
            .HasDatabaseName("ix_project_status_updated");

        builder.HasIndex(p => p.GroupId)
            .HasDatabaseName("ix_project_group_id");
    }
}
