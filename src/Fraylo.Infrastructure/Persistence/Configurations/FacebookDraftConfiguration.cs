using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public sealed class FacebookDraftConfiguration : IEntityTypeConfiguration<FacebookDraft>
{
    public void Configure(EntityTypeBuilder<FacebookDraft> builder)
    {
        builder.ToTable("FacebookDraft");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.DueAtUtc).HasColumnName("due_at_utc").HasColumnType("TEXT");

        builder.Property(p => p.Id).HasColumnName("id").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.Name).HasColumnName("name").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.ProfileId).HasColumnName("profile_id").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.Text).HasColumnName("text").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.RewriteContent).HasColumnName("rewrite_content").HasColumnType("INTEGER").HasDefaultValue(true).IsRequired();
        builder.Property(p => p.TargetIdsJson).HasColumnName("target_ids_json").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.ImageAssetIdsJson).HasColumnName("image_asset_ids_json").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.Revision).HasColumnName("revision").HasColumnType("INTEGER").IsRequired();
        builder.Property(p => p.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("TEXT").IsRequired();
        builder.HasIndex(p => p.UpdatedAtUtc).HasDatabaseName("ix_facebook_draft_updated");
    }
}
