using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public sealed class FacebookPostTaskConfiguration : IEntityTypeConfiguration<FacebookPostTask>
{
    public void Configure(EntityTypeBuilder<FacebookPostTask> builder)
    {
        builder.ToTable("FacebookPostTask", t => t.HasCheckConstraint("ck_facebook_task_state", "state IN ('Queued','Preparing','Submitting','Published','PendingApproval','Failed','Unknown','NeedsUser','Canceled')"));
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.BatchId).HasColumnName("batch_id").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.TargetId).HasColumnName("target_id").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.ProfileId).HasColumnName("profile_id").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.AccountIdentitySnapshot).HasColumnName("account_identity_snapshot").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.GroupNameSnapshot).HasColumnName("group_name_snapshot").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.GroupUrlSnapshot).HasColumnName("group_url_snapshot").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.TextSnapshot).HasColumnName("text_snapshot").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.State).HasColumnName("state").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.ReasonCode).HasColumnName("reason_code").HasColumnType("TEXT");
        builder.Property(p => p.Message).HasColumnName("message").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.SubmittedAtUtc).HasColumnName("submitted_at_utc").HasColumnType("TEXT");
        builder.Property(p => p.LeaseOwnerId).HasColumnName("lease_owner_id").HasColumnType("TEXT");
        builder.Property(p => p.LeaseExpiresAtUtc).HasColumnName("lease_expires_at_utc").HasColumnType("TEXT");
        builder.Property(p => p.CancelRequested).HasColumnName("cancel_requested").HasColumnType("INTEGER").IsRequired();
        builder.Property(p => p.Revision).HasColumnName("revision").HasColumnType("INTEGER").IsRequired();
        builder.Property(p => p.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("TEXT").IsRequired();
        builder.Property(p => p.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("TEXT").IsRequired();
        builder.HasOne<FacebookTarget>().WithMany().HasForeignKey(p => new { p.TargetId, p.ProfileId }).HasPrincipalKey(p => new { p.Id, p.ProfileId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FacebookPostBatch>().WithMany().HasForeignKey(p => p.BatchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => new { p.State, p.CreatedAtUtc }).HasDatabaseName("ix_facebook_task_state_created");
        builder.HasIndex(p => new { p.ProfileId, p.SubmittedAtUtc }).HasDatabaseName("ix_facebook_task_profile_submitted");
        builder.HasIndex(p => p.ProfileId).IsUnique().HasFilter("state IN ('Preparing','Submitting')").HasDatabaseName("uq_facebook_task_active_profile");
    }
}
