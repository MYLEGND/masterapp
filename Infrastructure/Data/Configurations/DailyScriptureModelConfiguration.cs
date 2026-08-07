using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

internal static class DailyScriptureModelConfiguration
{
    internal static void Configure(ModelBuilder modelBuilder, string? providerName)
    {
        var entity = modelBuilder.Entity<DailyScriptureOverride>();
        entity.ToTable("DailyScriptureOverrides");
        entity.HasKey(overrideEntry => overrideEntry.Id);
        entity.Property(overrideEntry => overrideEntry.Reference).IsRequired().HasMaxLength(240);
        entity.Property(overrideEntry => overrideEntry.Translation).IsRequired().HasMaxLength(40);
        entity.Property(overrideEntry => overrideEntry.PassageText).IsRequired()
            .HasColumnType(providerName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true
                ? "nvarchar(max)"
                : "TEXT");
        entity.Property(overrideEntry => overrideEntry.CreatedByUserId).IsRequired().HasMaxLength(450);
        entity.Property(overrideEntry => overrideEntry.CreatedByParticipantType).IsRequired().HasMaxLength(40);
        entity.Property(overrideEntry => overrideEntry.UpdatedByUserId).IsRequired().HasMaxLength(450);
        entity.Property(overrideEntry => overrideEntry.UpdatedByParticipantType).IsRequired().HasMaxLength(40);

        if (providerName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true)
        {
            entity.Property(overrideEntry => overrideEntry.RowVersion).IsRowVersion();
        }
        else
        {
            entity.Property(overrideEntry => overrideEntry.RowVersion).IsConcurrencyToken();
        }

        var activeDate = entity.HasIndex(overrideEntry => overrideEntry.DisplayDate).IsUnique();
        if (providerName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true)
        {
            activeDate.HasFilter("[IsActive] = 1");
        }
        else if (providerName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        {
            activeDate.HasFilter("\"IsActive\" = 1");
        }

        entity.HasIndex(overrideEntry => new { overrideEntry.IsActive, overrideEntry.DisplayDate });
    }
}
