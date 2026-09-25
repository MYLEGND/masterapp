using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

public sealed class MarketingConnectionConfiguration : IEntityTypeConfiguration<MarketingConnection>
{
    public void Configure(EntityTypeBuilder<MarketingConnection> e)
    {
        e.ToTable("MarketingConnections", table => table.HasCheckConstraint("CK_MarketingConnection_Owner",
            "([OwnerType] = 'founder' AND [AgentTrackingProfileId] IS NULL AND [CommerceBusinessId] IS NULL) OR " +
            "([OwnerType] = 'agent' AND [AgentTrackingProfileId] IS NOT NULL AND [CommerceBusinessId] IS NULL) OR " +
            "([OwnerType] = 'business' AND [CommerceBusinessId] IS NOT NULL AND [AgentTrackingProfileId] IS NULL)"));
        e.HasKey(x => x.Id);
        e.Property(x => x.OwnerKey).HasMaxLength(64).IsRequired();
        e.Property(x => x.OwnerType).HasMaxLength(16).IsRequired();
        e.Property(x => x.Provider).HasMaxLength(20).IsRequired();
        e.HasIndex(x => new { x.OwnerKey, x.Provider }).IsUnique();
        e.HasIndex(x => new { x.AgentTrackingProfileId, x.Provider }).IsUnique().HasFilter("[AgentTrackingProfileId] IS NOT NULL");
        e.HasIndex(x => new { x.CommerceBusinessId, x.Provider }).IsUnique().HasFilter("[CommerceBusinessId] IS NOT NULL");
        e.HasOne<AgentTrackingProfile>().WithMany().HasForeignKey(x => x.AgentTrackingProfileId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<CommerceBusiness>().WithMany().HasForeignKey(x => x.CommerceBusinessId).OnDelete(DeleteBehavior.Restrict);
        e.Property(x => x.PixelId).HasMaxLength(32);
        e.Property(x => x.TestEventCode).HasMaxLength(100);
        e.Property(x => x.AdAccountId).HasMaxLength(100);
        e.Property(x => x.AdAccountName).HasMaxLength(300);
        e.Property(x => x.MetaBusinessManagerId).HasMaxLength(100);
        e.Property(x => x.MetaBusinessManagerName).HasMaxLength(300);
        e.Property(x => x.MetaUserId).HasMaxLength(100);
        e.Property(x => x.MetaUserName).HasMaxLength(300);
        e.Property(x => x.Revision).IsConcurrencyToken();
    }
}
