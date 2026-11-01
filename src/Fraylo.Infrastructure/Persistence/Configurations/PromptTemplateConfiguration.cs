using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Infrastructure.Persistence.Configurations;

public class PromptTemplateConfiguration : IEntityTypeConfiguration<PromptTemplate>
{
    public void Configure(EntityTypeBuilder<PromptTemplate> builder)
    {
        builder.ToTable("PromptTemplate");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id)
            .HasColumnName("id")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(t => t.Purpose)
            .HasColumnName("purpose")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(t => t.Name).HasColumnName("name").HasColumnType("TEXT").IsRequired();
        builder.Property(t => t.IsActive).HasColumnName("is_active").IsRequired();

        builder.Property(t => t.TemplateText)
            .HasColumnName("template_text")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(t => t.VariableDefinitionsJson)
            .HasColumnName("variable_definitions_json")
            .HasColumnType("TEXT")
            .HasDefaultValue("[]")
            .IsRequired();

        builder.Property(t => t.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(t => t.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.HasIndex(t => t.Purpose)
            .IsUnique()
            .HasFilter("\"is_active\" = 1")
            .HasDatabaseName("uq_prompt_template_active_purpose");
    }
}
