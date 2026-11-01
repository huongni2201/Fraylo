using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public class AnalyticsSnapshotConfiguration : IEntityTypeConfiguration<AnalyticsSnapshot>
{
    public void Configure(EntityTypeBuilder<AnalyticsSnapshot> builder)
    {
        builder.ToTable("AnalyticsSnapshot");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .HasColumnName("id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(s => s.AccountId)
            .HasColumnName("account_id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(s => s.Source)
            .HasColumnName("source")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(s => s.PeriodStart)
            .HasColumnName("period_start")
            .HasColumnType("TEXT");

        builder.Property(s => s.PeriodEnd)
            .HasColumnName("period_end")
            .HasColumnType("TEXT");

        builder.Property(s => s.RecordCount)
            .HasColumnName("record_count")
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(s => s.RawManifest)
            .HasColumnName("raw_manifest")
            .HasColumnType("TEXT");

        builder.Property(s => s.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.HasIndex(s => new { s.AccountId, s.CreatedAtUtc })
            .HasDatabaseName("ix_analytics_snapshot_account_created");

        builder.HasMany(s => s.Records)
            .WithOne(r => r.Snapshot)
            .HasForeignKey(r => r.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(s => s.DailyRecords)
            .WithOne(r => r.Snapshot)
            .HasForeignKey(r => r.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class TikTokAnalyticsDailyRecordConfiguration : IEntityTypeConfiguration<TikTokAnalyticsDailyRecord>
{
    public void Configure(EntityTypeBuilder<TikTokAnalyticsDailyRecord> builder)
    {
        builder.ToTable("TikTokAnalyticsDailyRecord");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id).HasColumnName("id").HasColumnType("TEXT").IsRequired();
        builder.Property(r => r.SnapshotId).HasColumnName("snapshot_id").HasColumnType("TEXT").IsRequired();
        builder.Property(r => r.AccountId).HasColumnName("account_id").HasColumnType("TEXT").IsRequired();
        builder.Property(r => r.DataType).HasColumnName("data_type").HasColumnType("TEXT").IsRequired();
        builder.Property(r => r.PeriodDateUtc).HasColumnName("period_date_utc").HasColumnType("TEXT").IsRequired();
        builder.Property(r => r.VideoViews).HasColumnName("video_views").HasColumnType("INTEGER");
        builder.Property(r => r.ProfileViews).HasColumnName("profile_views").HasColumnType("INTEGER");
        builder.Property(r => r.Likes).HasColumnName("likes").HasColumnType("INTEGER");
        builder.Property(r => r.Comments).HasColumnName("comments").HasColumnType("INTEGER");
        builder.Property(r => r.Shares).HasColumnName("shares").HasColumnType("INTEGER");
        builder.Property(r => r.TotalViewers).HasColumnName("total_viewers").HasColumnType("INTEGER");
        builder.Property(r => r.NewViewers).HasColumnName("new_viewers").HasColumnType("INTEGER");
        builder.Property(r => r.ReturningViewers).HasColumnName("returning_viewers").HasColumnType("INTEGER");
        builder.Property(r => r.CapturedAtUtc).HasColumnName("captured_at_utc").HasColumnType("TEXT").IsRequired();

        builder.HasOne(r => r.Snapshot)
            .WithMany(s => s.DailyRecords)
            .HasForeignKey(r => r.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => new { r.AccountId, r.DataType, r.PeriodDateUtc })
            .HasDatabaseName("ix_tiktok_analytics_daily_account_type_date");
        builder.HasIndex(r => r.SnapshotId)
            .HasDatabaseName("ix_tiktok_analytics_daily_snapshot_id");
    }
}

public class VideoAnalyticsRecordConfiguration : IEntityTypeConfiguration<VideoAnalyticsRecord>
{
    public void Configure(EntityTypeBuilder<VideoAnalyticsRecord> builder)
    {
        builder.ToTable("VideoAnalyticsRecord");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id)
            .HasColumnName("id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(r => r.SnapshotId)
            .HasColumnName("snapshot_id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(r => r.AccountId)
            .HasColumnName("account_id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(r => r.VideoKey)
            .HasColumnName("video_key")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(r => r.Title)
            .HasColumnName("title")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(r => r.Views)
            .HasColumnName("views")
            .HasColumnType("INTEGER");

        builder.Property(r => r.Likes)
            .HasColumnName("likes")
            .HasColumnType("INTEGER");

        builder.Property(r => r.Comments)
            .HasColumnName("comments")
            .HasColumnType("INTEGER");

        builder.Property(r => r.Shares)
            .HasColumnName("shares")
            .HasColumnType("INTEGER");

        builder.Property(r => r.WatchTimeAvgSec)
            .HasColumnName("watch_time_avg_sec")
            .HasColumnType("REAL");

        builder.Property(r => r.GmvAmount)
            .HasColumnName("gmv_amount")
            .HasColumnType("TEXT");

        builder.Property(r => r.AffiliateClicks)
            .HasColumnName("affiliate_clicks")
            .HasColumnType("INTEGER");

        builder.Property(r => r.PublishedAtUtc)
            .HasColumnName("published_at_utc")
            .HasColumnType("TEXT");

        builder.Property(r => r.CapturedAtUtc)
            .HasColumnName("captured_at_utc")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.HasOne(r => r.Snapshot)
            .WithMany(s => s.Records)
            .HasForeignKey(r => r.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => new { r.AccountId, r.VideoKey })
            .HasDatabaseName("ix_video_analytics_account_video");

        builder.HasIndex(r => new { r.AccountId, r.CapturedAtUtc })
            .HasDatabaseName("ix_video_analytics_account_captured");

        builder.HasIndex(r => r.SnapshotId)
            .HasDatabaseName("ix_video_analytics_snapshot_id");
    }
}
