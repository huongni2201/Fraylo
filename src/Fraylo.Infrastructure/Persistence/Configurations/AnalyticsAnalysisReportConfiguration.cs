using Fraylo.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public sealed class AnalyticsAnalysisReportConfiguration : IEntityTypeConfiguration<AnalyticsAnalysisReport>
{
    public void Configure(EntityTypeBuilder<AnalyticsAnalysisReport> builder)
    {
        builder.ToTable("AnalyticsAnalysisReport", t => t.HasCheckConstraint("CK_AnalyticsAnalysisReport_Scope",
            "(platform = 'tiktok' AND page_id IS NULL) OR (platform = 'facebook' AND page_id IS NOT NULL)"));
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.Platform).HasColumnName("platform");
        builder.Property(r => r.AccountId).HasColumnName("account_id");
        builder.Property(r => r.PageId).HasColumnName("page_id");
        builder.Property(r => r.SourceId).HasColumnName("source_id");
        builder.Property(r => r.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(r => r.ProviderKey).HasColumnName("provider_key");
        builder.Property(r => r.SettingsRevision).HasColumnName("settings_revision");
        builder.Property(r => r.PreparationReason).HasColumnName("preparation_reason");
        builder.Property(r => r.InputJson).HasColumnName("input_json");
        builder.Property(r => r.InputHash).HasColumnName("input_hash");
        builder.HasIndex(r => new { r.Platform, r.AccountId, r.PageId, r.CreatedAtUtc })
            .HasDatabaseName("ix_analysis_report_scope");
    }
}
