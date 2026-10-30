using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

public sealed class CrmHistoryEntryConfiguration : IEntityTypeConfiguration<CrmHistoryEntry>
{
    public void Configure(EntityTypeBuilder<CrmHistoryEntry> e)
    {
        e.ToTable("CrmHistoryEntries"); e.HasKey(x => x.Id);
        e.Property(x => x.RelationshipKey).HasMaxLength(100).IsRequired();
        e.Property(x => x.RecordedBy).HasMaxLength(450).IsRequired();
        e.Property(x => x.OccurredDate).HasMaxLength(10);
        e.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
        e.Property(x => x.TimePrecision).HasMaxLength(20).IsRequired();
        e.Property(x => x.Type).HasMaxLength(60).IsRequired();
        e.Property(x => x.Source).HasMaxLength(80).IsRequired();
        e.Property(x => x.SourceId).HasMaxLength(200).IsRequired();
        e.Property(x => x.ContentJson).IsRequired();
        e.Property(x => x.Product).HasMaxLength(100);
        e.Property(x => x.FormName).HasMaxLength(160);
        e.Property(x => x.FormVersion).HasMaxLength(100);
        e.Property(x => x.CaseReference).HasMaxLength(200);
        e.Property(x => x.AssignedOwner).HasMaxLength(450);
        e.Property(x => x.Status).HasMaxLength(40).IsRequired();
        e.Property(x => x.EvidenceStatus).HasMaxLength(80).IsRequired();
        e.HasIndex(x => new { x.RelationshipKey, x.Source, x.SourceId }).IsUnique();
        e.HasIndex(x => new { x.RelationshipKey, x.OccurredUtc, x.Id });
        e.HasIndex(x => new { x.RelationshipKey, x.RecordedUtc, x.Id });
        e.HasIndex(x => x.SubmissionId);
    }
}
