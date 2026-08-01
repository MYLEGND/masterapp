using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

internal static class MessagingModelConfiguration
{
    internal static void Configure(
        ModelBuilder modelBuilder,
        string? providerName)
    {
        ConfigureConversation(modelBuilder.Entity<MessageConversation>(), providerName);
        ConfigureParticipant(modelBuilder.Entity<MessageConversationParticipant>());
        ConfigureMessage(modelBuilder.Entity<InternalMessage>(), providerName);
        ConfigureAttachment(modelBuilder.Entity<MessageAttachment>());
        ConfigureGrant(modelBuilder.Entity<ClientAgentMessagingGrant>());
        ConfigureAuditEntry(modelBuilder.Entity<MessagingAuditEntry>());
    }

    private static void ConfigureConversation(
        EntityTypeBuilder<MessageConversation> entity,
        string? providerName)
    {
        entity.ToTable("MessageConversations");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.ConversationType)
            .IsRequired()
            .HasMaxLength(40);

        entity.Property(x => x.DirectConversationKey)
            .HasMaxLength(1_000);

        var directConversationKeyIndex = entity
            .HasIndex(x => x.DirectConversationKey)
            .IsUnique();

        if (IsSqlServer(providerName))
        {
            directConversationKeyIndex.HasFilter("[DirectConversationKey] IS NOT NULL");
        }
        else if (IsSqlite(providerName))
        {
            directConversationKeyIndex.HasFilter("\"DirectConversationKey\" IS NOT NULL");
        }

        entity.Property(x => x.Subject)
            .HasMaxLength(240);

        entity.Property(x => x.Purpose)
            .HasMaxLength(40);

        entity.Property(x => x.CreatedByUserId)
            .IsRequired()
            .HasMaxLength(450);

        entity.Property(x => x.OwnerParticipantType)
            .HasMaxLength(40);

        entity.Property(x => x.OwnerUserId)
            .HasMaxLength(450);

        entity.HasIndex(x => new { x.Purpose, x.CreatedByUserId, x.OwnerParticipantType });

        ConfigureRowVersion(entity.Property(x => x.RowVersion), providerName);

        entity.HasIndex(x => x.LastMessageUtc);

        entity.HasIndex(x => x.IsClosed);
    }

    private static void ConfigureParticipant(
        EntityTypeBuilder<MessageConversationParticipant> entity)
    {
        entity.ToTable("MessageConversationParticipants");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.UserId)
            .IsRequired()
            .HasMaxLength(450);

        entity.Property(x => x.ParticipantType)
            .IsRequired()
            .HasMaxLength(40);

        entity.HasIndex(x => new { x.ConversationId, x.UserId, x.ParticipantType })
            .IsUnique();

        entity.HasIndex(x => new { x.UserId, x.IsActive });

        entity.HasOne(x => x.Conversation)
            .WithMany(x => x.Participants)
            .HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureMessage(
        EntityTypeBuilder<InternalMessage> entity,
        string? providerName)
    {
        entity.ToTable("InternalMessages");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.SenderUserId)
            .IsRequired()
            .HasMaxLength(450);

        entity.Property(x => x.SenderType)
            .IsRequired()
            .HasMaxLength(40);

        entity.Property(x => x.Body)
            .IsRequired()
            .HasMaxLength(10000);

        entity.Property(x => x.ClientMessageId)
            .HasMaxLength(100);

        ConfigureRowVersion(entity.Property(x => x.RowVersion), providerName);

        entity.HasIndex(x => new { x.ConversationId, x.SentUtc });

        var clientMessageIndex = entity
            .HasIndex(x => x.ClientMessageId)
            .IsUnique();

        if (IsSqlServer(providerName))
        {
            clientMessageIndex.HasFilter("[ClientMessageId] IS NOT NULL");
        }
        else if (IsSqlite(providerName))
        {
            clientMessageIndex.HasFilter("\"ClientMessageId\" IS NOT NULL");
        }

        entity.HasOne(x => x.Conversation)
            .WithMany(x => x.Messages)
            .HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureAttachment(
        EntityTypeBuilder<MessageAttachment> entity)
    {
        entity.ToTable("MessageAttachments");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.OriginalFileName)
            .IsRequired()
            .HasMaxLength(255);

        entity.Property(x => x.StoredFileName)
            .IsRequired()
            .HasMaxLength(255);

        entity.Property(x => x.ContentType)
            .IsRequired()
            .HasMaxLength(150);

        entity.Property(x => x.StoragePath)
            .IsRequired()
            .HasMaxLength(1000);

        entity.Property(x => x.ScanStatus)
            .IsRequired()
            .HasMaxLength(40);

        entity.HasIndex(x => x.InternalMessageId);

        entity.HasOne(x => x.InternalMessage)
            .WithMany(x => x.Attachments)
            .HasForeignKey(x => x.InternalMessageId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureGrant(
        EntityTypeBuilder<ClientAgentMessagingGrant> entity)
    {
        entity.ToTable("ClientAgentMessagingGrants");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.ClientUserId)
            .IsRequired()
            .HasMaxLength(450);

        entity.Property(x => x.AgentUserId)
            .IsRequired()
            .HasMaxLength(450);

        entity.Property(x => x.GrantedByAgentUserId)
            .IsRequired()
            .HasMaxLength(450);

        entity.Property(x => x.Reason)
            .HasMaxLength(1000);

        entity.HasIndex(x => new { x.ClientUserId, x.AgentUserId })
            .IsUnique();

        entity.HasIndex(x => new { x.ClientUserId, x.IsActive });

        entity.HasIndex(x => new { x.AgentUserId, x.IsActive });
    }

    private static void ConfigureAuditEntry(
        EntityTypeBuilder<MessagingAuditEntry> entity)
    {
        entity.ToTable("MessagingAuditEntries");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.ActorUserId)
            .IsRequired()
            .HasMaxLength(450);

        entity.Property(x => x.Action)
            .IsRequired()
            .HasMaxLength(100);

        entity.Property(x => x.TargetUserId)
            .HasMaxLength(450);

        entity.Property(x => x.Detail)
            .HasMaxLength(1000);

        entity.HasIndex(x => x.CreatedUtc);

        entity.HasIndex(x => x.ActorUserId);

        entity.HasIndex(x => x.ConversationId);
    }

    private static void ConfigureRowVersion(
        PropertyBuilder<byte[]> property,
        string? providerName)
    {
        if (IsSqlServer(providerName))
        {
            property.IsRowVersion();
            return;
        }

        property
            .IsConcurrencyToken()
            .ValueGeneratedNever();
    }

    private static bool IsSqlServer(string? providerName) =>
        providerName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsSqlite(string? providerName) =>
        providerName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;
}
