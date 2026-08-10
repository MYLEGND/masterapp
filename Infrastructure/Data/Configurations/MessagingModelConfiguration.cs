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
        ConfigureVerificationReviewRequest(modelBuilder.Entity<VerificationReviewRequest>(), providerName);
        ConfigureControlledResourceGrant(modelBuilder.Entity<ControlledResourceGrant>(), providerName);
        ConfigureMessageTranslation(modelBuilder.Entity<MessageTranslation>());
        ConfigureMobileActivityNotification(modelBuilder.Entity<MobileActivityNotification>(), providerName);
        ConfigureUserGlobalBadge(modelBuilder.Entity<UserGlobalBadge>(), providerName);
        ConfigureMobilePushDevice(modelBuilder.Entity<MobilePushDevice>());
        ConfigureMobilePushDelivery(modelBuilder.Entity<MobilePushDelivery>());
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

        entity.Property(x => x.GroupImageContentType)
            .HasMaxLength(150);

        entity.Property(x => x.Purpose)
            .HasMaxLength(40);

        entity.Property(x => x.CreatedByUserId)
            .IsRequired()
            .HasMaxLength(450);

        entity.Property(x => x.OwnerParticipantType)
            .HasMaxLength(40);

        entity.Property(x => x.OwnerUserId)
            .HasMaxLength(450);

        entity.Property(x => x.HostUserId)
            .HasMaxLength(450);

        entity.Property(x => x.HostParticipantType)
            .HasMaxLength(40);

        entity.Property(x => x.MeetingLinkLabel)
            .HasMaxLength(100);

        entity.Property(x => x.MeetingLinkUrl)
            .HasMaxLength(2_048);

        entity.Property(x => x.MeetingFrequency)
            .HasMaxLength(24);

        entity.Property(x => x.MeetingWeekdays)
            .HasMaxLength(100);

        entity.Property(x => x.MeetingLocalTime)
            .HasMaxLength(5);

        entity.Property(x => x.MeetingTimeZoneId)
            .HasMaxLength(100);

        entity.Property(x => x.MeetingCustomDescription)
            .HasMaxLength(240);

        entity.HasIndex(x => new { x.IsPromoted, x.PromotionStartedUtc });

        entity.HasIndex(x => new { x.Purpose, x.CreatedByUserId, x.OwnerParticipantType });

        // There is exactly one staff-only verification review conversation for
        // the application. A filtered unique index makes that invariant hold
        // even when two members submit a request at the same moment.
        var verificationReviewIndex = entity
            .HasIndex(x => x.Purpose)
            .IsUnique();
        if (IsSqlServer(providerName))
        {
            verificationReviewIndex.HasFilter("[Purpose] = 'VerificationReview'");
        }
        else if (IsSqlite(providerName))
        {
            verificationReviewIndex.HasFilter("\"Purpose\" = 'VerificationReview'");
        }

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

        entity.HasIndex(x => new { x.UserId, x.ParticipantType, x.PinnedUtc });
        entity.HasIndex(x => new { x.UserId, x.ParticipantType, x.HiddenUtc });

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

        entity.Property(x => x.OriginalLanguage)
            .HasMaxLength(32);

        entity.Property(x => x.ClientMessageId)
            .HasMaxLength(100);

        ConfigureRowVersion(entity.Property(x => x.RowVersion), providerName);

        entity.HasIndex(x => new { x.ConversationId, x.SentUtc });

        entity.HasIndex(x => x.VerificationReviewRequestId);

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

    private static void ConfigureVerificationReviewRequest(
        EntityTypeBuilder<VerificationReviewRequest> entity,
        string? providerName)
    {
        entity.ToTable("VerificationReviewRequests");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.RequesterUserId).IsRequired().HasMaxLength(450);
        entity.Property(x => x.RequesterParticipantType).IsRequired().HasMaxLength(40);
        entity.Property(x => x.ResourceType)
            .IsRequired()
            .HasMaxLength(80)
            .HasDefaultValue(ControlledResourceTypes.VerificationBadge);
        entity.Property(x => x.Status).IsRequired().HasMaxLength(24);
        entity.Property(x => x.ResolvedByUserId).HasMaxLength(450);
        entity.Property(x => x.ResolutionNote).HasMaxLength(1_000);
        entity.HasIndex(x => new { x.RequesterUserId, x.RequesterParticipantType, x.ResourceType, x.Status });
        entity.HasIndex(x => new { x.ReviewConversationId, x.RequestedUtc });

        var pendingRequestIndex = entity
            .HasIndex(x => new { x.RequesterUserId, x.RequesterParticipantType, x.ResourceType })
            .IsUnique();
        if (IsSqlServer(providerName))
        {
            pendingRequestIndex.HasFilter("[Status] = 'Pending'");
        }
        else if (IsSqlite(providerName))
        {
            pendingRequestIndex.HasFilter("\"Status\" = 'Pending'");
        }
    }

    private static void ConfigureControlledResourceGrant(
        EntityTypeBuilder<ControlledResourceGrant> entity,
        string? providerName)
    {
        entity.ToTable("ControlledResourceGrants");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.UserId).IsRequired().HasMaxLength(450);
        entity.Property(x => x.ParticipantType).IsRequired().HasMaxLength(40);
        entity.Property(x => x.ResourceType).IsRequired().HasMaxLength(80);
        entity.Property(x => x.GrantedByUserId).IsRequired().HasMaxLength(450);
        entity.Property(x => x.RevokedByUserId).HasMaxLength(450);
        ConfigureRowVersion(entity.Property(x => x.RowVersion), providerName);
        entity.HasIndex(x => new { x.UserId, x.ParticipantType, x.ResourceType }).IsUnique();
        entity.HasIndex(x => new { x.ResourceType, x.IsActive });
    }

    private static void ConfigureMessageTranslation(EntityTypeBuilder<MessageTranslation> entity)
    {
        entity.ToTable("MessageTranslations");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.TargetLanguage).IsRequired().HasMaxLength(32);
        entity.Property(x => x.TranslatedText).IsRequired().HasMaxLength(10_000);
        entity.Property(x => x.Provider).IsRequired().HasMaxLength(80);
        entity.HasIndex(x => new { x.InternalMessageId, x.TargetLanguage }).IsUnique();
        entity.HasOne(x => x.InternalMessage)
            .WithMany(x => x.Translations)
            .HasForeignKey(x => x.InternalMessageId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureMobileActivityNotification(
        EntityTypeBuilder<MobileActivityNotification> entity,
        string? providerName)
    {
        entity.ToTable("MobileActivityNotifications");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.RecipientUserId).IsRequired().HasMaxLength(450);
        entity.Property(x => x.RecipientParticipantType).IsRequired().HasMaxLength(40);
        entity.Property(x => x.Kind).IsRequired().HasMaxLength(80);
        entity.Property(x => x.Title).IsRequired().HasMaxLength(240);
        entity.Property(x => x.Detail).IsRequired().HasMaxLength(1_000);
        entity.Property(x => x.ConversationId);
        entity.Property(x => x.SourceMessageId);
        ConfigureRowVersion(entity.Property(x => x.RowVersion), providerName);
        entity.HasIndex(x => new
        {
            x.RecipientUserId,
            x.RecipientParticipantType,
            x.OccurredUtc
        });
        var requestOutcomeIndex = entity.HasIndex(x => x.ControlledResourceRequestId).IsUnique();
        if (IsSqlServer(providerName))
        {
            requestOutcomeIndex.HasFilter("[ControlledResourceRequestId] IS NOT NULL");
        }
        else if (IsSqlite(providerName))
        {
            requestOutcomeIndex.HasFilter("\"ControlledResourceRequestId\" IS NOT NULL");
        }

        var messageRecipientIndex = entity.HasIndex(x => new
        {
            x.SourceMessageId,
            x.RecipientUserId,
            x.RecipientParticipantType
        }).IsUnique();
        if (IsSqlServer(providerName))
        {
            messageRecipientIndex.HasFilter("[SourceMessageId] IS NOT NULL");
        }
        else if (IsSqlite(providerName))
        {
            messageRecipientIndex.HasFilter("\"SourceMessageId\" IS NOT NULL");
        }

        entity.HasIndex(x => new
        {
            x.RecipientUserId,
            x.RecipientParticipantType,
            x.IsRead,
            x.IsCleared,
            x.OccurredUtc
        });
        entity.HasIndex(x => new
        {
            x.RecipientUserId,
            x.RecipientParticipantType,
            x.ConversationId,
            x.IsRead,
            x.IsCleared
        });
    }

    private static void ConfigureUserGlobalBadge(
        EntityTypeBuilder<UserGlobalBadge> entity,
        string? providerName)
    {
        entity.ToTable("UserGlobalBadges");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.UserId).IsRequired().HasMaxLength(450);
        entity.Property(x => x.ParticipantType).IsRequired().HasMaxLength(40);
        ConfigureRowVersion(entity.Property(x => x.RowVersion), providerName);
        entity.HasIndex(x => new { x.UserId, x.ParticipantType }).IsUnique();
    }

    private static void ConfigureMobilePushDevice(EntityTypeBuilder<MobilePushDevice> entity)
    {
        entity.ToTable("MobilePushDevices");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.UserId).IsRequired().HasMaxLength(450);
        entity.Property(x => x.ParticipantType).IsRequired().HasMaxLength(40);
        entity.Property(x => x.DeviceToken).IsRequired().HasMaxLength(4_096);
        entity.Property(x => x.TokenHash).IsRequired().HasMaxLength(128);
        entity.Property(x => x.Provider)
            .IsRequired()
            .HasMaxLength(16)
            // Existing rows are APNs registrations; this protects them during
            // the provider-scoped unique-index migration.
            .HasDefaultValue(MobilePushProviders.Apns);
        entity.Property(x => x.Environment).IsRequired().HasMaxLength(24);
        entity.HasIndex(x => new { x.Provider, x.TokenHash }).IsUnique();
        entity.HasIndex(x => new { x.UserId, x.ParticipantType, x.IsActive });
    }

    private static void ConfigureMobilePushDelivery(EntityTypeBuilder<MobilePushDelivery> entity)
    {
        entity.ToTable("MobilePushDeliveries");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.LastError).HasMaxLength(1_000);
        entity.HasIndex(x => new { x.NotificationId, x.MobilePushDeviceId }).IsUnique();
        entity.HasIndex(x => new { x.SentUtc, x.AbandonedUtc, x.NextAttemptUtc });
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
