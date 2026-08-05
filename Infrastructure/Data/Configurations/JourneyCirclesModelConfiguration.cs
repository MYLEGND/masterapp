using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Configurations;

internal static class JourneyCirclesModelConfiguration
{
    internal static void Configure(ModelBuilder modelBuilder, string? providerName)
    {
        modelBuilder.Entity<JourneyCircleProfile>(entity =>
        {
            entity.ToTable("JourneyCircleProfiles"); entity.HasKey(x => x.Id);
            entity.Property(x => x.DisplayName).HasMaxLength(100).IsRequired(); entity.Property(x => x.LifeStage).HasMaxLength(80); entity.Property(x => x.LocationLabel).HasMaxLength(100); entity.Property(x => x.Introduction).HasMaxLength(600);
            entity.Property(x => x.GoalsJson).HasColumnType(IsSqlServer(providerName) ? "nvarchar(max)" : "TEXT").IsRequired(); entity.Property(x => x.InterestsJson).HasColumnType(IsSqlServer(providerName) ? "nvarchar(max)" : "TEXT").IsRequired(); entity.Property(x => x.CircleCodesJson).HasColumnType(IsSqlServer(providerName) ? "nvarchar(max)" : "TEXT").IsRequired(); entity.Property(x => x.ConnectionTypesJson).HasColumnType(IsSqlServer(providerName) ? "nvarchar(max)" : "TEXT").IsRequired();
            entity.Property(x => x.CommunicationStyle).HasMaxLength(80); entity.Property(x => x.AccountabilityFrequency).HasMaxLength(80); entity.Property(x => x.CommunityAccessState).HasMaxLength(40);
            entity.HasIndex(x => x.ClientProfileId).IsUnique(); entity.HasIndex(x => new { x.IsOptedIn, x.IsDiscoverable, x.AllowSuggestions });
            entity.HasOne(x => x.ClientProfile).WithMany().HasForeignKey(x => x.ClientProfileId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<JourneyCircleConnection>(entity =>
        {
            entity.ToTable("JourneyCircleConnections"); entity.HasKey(x => x.Id); entity.Property(x => x.ConnectionKey).HasMaxLength(80).IsRequired(); entity.Property(x => x.Status).HasMaxLength(40).IsRequired(); entity.Property(x => x.ConnectionReason).HasMaxLength(160); entity.Property(x => x.Introduction).HasMaxLength(600);
            entity.HasIndex(x => x.ConnectionKey).IsUnique(); entity.HasIndex(x => new { x.RecipientClientProfileId, x.Status }); entity.HasIndex(x => new { x.RequesterClientProfileId, x.Status });
        });
        modelBuilder.Entity<JourneyCircleBlock>(entity =>
        {
            entity.ToTable("JourneyCircleBlocks"); entity.HasKey(x => x.Id);
            entity.Property(x => x.BlockerUserId).HasMaxLength(450);
            entity.Property(x => x.BlockerParticipantType).HasMaxLength(40);
            entity.Property(x => x.BlockedUserId).HasMaxLength(450);
            entity.Property(x => x.BlockedParticipantType).HasMaxLength(40);
            entity.HasIndex(x => new { x.BlockerClientProfileId, x.BlockedClientProfileId }).IsUnique().HasFilter("[BlockerClientProfileId] IS NOT NULL AND [BlockedClientProfileId] IS NOT NULL");
            entity.HasIndex(x => new { x.BlockerUserId, x.BlockerParticipantType, x.BlockedUserId, x.BlockedParticipantType }).IsUnique().HasFilter("[BlockerUserId] IS NOT NULL AND [BlockerParticipantType] IS NOT NULL AND [BlockedUserId] IS NOT NULL AND [BlockedParticipantType] IS NOT NULL");
        });
        modelBuilder.Entity<JourneyCircleReport>(entity =>
        {
            entity.ToTable("JourneyCircleReports"); entity.HasKey(x => x.Id);
            entity.Property(x => x.ReporterUserId).HasMaxLength(450); entity.Property(x => x.ReporterParticipantType).HasMaxLength(40);
            entity.Property(x => x.ReportedUserId).HasMaxLength(450); entity.Property(x => x.ReportedParticipantType).HasMaxLength(40);
            entity.Property(x => x.TargetKind).HasMaxLength(80); entity.Property(x => x.Category).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Detail).HasMaxLength(600); entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
            entity.Property(x => x.ResolvedByUserId).HasMaxLength(450); entity.Property(x => x.Resolution).HasMaxLength(80);
            entity.HasIndex(x => new { x.Status, x.CreatedUtc });
            entity.HasIndex(x => new { x.ReportedUserId, x.ReportedParticipantType, x.Status });
        });
        modelBuilder.Entity<JourneyCircleModerationEvent>(entity => { entity.ToTable("JourneyCircleModerationEvents"); entity.HasKey(x => x.Id); entity.Property(x => x.ActorUserId).HasMaxLength(450).IsRequired(); entity.Property(x => x.Surface).HasMaxLength(80).IsRequired(); entity.Property(x => x.Category).HasMaxLength(80).IsRequired(); entity.Property(x => x.Severity).HasMaxLength(40).IsRequired(); entity.Property(x => x.Action).HasMaxLength(80).IsRequired(); entity.Property(x => x.PolicyVersion).HasMaxLength(40).IsRequired(); entity.HasIndex(x => new { x.RequiresReview, x.CreatedUtc }); });
    }

    private static bool IsSqlServer(string? providerName) => providerName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true;
}
