using Microsoft.EntityFrameworkCore;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Infrastructure.Persistence;

public class FrayloDbContext : DbContext
{
    public FrayloDbContext(DbContextOptions<FrayloDbContext> options)
        : base(options)
    {
    }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectGroup> ProjectGroups => Set<ProjectGroup>();
    public DbSet<Scene> Scenes => Set<Scene>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<PromptTemplate> PromptTemplates => Set<PromptTemplate>();
    public DbSet<BrowserProfile> BrowserProfiles => Set<BrowserProfile>();
    public DbSet<BrowserHandoffCheckpoint> BrowserHandoffCheckpoints => Set<BrowserHandoffCheckpoint>();
    public DbSet<ProviderAccount> ProviderAccounts => Set<ProviderAccount>();
    public DbSet<ExecutionBinding> ExecutionBindings => Set<ExecutionBinding>();
    public DbSet<ActiveSlotOccupancy> ActiveSlotOccupancies => Set<ActiveSlotOccupancy>();
    public DbSet<AnalyticsSnapshot> AnalyticsSnapshots => Set<AnalyticsSnapshot>();
    public DbSet<VideoAnalyticsRecord> VideoAnalyticsRecords => Set<VideoAnalyticsRecord>();
    public DbSet<TikTokAnalyticsDailyRecord> TikTokAnalyticsDailyRecords => Set<TikTokAnalyticsDailyRecord>();
    public DbSet<VideoRevision> VideoRevisions => Set<VideoRevision>();
    public DbSet<PublishRecord> PublishRecords => Set<PublishRecord>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FrayloDbContext).Assembly);
    }
}
