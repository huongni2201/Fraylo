using Fraylo.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public sealed class FacebookAnalyticsPageConfiguration : IEntityTypeConfiguration<FacebookAnalyticsPage>
{
    public void Configure(EntityTypeBuilder<FacebookAnalyticsPage> b)
    {
        b.ToTable("FacebookAnalyticsPage");
        b.HasKey(x => x.Id);
        b.HasOne<BrowserProfile>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ProfileId, x.FacebookPageId }).IsUnique();
    }
}

public sealed class FacebookAnalyticsCaptureConfiguration : IEntityTypeConfiguration<FacebookAnalyticsCapture>
{
    public void Configure(EntityTypeBuilder<FacebookAnalyticsCapture> b)
    {
        b.Property(x => x.ActivityLogJson).HasColumnName("activity_log_json").HasDefaultValue("[]").IsRequired();
        b.ToTable("FacebookAnalyticsCapture", t => t.HasCheckConstraint("ck_fba_capture_status", "\"Status\" IN ('Running','Complete','Partial','Failed','Canceled')"));
        b.HasKey(x => x.Id);
        b.HasOne<FacebookAnalyticsPage>().WithMany().HasForeignKey(x => x.PageId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PageId, x.StartedAtUtc });
    }
}

public sealed class FacebookContentMetricSnapshotConfiguration : IEntityTypeConfiguration<FacebookContentMetricSnapshot>
{
    public void Configure(EntityTypeBuilder<FacebookContentMetricSnapshot> b)
    {
        b.ToTable("FacebookContentMetricSnapshot");
        b.HasKey(x => x.Id);
        b.HasOne<FacebookAnalyticsCapture>().WithMany().HasForeignKey(x => x.CaptureId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.CaptureId, x.ContentId }).IsUnique();
        b.HasIndex(x => x.ContentId);
    }
}
