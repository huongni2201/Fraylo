using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public sealed class FacebookPostBatchConfiguration : IEntityTypeConfiguration<FacebookPostBatch>
{
    public void Configure(EntityTypeBuilder<FacebookPostBatch> builder)
    {
        builder.ToTable("FacebookPostBatch");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.ProfileId).HasColumnName("profile_id").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.RequestHash).HasColumnName("request_hash").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("TEXT").IsRequired();
        builder.HasOne<BrowserProfile>().WithMany().HasForeignKey(p => p.ProfileId).OnDelete(DeleteBehavior.Restrict);
    }
}
