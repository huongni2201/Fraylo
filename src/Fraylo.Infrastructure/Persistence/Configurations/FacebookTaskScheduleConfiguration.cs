using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public sealed class FacebookTaskScheduleConfiguration : IEntityTypeConfiguration<FacebookTaskSchedule>
{
    public void Configure(EntityTypeBuilder<FacebookTaskSchedule> builder)
    {
        builder.ToTable("FacebookTaskSchedule");
        builder.HasKey(p => p.TaskId);
        builder.Property(p => p.TaskId).HasColumnName("task_id").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.DueAtUtc).HasColumnName("due_at_utc").HasColumnType("TEXT").IsRequired();
        builder.HasOne<FacebookPostTask>().WithOne().HasForeignKey<FacebookTaskSchedule>(p => p.TaskId).OnDelete(DeleteBehavior.Cascade);
    }
}
