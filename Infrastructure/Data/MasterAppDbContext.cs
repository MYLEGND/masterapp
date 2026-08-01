using System;
using Domain.Billing;
using Domain.Entities;
using Domain.Entities.FinancialIntelligence;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Infrastructure.Data.Configurations;

namespace Infrastructure.Data;

public class MasterAppDbContext : DbContext
{
    public MasterAppDbContext(DbContextOptions<MasterAppDbContext> options) : base(options) { }

    public DbSet<ClientProfile> ClientProfiles => Set<ClientProfile>();
    public DbSet<AgentClient> AgentClients => Set<AgentClient>();
    public DbSet<AgentAssistant> AgentAssistants => Set<AgentAssistant>();
    public DbSet<HouseholdMember> HouseholdMembers => Set<HouseholdMember>();
    public DbSet<HouseholdAccount> HouseholdAccounts => Set<HouseholdAccount>();
    public DbSet<HouseholdMembership> HouseholdMemberships => Set<HouseholdMembership>();
    public DbSet<HouseholdMemberInvitation> HouseholdMemberInvitations => Set<HouseholdMemberInvitation>();
    public DbSet<FinanceToolState> FinanceToolStates => Set<FinanceToolState>();
    public DbSet<FinancialDataConnection> FinancialDataConnections => Set<FinancialDataConnection>();
    public DbSet<ImportedFinancialAccount> ImportedFinancialAccounts => Set<ImportedFinancialAccount>();
    public DbSet<ImportedFinancialTransaction> ImportedFinancialTransactions => Set<ImportedFinancialTransaction>();
    public DbSet<RecurringFinancialStream> RecurringFinancialStreams => Set<RecurringFinancialStream>();
    public DbSet<ExpenseLensStreamLink> ExpenseLensStreamLinks => Set<ExpenseLensStreamLink>();
    public DbSet<ClientFinancialIntelligenceProfile> ClientFinancialIntelligenceProfiles => Set<ClientFinancialIntelligenceProfile>();
    public DbSet<FinancialObservation> FinancialObservations => Set<FinancialObservation>();
    public DbSet<FinancialFinding> FinancialFindings => Set<FinancialFinding>();
    public DbSet<FinancialFindingObservation> FinancialFindingObservations => Set<FinancialFindingObservation>();
    public DbSet<FinancialFindingFeedback> FinancialFindingFeedback => Set<FinancialFindingFeedback>();
    public DbSet<AgentFinanceToolState> AgentFinanceToolStates => Set<AgentFinanceToolState>();
    public DbSet<BookkeepingEntry> BookkeepingEntries => Set<BookkeepingEntry>();
    public DbSet<RecurringExpense> RecurringExpenses => Set<RecurringExpense>();
    public DbSet<WorkstationLeadProfile> WorkstationLeadProfiles => Set<WorkstationLeadProfile>();
    public DbSet<Proposal> Proposals => Set<Proposal>();
    public DbSet<UnderwritingRecord> UnderwritingRecords => Set<UnderwritingRecord>();
    public DbSet<OnboardingInvite> OnboardingInvites => Set<OnboardingInvite>();
    public DbSet<OnboardingSubmission> OnboardingSubmissions => Set<OnboardingSubmission>();
    public DbSet<AgentProfile> AgentProfiles => Set<AgentProfile>();
    public DbSet<MobileProfileSettings> MobileProfileSettings => Set<MobileProfileSettings>();
    public DbSet<ProductionRecord> ProductionRecords => Set<ProductionRecord>();
    public DbSet<WebsiteLead> WebsiteLeads => Set<WebsiteLead>();
    public DbSet<WebsiteLeadIntakeLink> WebsiteLeadIntakeLinks => Set<WebsiteLeadIntakeLink>();
    public DbSet<LeadAppointment> LeadAppointments => Set<LeadAppointment>();
    public DbSet<GraphCalendarSubscription> GraphCalendarSubscriptions => Set<GraphCalendarSubscription>();
    public DbSet<AppointmentSyncLog> AppointmentSyncLogs => Set<AppointmentSyncLog>();
    public DbSet<AnalyticsEvent> AnalyticsEvents => Set<AnalyticsEvent>();
    public DbSet<AnalyticsDriftAlert> AnalyticsDriftAlerts => Set<AnalyticsDriftAlert>();
    public DbSet<MetaSignalEvent> MetaSignalEvents => Set<MetaSignalEvent>();
    public DbSet<AgentTrackingProfile> AgentTrackingProfiles => Set<AgentTrackingProfile>();
    public DbSet<AgentTrackingAlias> AgentTrackingAliases => Set<AgentTrackingAlias>();
    public DbSet<ActionItem> ActionItems => Set<ActionItem>();
    public DbSet<ActionLog> ActionLogs => Set<ActionLog>();
    public DbSet<Blocker> Blockers => Set<Blocker>();
    public DbSet<DecisionRecord> DecisionRecords => Set<DecisionRecord>();
    public DbSet<PlaybookExecution> PlaybookExecutions => Set<PlaybookExecution>();
    public DbSet<Commitment> Commitments => Set<Commitment>();
    public DbSet<ClientFinancialPlan> ClientFinancialPlans => Set<ClientFinancialPlan>();
    public DbSet<AgentZoomLink> AgentZoomLinks => Set<AgentZoomLink>();
    public DbSet<CommerceBusiness> CommerceBusinesses => Set<CommerceBusiness>();
    public DbSet<CommerceBusinessSettings> CommerceBusinessSettings => Set<CommerceBusinessSettings>();
    public DbSet<CommerceBusinessMember> CommerceBusinessMembers => Set<CommerceBusinessMember>();
    public DbSet<CommerceBusinessSubscription> CommerceBusinessSubscriptions => Set<CommerceBusinessSubscription>();
    public DbSet<CommerceBusinessStorefrontSettings> CommerceBusinessStorefrontSettings => Set<CommerceBusinessStorefrontSettings>();
    public DbSet<CommerceProduct> CommerceProducts => Set<CommerceProduct>();
    public DbSet<CommerceProductImage> CommerceProductImages => Set<CommerceProductImage>();
    public DbSet<CommerceProductInventoryItem> CommerceProductInventoryItems => Set<CommerceProductInventoryItem>();
    public DbSet<CommerceProductDiscount> CommerceProductDiscounts => Set<CommerceProductDiscount>();
    public DbSet<CommerceOrder> CommerceOrders => Set<CommerceOrder>();
    public DbSet<CommerceOrderLine> CommerceOrderLines => Set<CommerceOrderLine>();
    public DbSet<ClientSubscriptionOffer> ClientSubscriptionOffers => Set<ClientSubscriptionOffer>();
    public DbSet<ClientSubscription> ClientSubscriptions => Set<ClientSubscription>();
    public DbSet<ClientPaymentMethod> ClientPaymentMethods => Set<ClientPaymentMethod>();
    public DbSet<ClientBillingNotification> ClientBillingNotifications => Set<ClientBillingNotification>();
    public DbSet<SubscriptionActivationInvitation> SubscriptionActivationInvitations => Set<SubscriptionActivationInvitation>();
    public DbSet<ClientIdentityContinuation> ClientIdentityContinuations => Set<ClientIdentityContinuation>();
    public DbSet<SubscriptionPayment> SubscriptionPayments => Set<SubscriptionPayment>();
    public DbSet<BillingProviderEvent> BillingProviderEvents => Set<BillingProviderEvent>();
    public DbSet<ClientEntitlement> ClientEntitlements => Set<ClientEntitlement>();
    public DbSet<BillingAuditEntry> BillingAuditEntries => Set<BillingAuditEntry>();

    public DbSet<MessageConversation> MessageConversations => Set<MessageConversation>();
    public DbSet<MessageConversationParticipant> MessageConversationParticipants => Set<MessageConversationParticipant>();
    public DbSet<InternalMessage> InternalMessages => Set<InternalMessage>();
    public DbSet<MessageAttachment> MessageAttachments => Set<MessageAttachment>();
    public DbSet<ClientAgentMessagingGrant> ClientAgentMessagingGrants => Set<ClientAgentMessagingGrant>();
    public DbSet<MessagingAuditEntry> MessagingAuditEntries => Set<MessagingAuditEntry>();
    public DbSet<VerificationReviewRequest> VerificationReviewRequests => Set<VerificationReviewRequest>();
    public DbSet<ControlledResourceGrant> ControlledResourceGrants => Set<ControlledResourceGrant>();
    public DbSet<MessageTranslation> MessageTranslations => Set<MessageTranslation>();
    public DbSet<JourneyCircleProfile> JourneyCircleProfiles => Set<JourneyCircleProfile>();
    public DbSet<JourneyCircleConnection> JourneyCircleConnections => Set<JourneyCircleConnection>();
    public DbSet<JourneyCircleBlock> JourneyCircleBlocks => Set<JourneyCircleBlock>();
    public DbSet<JourneyCircleReport> JourneyCircleReports => Set<JourneyCircleReport>();
    public DbSet<JourneyCircleModerationEvent> JourneyCircleModerationEvents => Set<JourneyCircleModerationEvent>();
    public DbSet<SocialPost> SocialPosts => Set<SocialPost>();
    public DbSet<SocialPostMediaAsset> SocialPostMediaAssets => Set<SocialPostMediaAsset>();
    public DbSet<SocialPostComment> SocialPostComments => Set<SocialPostComment>();
    public DbSet<SocialPostReaction> SocialPostReactions => Set<SocialPostReaction>();
    public DbSet<SocialFollow> SocialFollows => Set<SocialFollow>();
    public DbSet<SocialPostViewer> SocialPostViews => Set<SocialPostViewer>();
    public DbSet<SocialPostSave> SocialPostSaves => Set<SocialPostSave>();
    public DbSet<SocialPostShare> SocialPostShares => Set<SocialPostShare>();
    public DbSet<SocialPostRepost> SocialPostReposts => Set<SocialPostRepost>();
    public DbSet<SocialProfileVisit> SocialProfileVisits => Set<SocialProfileVisit>();
    public DbSet<SocialPostMusicAttachment> SocialPostMusicAttachments => Set<SocialPostMusicAttachment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        MessagingModelConfiguration.Configure(modelBuilder, Database.ProviderName);
        JourneyCirclesModelConfiguration.Configure(modelBuilder, Database.ProviderName);
        SocialFeedModelConfiguration.Configure(modelBuilder, Database.ProviderName);
        var isSqlServer = Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true;

        modelBuilder.Entity<CommerceBusiness>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Key).IsRequired().HasMaxLength(80);
            e.Property(x => x.DisplayName).IsRequired().HasMaxLength(160);
            e.Property(x => x.LegalName).IsRequired().HasMaxLength(200);
            e.Property(x => x.BusinessType).IsRequired().HasMaxLength(120);
            e.Property(x => x.PrimaryDomain).HasMaxLength(255);
            e.Property(x => x.Status).IsRequired().HasMaxLength(40);
            e.Property(x => x.OwnerEmail).IsRequired().HasMaxLength(320);
            e.HasIndex(x => x.Key).IsUnique();
            e.HasIndex(x => x.IsActive);
        });

        modelBuilder.Entity<CommerceBusinessSettings>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.GlobalDiscountCode).HasMaxLength(80);
            e.Property(x => x.GlobalDiscountType).IsRequired().HasMaxLength(40);
            e.Property(x => x.TaxPercent).HasPrecision(9, 4);
            e.Property(x => x.GlobalDiscountAmount).HasPrecision(9, 4);
            e.HasIndex(x => x.CommerceBusinessId).IsUnique();
            e.HasOne(x => x.CommerceBusiness)
                .WithMany()
                .HasForeignKey(x => x.CommerceBusinessId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CommerceBusinessMember>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).IsRequired().HasMaxLength(320);
            e.Property(x => x.NormalizedEmail).IsRequired().HasMaxLength(320);
            e.Property(x => x.DisplayName).IsRequired().HasMaxLength(160);
            e.Property(x => x.RoleKey).IsRequired().HasMaxLength(80);
            e.Property(x => x.Status).IsRequired().HasMaxLength(40);
            e.HasIndex(x => new { x.CommerceBusinessId, x.NormalizedEmail }).IsUnique();
            e.HasIndex(x => x.NormalizedEmail);
            e.HasOne(x => x.CommerceBusiness)
                .WithMany()
                .HasForeignKey(x => x.CommerceBusinessId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CommerceBusinessSubscription>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.PlanKey).IsRequired().HasMaxLength(80);
            e.Property(x => x.PlanName).IsRequired().HasMaxLength(120);
            e.Property(x => x.Status).IsRequired().HasMaxLength(40);
            e.Property(x => x.BillingProvider).IsRequired().HasMaxLength(80);
            e.Property(x => x.BillingCustomerId).HasMaxLength(160);
            e.Property(x => x.BillingSubscriptionId).HasMaxLength(160);
            e.HasIndex(x => x.CommerceBusinessId).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasOne(x => x.CommerceBusiness)
                .WithMany()
                .HasForeignKey(x => x.CommerceBusinessId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CommerceBusinessStorefrontSettings>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.BrandHeadline).IsRequired().HasMaxLength(180);
            e.Property(x => x.BrandSubheadline).IsRequired().HasMaxLength(300);
            e.Property(x => x.AccentColor).IsRequired().HasMaxLength(40);
            e.Property(x => x.LogoUrl).IsRequired().HasMaxLength(2048);
            e.Property(x => x.StorefrontStatus).IsRequired().HasMaxLength(40);
            e.HasIndex(x => x.CommerceBusinessId).IsUnique();
            e.HasOne(x => x.CommerceBusiness)
                .WithMany()
                .HasForeignKey(x => x.CommerceBusinessId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CommerceProduct>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ExternalProductKey).IsRequired().HasMaxLength(120);
            e.Property(x => x.Name).IsRequired().HasMaxLength(180);
            e.Property(x => x.Slug).IsRequired().HasMaxLength(180);
            e.Property(x => x.Description).IsRequired();
            e.Property(x => x.PriceLabel).IsRequired().HasMaxLength(80);
            e.Property(x => x.Badge).IsRequired().HasMaxLength(80);
            e.HasIndex(x => new { x.CommerceBusinessId, x.ExternalProductKey }).IsUnique();
            e.HasIndex(x => new { x.CommerceBusinessId, x.Slug }).IsUnique();
            e.HasIndex(x => new { x.CommerceBusinessId, x.IsActive, x.DisplayOrder });
            e.HasOne(x => x.CommerceBusiness)
                .WithMany()
                .HasForeignKey(x => x.CommerceBusinessId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CommerceProductImage>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ExternalImageKey).IsRequired().HasMaxLength(120);
            e.Property(x => x.ImageUrl).IsRequired().HasMaxLength(2048);
            e.Property(x => x.FileName).IsRequired().HasMaxLength(255);
            e.Property(x => x.AltText).IsRequired().HasMaxLength(300);
            e.Property(x => x.ObjectFit).IsRequired().HasMaxLength(40);
            e.Property(x => x.Zoom).HasPrecision(9, 4);
            e.HasIndex(x => new { x.CommerceProductId, x.ExternalImageKey }).IsUnique();
            e.HasIndex(x => new { x.CommerceProductId, x.DisplayOrder });
            e.HasOne(x => x.CommerceProduct)
                .WithMany(x => x.Images)
                .HasForeignKey(x => x.CommerceProductId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CommerceProductInventoryItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ExternalInventoryKey).IsRequired().HasMaxLength(120);
            e.Property(x => x.Size).IsRequired().HasMaxLength(40);
            e.HasIndex(x => new { x.CommerceProductId, x.Size }).IsUnique();
            e.HasOne(x => x.CommerceProduct)
                .WithMany(x => x.InventoryItems)
                .HasForeignKey(x => x.CommerceProductId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CommerceProductDiscount>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ExternalDiscountKey).IsRequired().HasMaxLength(120);
            e.Property(x => x.Code).IsRequired().HasMaxLength(80);
            e.Property(x => x.DiscountType).IsRequired().HasMaxLength(40);
            e.Property(x => x.Amount).HasPrecision(9, 4);
            e.HasIndex(x => new { x.CommerceProductId, x.ExternalDiscountKey }).IsUnique();
            e.HasIndex(x => new { x.CommerceProductId, x.Code });
            e.HasOne(x => x.CommerceProduct)
                .WithMany(x => x.Discounts)
                .HasForeignKey(x => x.CommerceProductId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CommerceOrder>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.OrderNumber).IsRequired().HasMaxLength(80);
            e.Property(x => x.Status).IsRequired().HasMaxLength(40);
            e.Property(x => x.PaymentStatus).IsRequired().HasMaxLength(40);
            e.Property(x => x.FulfillmentStatus).IsRequired().HasMaxLength(40);
            e.Property(x => x.ReturnStatus).IsRequired().HasMaxLength(40);
            e.Property(x => x.CheckoutAttemptId).HasMaxLength(120);
            e.Property(x => x.SquarePaymentId).HasMaxLength(160);
            e.Property(x => x.SquareError).HasMaxLength(1000);
            e.Property(x => x.TrackingCarrier).HasMaxLength(120);
            e.Property(x => x.TrackingNumber).HasMaxLength(160);
            e.Property(x => x.AdminNotes).HasMaxLength(2000);
            e.Property(x => x.FirstName).IsRequired().HasMaxLength(120);
            e.Property(x => x.LastName).IsRequired().HasMaxLength(120);
            e.Property(x => x.Email).IsRequired().HasMaxLength(320);
            e.Property(x => x.Phone).IsRequired().HasMaxLength(80);
            e.Property(x => x.AddressLine1).IsRequired().HasMaxLength(240);
            e.Property(x => x.AddressLine2).HasMaxLength(240);
            e.Property(x => x.City).IsRequired().HasMaxLength(120);
            e.Property(x => x.State).IsRequired().HasMaxLength(80);
            e.Property(x => x.PostalCode).IsRequired().HasMaxLength(40);
            e.Property(x => x.Source).IsRequired().HasMaxLength(120);
            e.Property(x => x.UserAgent).HasMaxLength(1000);
            e.Property(x => x.RequestIp).HasMaxLength(80);
            e.Property(x => x.DiscountCode).HasMaxLength(80);
            e.Property(x => x.DiscountLabel).HasMaxLength(160);
            e.HasIndex(x => new { x.CommerceBusinessId, x.OrderNumber }).IsUnique();
            e.HasIndex(x => new { x.CommerceBusinessId, x.CreatedUtc });
            e.HasIndex(x => new { x.CommerceBusinessId, x.PaymentStatus, x.FulfillmentStatus });
            e.HasIndex(x => x.CheckoutAttemptId);
            e.HasOne(x => x.CommerceBusiness)
                .WithMany()
                .HasForeignKey(x => x.CommerceBusinessId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CommerceOrderLine>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ProductExternalKey).IsRequired().HasMaxLength(120);
            e.Property(x => x.ProductName).IsRequired().HasMaxLength(180);
            e.Property(x => x.ProductSlug).IsRequired().HasMaxLength(180);
            e.Property(x => x.Size).IsRequired().HasMaxLength(40);
            e.Property(x => x.ImageUrl).HasMaxLength(2048);
            e.HasIndex(x => x.CommerceOrderId);
            e.HasOne(x => x.CommerceOrder)
                .WithMany(x => x.Lines)
                .HasForeignKey(x => x.CommerceOrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ClientSubscriptionOffer>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("ClientSubscriptionOffers");
            e.Property(x => x.OwnerAgentUserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.PriceType).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(8).IsRequired();
            e.Property(x => x.BillingAnchorSelectionMode).HasConversion<string>().HasMaxLength(40).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();

            e.HasIndex(x => x.ClientProfileId);
            e.HasIndex(x => new { x.ClientProfileId, x.Status });
            e.HasIndex(x => x.OwnerAgentUserId);

            e.HasOne(x => x.ClientProfile)
                .WithMany()
                .HasForeignKey(x => x.ClientProfileId)
                .OnDelete(DeleteBehavior.Cascade);

            if (isSqlServer)
                e.Property(x => x.RowVersion).IsRowVersion();
            else
                e.Property(x => x.RowVersion)
                    .IsRequired()
                    .IsConcurrencyToken()
                    .HasDefaultValueSql("X''")
                    .ValueGeneratedNever();
        });

        modelBuilder.Entity<ClientSubscription>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("ClientSubscriptions");
            e.Property(x => x.OwnerAgentUserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.Provider).HasConversion<string>().HasMaxLength(40).IsRequired();
            e.Property(x => x.ProviderEnvironment).HasConversion<string>().HasMaxLength(40).IsRequired();
            e.Property(x => x.ProviderCustomerId).HasMaxLength(160);
            e.Property(x => x.ProviderSubscriptionId).HasMaxLength(160);
            e.Property(x => x.ProviderPlanVariationId).HasMaxLength(160);
            e.Property(x => x.Currency).HasMaxLength(8).IsRequired();
            e.Property(x => x.BillingTimeZoneId).HasMaxLength(120).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(40).IsRequired();
            e.Property(x => x.PaymentStanding).HasConversion<string>().HasMaxLength(40).IsRequired();

            if (isSqlServer)
                e.HasIndex(x => x.ClientProfileId).IsUnique().HasFilter("[Status] <> 'Canceled' AND [Status] <> 'ActivationFailed'");
            else
                e.HasIndex(x => new { x.ClientProfileId, x.Status });

            if (isSqlServer)
                e.HasIndex(x => new { x.Provider, x.ProviderEnvironment, x.ProviderSubscriptionId }).IsUnique().HasFilter("[ProviderSubscriptionId] IS NOT NULL");
            else
                e.HasIndex(x => new { x.Provider, x.ProviderEnvironment, x.ProviderSubscriptionId }).IsUnique();

            e.HasIndex(x => new { x.Provider, x.ProviderEnvironment, x.ProviderCustomerId });
            e.HasIndex(x => new { x.ClientProfileId, x.UpdatedUtc });
            e.HasIndex(x => new { x.Status, x.NextBillingDateUtc });
            e.HasIndex(x => new { x.IsPlatformManaged, x.Status, x.NextBillingDateUtc });

            e.HasOne(x => x.ClientProfile)
                .WithMany()
                .HasForeignKey(x => x.ClientProfileId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.AcceptedOffer)
                .WithMany()
                .HasForeignKey(x => x.AcceptedOfferId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.DefaultPaymentMethod)
                .WithMany()
                .HasForeignKey(x => x.DefaultPaymentMethodId)
                .OnDelete(isSqlServer ? DeleteBehavior.NoAction : DeleteBehavior.SetNull);

            if (isSqlServer)
                e.Property(x => x.RowVersion).IsRowVersion();
            else
                e.Property(x => x.RowVersion)
                    .IsRequired()
                    .IsConcurrencyToken()
                    .HasDefaultValueSql("X''")
                    .ValueGeneratedNever();
        });

        modelBuilder.Entity<ClientPaymentMethod>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("ClientPaymentMethods");
            e.Property(x => x.Provider).HasConversion<string>().HasMaxLength(40).IsRequired();
            e.Property(x => x.ProviderEnvironment).HasConversion<string>().HasMaxLength(40).IsRequired();
            e.Property(x => x.ProviderPaymentMethodId).HasMaxLength(160).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(80);
            e.Property(x => x.CardBrand).HasMaxLength(40);
            e.Property(x => x.Last4).HasMaxLength(4);
            e.Property(x => x.CardholderName).HasMaxLength(200);
            e.Property(x => x.BillingAddressLine1).HasMaxLength(200);
            e.Property(x => x.BillingAddressLine2).HasMaxLength(200);
            e.Property(x => x.BillingCity).HasMaxLength(120);
            e.Property(x => x.BillingState).HasMaxLength(120);
            e.Property(x => x.BillingPostalCode).HasMaxLength(32);
            e.Property(x => x.BillingCountryCode).HasMaxLength(8);
            e.HasIndex(x => new { x.ClientProfileId, x.RetiredUtc });
            e.HasIndex(x => new { x.Provider, x.ProviderEnvironment, x.ProviderPaymentMethodId }).IsUnique();

            e.HasOne(x => x.ClientProfile)
                .WithMany()
                .HasForeignKey(x => x.ClientProfileId)
                .OnDelete(DeleteBehavior.Cascade);

            if (isSqlServer)
                e.Property(x => x.RowVersion).IsRowVersion();
            else
                e.Property(x => x.RowVersion)
                    .IsRequired()
                    .IsConcurrencyToken()
                    .HasDefaultValueSql("X''")
                    .ValueGeneratedNever();
        });

        modelBuilder.Entity<ClientBillingNotification>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("ClientBillingNotifications");
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(48).IsRequired();
            e.Property(x => x.EventKey).HasMaxLength(220).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(240).IsRequired();
            e.Property(x => x.PlainTextBody).HasMaxLength(4000).IsRequired();
            e.Property(x => x.SafeFailureCode).HasMaxLength(120);
            e.HasIndex(x => x.EventKey).IsUnique();
            e.HasIndex(x => new { x.SentUtc, x.NotBeforeUtc, x.NextAttemptUtc });
            e.HasIndex(x => new { x.ClientSubscriptionId, x.Kind });

            e.HasOne(x => x.ClientProfile)
                .WithMany()
                .HasForeignKey(x => x.ClientProfileId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ClientSubscription)
                .WithMany()
                .HasForeignKey(x => x.ClientSubscriptionId)
                .OnDelete(isSqlServer ? DeleteBehavior.NoAction : DeleteBehavior.Cascade);

            if (isSqlServer)
                e.Property(x => x.RowVersion).IsRowVersion();
            else
                e.Property(x => x.RowVersion)
                    .IsRequired()
                    .IsConcurrencyToken()
                    .HasDefaultValueSql("X''")
                    .ValueGeneratedNever();
        });

        modelBuilder.Entity<SubscriptionActivationInvitation>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("SubscriptionActivationInvitations");
            e.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.IntendedNormalizedEmail).HasMaxLength(320).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.CreatedByAgentUserId).HasMaxLength(450).IsRequired();

            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.ClientProfileId, x.Status });
            e.HasIndex(x => x.ClientSubscriptionOfferId);

            e.HasOne(x => x.ClientProfile)
                .WithMany()
                .HasForeignKey(x => x.ClientProfileId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.ClientSubscriptionOffer)
                .WithMany()
                .HasForeignKey(x => x.ClientSubscriptionOfferId)
                .OnDelete(isSqlServer ? DeleteBehavior.NoAction : DeleteBehavior.Cascade);

            if (isSqlServer)
                e.Property(x => x.RowVersion).IsRowVersion();
            else
                e.Property(x => x.RowVersion)
                    .IsRequired()
                    .IsConcurrencyToken()
                    .HasDefaultValueSql("X''")
                    .ValueGeneratedNever();
        });

        modelBuilder.Entity<ClientIdentityContinuation>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("ClientIdentityContinuations");
            e.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.IntendedNormalizedEmail).HasMaxLength(320).IsRequired();
            e.Property(x => x.ReturnUrl).HasMaxLength(2048).IsRequired();

            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.ClientProfileId, x.ExpiresUtc });
            e.HasIndex(x => new { x.ClientProfileId, x.ConsumedUtc });

            e.HasOne(x => x.ClientProfile)
                .WithMany()
                .HasForeignKey(x => x.ClientProfileId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.SubscriptionActivationInvitation)
                .WithMany()
                .HasForeignKey(x => x.SubscriptionActivationInvitationId)
                .OnDelete(isSqlServer ? DeleteBehavior.NoAction : DeleteBehavior.SetNull);

            e.HasOne(x => x.ClientSubscription)
                .WithMany()
                .HasForeignKey(x => x.ClientSubscriptionId)
                .OnDelete(isSqlServer ? DeleteBehavior.NoAction : DeleteBehavior.SetNull);

            if (isSqlServer)
                e.Property(x => x.RowVersion).IsRowVersion();
            else
                e.Property(x => x.RowVersion)
                    .IsRequired()
                    .IsConcurrencyToken()
                    .HasDefaultValueSql("X''")
                    .ValueGeneratedNever();
        });

        modelBuilder.Entity<SubscriptionPayment>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("SubscriptionPayments");
            e.Property(x => x.Provider).HasConversion<string>().HasMaxLength(40).IsRequired();
            e.Property(x => x.ProviderEnvironment).HasConversion<string>().HasMaxLength(40).IsRequired();
            e.Property(x => x.ProviderPaymentId).HasMaxLength(160);
            e.Property(x => x.ProviderInvoiceId).HasMaxLength(160);
            e.Property(x => x.ProviderRefundId).HasMaxLength(160);
            e.Property(x => x.Currency).HasMaxLength(8).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(40).IsRequired();
            e.Property(x => x.IdempotencyKey).HasMaxLength(128);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(40).IsRequired();
            e.Property(x => x.SafeFailureCode).HasMaxLength(120);
            e.Property(x => x.ClaimToken).HasMaxLength(128);
            e.Property(x => x.ProviderRequestId).HasMaxLength(160);

            e.HasIndex(x => x.ClientSubscriptionId);
            e.HasIndex(x => x.ClientPaymentMethodId);
            e.HasIndex(x => x.CommerceOrderId);

            if (isSqlServer)
                e.HasIndex(x => new { x.Provider, x.ProviderEnvironment, x.ProviderPaymentId }).IsUnique().HasFilter("[ProviderPaymentId] IS NOT NULL");
            else
                e.HasIndex(x => new { x.Provider, x.ProviderEnvironment, x.ProviderPaymentId }).IsUnique();

            if (isSqlServer)
                e.HasIndex(x => new { x.Provider, x.ProviderEnvironment, x.ProviderRefundId }).IsUnique().HasFilter("[ProviderRefundId] IS NOT NULL");
            else
                e.HasIndex(x => new { x.Provider, x.ProviderEnvironment, x.ProviderRefundId }).IsUnique();

            if (isSqlServer)
            {
                e.HasIndex(x => new { x.Provider, x.ProviderEnvironment, x.IdempotencyKey })
                    .IsUnique()
                    .HasFilter("[IdempotencyKey] IS NOT NULL");
                e.HasIndex(x => new { x.ClientSubscriptionId, x.BillingPeriodStartUtc, x.AttemptNumber })
                    .IsUnique()
                    .HasFilter("[ClientSubscriptionId] IS NOT NULL AND [BillingPeriodStartUtc] IS NOT NULL");
            }
            else
            {
                e.HasIndex(x => new { x.Provider, x.ProviderEnvironment, x.IdempotencyKey }).IsUnique();
                e.HasIndex(x => new { x.ClientSubscriptionId, x.BillingPeriodStartUtc, x.AttemptNumber }).IsUnique();
            }

            e.HasIndex(x => new { x.Status, x.RetryNotBeforeUtc, x.ScheduledChargeUtc });

            e.HasOne(x => x.ClientSubscription)
                .WithMany()
                .HasForeignKey(x => x.ClientSubscriptionId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(x => x.ClientPaymentMethod)
                .WithMany()
                .HasForeignKey(x => x.ClientPaymentMethodId)
                .OnDelete(isSqlServer ? DeleteBehavior.NoAction : DeleteBehavior.SetNull);

            e.HasOne(x => x.CommerceOrder)
                .WithMany()
                .HasForeignKey(x => x.CommerceOrderId)
                .OnDelete(DeleteBehavior.SetNull);

            if (isSqlServer)
                e.Property(x => x.RowVersion).IsRowVersion();
            else
                e.Property(x => x.RowVersion)
                    .IsRequired()
                    .IsConcurrencyToken()
                    .HasDefaultValueSql("X''")
                    .ValueGeneratedNever();
        });

        modelBuilder.Entity<BillingProviderEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("BillingProviderEvents");
            e.Property(x => x.Provider).HasConversion<string>().HasMaxLength(40).IsRequired();
            e.Property(x => x.ProviderEnvironment).HasConversion<string>().HasMaxLength(40).IsRequired();
            e.Property(x => x.ProviderEventId).HasMaxLength(160).IsRequired();
            e.Property(x => x.EventType).HasMaxLength(120).IsRequired();
            e.Property(x => x.ProviderObjectId).HasMaxLength(160);
            e.Property(x => x.ProcessingStatus).HasConversion<string>().HasMaxLength(40).IsRequired();
            e.Property(x => x.SafeErrorCode).HasMaxLength(120);
            e.Property(x => x.PayloadHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.RetainedPayloadJson).HasColumnType("text");

            e.HasIndex(x => new { x.Provider, x.ProviderEnvironment, x.ProviderEventId }).IsUnique();
            e.HasIndex(x => new { x.ProcessingStatus, x.RetryUtc });
            e.HasIndex(x => x.ProviderObjectId);

            if (isSqlServer)
                e.Property(x => x.RowVersion).IsRowVersion();
            else
                e.Property(x => x.RowVersion)
                    .IsRequired()
                    .IsConcurrencyToken()
                    .HasDefaultValueSql("X''")
                    .ValueGeneratedNever();
        });

        modelBuilder.Entity<ClientEntitlement>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("ClientEntitlements");
            e.Property(x => x.EntitlementKey).HasMaxLength(120).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.SourceType).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.SourceId).HasMaxLength(160).IsRequired();
            e.Property(x => x.ReasonCode).HasMaxLength(120);

            e.HasIndex(x => new { x.ClientProfileId, x.EntitlementKey }).IsUnique();
            e.HasIndex(x => x.Status);

            e.HasOne(x => x.ClientProfile)
                .WithMany()
                .HasForeignKey(x => x.ClientProfileId)
                .OnDelete(DeleteBehavior.Cascade);

            if (isSqlServer)
                e.Property(x => x.RowVersion).IsRowVersion();
            else
                e.Property(x => x.RowVersion)
                    .IsRequired()
                    .IsConcurrencyToken()
                    .HasDefaultValueSql("X''")
                    .ValueGeneratedNever();
        });

        modelBuilder.Entity<BillingAuditEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("BillingAuditEntries");
            e.Property(x => x.EntityType).HasMaxLength(120).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(128).IsRequired();
            e.Property(x => x.Action).HasMaxLength(80).IsRequired();
            e.Property(x => x.PreviousStatus).HasMaxLength(40);
            e.Property(x => x.NewStatus).HasMaxLength(40);
            e.Property(x => x.ActorType).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.ActorId).HasMaxLength(450);
            e.Property(x => x.Source).HasMaxLength(80).IsRequired();
            e.Property(x => x.ReasonCode).HasMaxLength(120);
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.Property(x => x.SanitizedMetadataJson).HasColumnType("text");

            e.HasIndex(x => new { x.EntityType, x.EntityId, x.OccurredUtc });
            e.HasIndex(x => new { x.ActorId, x.OccurredUtc });
        });

        modelBuilder.Entity<OnboardingInvite>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).IsRequired().HasMaxLength(128);
            e.Property(x => x.FirstName).IsRequired().HasMaxLength(120);
            e.Property(x => x.LastName).IsRequired().HasMaxLength(120);
            e.Property(x => x.Email).IsRequired().HasMaxLength(320);
            e.Property(x => x.NormalizedEmail).HasMaxLength(320);
            e.Property(x => x.RoleType).HasMaxLength(120).IsRequired();
            e.Property(x => x.Status).IsRequired().HasMaxLength(40);
            e.Property(x => x.CreatedBy).HasMaxLength(320);

            if (isSqlServer)
                e.HasIndex(x => x.NormalizedEmail).IsUnique().HasFilter("[NormalizedEmail] IS NOT NULL");
            else
                e.HasIndex(x => x.NormalizedEmail).IsUnique();

            e.HasIndex(x => x.TokenHash).IsUnique();
        });

        modelBuilder.Entity<AgentProfile>(e =>
        {
            e.Property(x => x.AgentUserId).HasMaxLength(450);
            e.Property(x => x.AgentUpn).HasMaxLength(450);
            e.Property(x => x.NormalizedEmail).HasMaxLength(320);
            e.Property(x => x.ProfileImageContentType).HasMaxLength(127);
            if (isSqlServer)
                e.Property(x => x.ProfileImageContent).HasColumnType("varbinary(max)");
            e.Property(x => x.ShortBio).HasMaxLength(280);
            e.Property(x => x.MetaPixelId).HasMaxLength(64);
            e.Property(x => x.MetaCapiAccessToken).HasColumnName("MetaAccessToken").HasMaxLength(2048);
            e.Property(x => x.MetaTestEventCode).HasMaxLength(128);
            e.Property(x => x.MicrosoftBookingsEmbedUrl).HasMaxLength(2048);
            e.Property(x => x.FallbackBookingUrl).HasMaxLength(2048);
            e.Property(x => x.BookingPageIdOrMailbox).HasMaxLength(320);
            e.Property(x => x.CalendarUserId).HasMaxLength(450);
            e.Property(x => x.CalendarEmail).HasMaxLength(320);
            e.Property(x => x.DeactivationReason).HasMaxLength(512);

            if (isSqlServer)
                e.HasIndex(x => x.NormalizedEmail).IsUnique().HasFilter("[NormalizedEmail] IS NOT NULL");
            else
                e.HasIndex(x => x.NormalizedEmail).IsUnique();

            if (isSqlServer)
                e.HasIndex(x => x.AgentUserId).IsUnique().HasFilter("[AgentUserId] IS NOT NULL");
            else
                e.HasIndex(x => x.AgentUserId).IsUnique();
        });

        modelBuilder.Entity<MobileProfileSettings>(e =>
        {
            e.ToTable("MobileProfileSettings");
            e.HasKey(x => x.Id);
            e.Property(x => x.ParticipantType).IsRequired().HasMaxLength(40);
            e.Property(x => x.Username).HasMaxLength(64);
            e.Property(x => x.NormalizedUsername).HasMaxLength(64);
            e.Property(x => x.UsernameChangeMonthUtc);
            e.Property(x => x.UsernameChangeCount).HasDefaultValue(0);
            e.Property(x => x.Bio).HasMaxLength(1_000);
            e.Property(x => x.Website).HasMaxLength(2_048);
            e.Property(x => x.Location).HasMaxLength(120);
            e.Property(x => x.PublicEmail).HasMaxLength(320);
            e.Property(x => x.PreferredCommunicationLanguage).HasMaxLength(32);
            e.HasIndex(x => new { x.ProfileId, x.ParticipantType }).IsUnique();

            if (isSqlServer)
                e.HasIndex(x => x.NormalizedUsername).IsUnique().HasFilter("[NormalizedUsername] IS NOT NULL");
            else
                e.HasIndex(x => x.NormalizedUsername).IsUnique();
        });

        modelBuilder.Entity<AgentAssistant>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.NormalizedEmail).HasMaxLength(320);

            if (isSqlServer)
                e.HasIndex(x => x.NormalizedEmail).IsUnique().HasFilter("[NormalizedEmail] IS NOT NULL");
            else
                e.HasIndex(x => x.NormalizedEmail).IsUnique();
        });

        modelBuilder.Entity<ClientProfile>(e =>
        {
            e.Property(x => x.AccountManagementMode)
                .HasMaxLength(32)
                .HasDefaultValue(ClientAccountManagementModes.SharedAccount);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.NormalizedEmail).HasMaxLength(320);
            e.Property(x => x.ExternalIdentityObjectId).HasMaxLength(450);
            e.Property(x => x.ProfileImageContentType).HasMaxLength(127);
            if (isSqlServer)
                e.Property(x => x.ProfileImageContent).HasColumnType("varbinary(max)");

            if (isSqlServer)
                e.HasIndex(x => x.NormalizedEmail).IsUnique().HasFilter("[NormalizedEmail] IS NOT NULL");
            else
                e.HasIndex(x => x.NormalizedEmail).IsUnique();

            if (isSqlServer)
                e.HasIndex(x => x.ExternalIdentityObjectId).IsUnique().HasFilter("[ExternalIdentityObjectId] IS NOT NULL");
            else
                e.HasIndex(x => x.ExternalIdentityObjectId).IsUnique();

            if (isSqlServer)
                e.Property(x => x.RowVersion).IsRowVersion();
            else
                e.Property(x => x.RowVersion)
                    .IsRequired()
                    .IsConcurrencyToken()
                    .HasDefaultValueSql("X''")
                    .ValueGeneratedNever();
        });

        modelBuilder.Entity<ClientFinancialPlan>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.JsonData).IsRequired().HasColumnType("TEXT");
            e.Property(x => x.UpdatedBy).HasMaxLength(320);
            e.Property(x => x.Version).HasDefaultValue(1);
            e.Property(x => x.IsDeleted).HasDefaultValue(false);

            e.HasOne<ClientProfile>()
                .WithMany()
                .HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne<HouseholdAccount>()
                .WithMany()
                .HasForeignKey(x => x.HouseholdAccountId)
                .OnDelete(DeleteBehavior.Restrict);

            if (isSqlServer)
                e.HasIndex(x => x.HouseholdAccountId).IsUnique().HasFilter("[HouseholdAccountId] IS NOT NULL AND [IsDeleted] = 0");
            else
                e.HasIndex(x => new { x.HouseholdAccountId, x.IsDeleted }).IsUnique();
        });

        // ==========================================================
        // EXECUTION ENGINE (MVP)
        // ==========================================================
        modelBuilder.Entity<ActionItem>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.RelatedEntityType).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.OwnerType).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.ActionSurface).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.ActionCategory).HasConversion<string>().HasMaxLength(60);

            e.Property(x => x.Title).HasMaxLength(240);
            e.Property(x => x.RelatedEntityId).HasMaxLength(180);
            e.Property(x => x.OwnerId).HasMaxLength(180);
            e.Property(x => x.EffectiveAgentOid).HasMaxLength(180);
            e.Property(x => x.Source).HasMaxLength(120);
            e.Property(x => x.SourceRef).HasMaxLength(200);
            e.Property(x => x.CreatedBy).HasMaxLength(180);
            e.Property(x => x.DismissedReason).HasMaxLength(400);
            e.Property(x => x.PipelineStage).HasMaxLength(120);

            e.HasIndex(x => new { x.OwnerId, x.Status, x.DueDateUtc });
            e.HasIndex(x => new { x.EffectiveAgentOid, x.Status, x.DueDateUtc });
            e.HasIndex(x => new { x.RelatedEntityType, x.RelatedEntityId });
            e.HasIndex(x => new { x.Status, x.DueDateUtc });
            e.HasIndex(x => new { x.Source, x.SourceRef }).IsUnique(false);
        });

        modelBuilder.Entity<ActionLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Verb).HasMaxLength(120);
            e.Property(x => x.ActorId).HasMaxLength(180);
            e.HasIndex(x => new { x.ActionId, x.OccurredUtc });
        });

        modelBuilder.Entity<Blocker>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RelatedEntityType).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.BlockerType).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.BlockerOwnerType).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.RelatedEntityId).HasMaxLength(180);
            e.Property(x => x.BlockerOwnerId).HasMaxLength(180);
            e.Property(x => x.BlockerReason).HasMaxLength(400);
            e.Property(x => x.Notes).HasMaxLength(800);

            e.HasIndex(x => new { x.RelatedEntityType, x.RelatedEntityId, x.Status });
            e.HasIndex(x => x.UnblockDueDateUtc);
        });

        modelBuilder.Entity<DecisionRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RelatedEntityType).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.RelatedEntityId).HasMaxLength(180);
            e.Property(x => x.Title).HasMaxLength(240);
            e.Property(x => x.RecommendationType).HasConversion<string>().HasMaxLength(60);
            e.Property(x => x.CreatedBy).HasMaxLength(180);

            e.HasIndex(x => new { x.RelatedEntityType, x.RelatedEntityId, x.CreatedUtc });
        });

        modelBuilder.Entity<PlaybookExecution>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ExecutionKey).HasMaxLength(200);
            e.HasIndex(x => x.ExecutionKey).IsUnique();
        });

        modelBuilder.Entity<Commitment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RelatedEntityType).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.RelatedEntityId).HasMaxLength(180);
            e.Property(x => x.PromisedByType).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.PromisedById).HasMaxLength(180);
            e.Property(x => x.PromisedToType).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.PromisedToId).HasMaxLength(180);
            e.Property(x => x.PromiseText).HasMaxLength(500).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.CreatedBy).HasMaxLength(180);

            e.HasIndex(x => new { x.RelatedEntityType, x.RelatedEntityId });
            e.HasIndex(x => new { x.PromisedById, x.Status });
            e.HasIndex(x => new { x.DueDateUtc, x.Status });
        });

        // ==========================================================
        // ANALYTICS EVENTS
        // ==========================================================
        modelBuilder.Entity<AnalyticsEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.EventId).IsRequired();
            e.Property(x => x.EventType).IsRequired().HasMaxLength(80);
            e.Property(x => x.PageKey).HasMaxLength(120);
            e.Property(x => x.SectionKey).HasMaxLength(120);
            e.Property(x => x.ElementKey).HasMaxLength(160);
            e.Property(x => x.ButtonLabel).HasMaxLength(200);
            e.Property(x => x.FormKey).HasMaxLength(120);
            e.Property(x => x.QuoteType).HasMaxLength(80);
            e.Property(x => x.Url).HasMaxLength(500);
            e.Property(x => x.Path).HasMaxLength(300);
            e.Property(x => x.Referrer).HasMaxLength(500);
            e.Property(x => x.SessionId).HasMaxLength(120);
            e.Property(x => x.VisitorId).HasMaxLength(120);
            e.Property(x => x.UtmSource).HasMaxLength(160);
            e.Property(x => x.UtmMedium).HasMaxLength(160);
            e.Property(x => x.UtmCampaign).HasMaxLength(160);
            e.Property(x => x.UtmId).HasMaxLength(160);
            e.Property(x => x.Fbclid).HasMaxLength(120);
            e.Property(x => x.Environment).HasMaxLength(40);
            e.Property(x => x.Host).HasMaxLength(160);
            e.Property(x => x.SubmitOutcome).HasMaxLength(40);
            e.Property(x => x.MetadataJson).HasColumnType(isSqlServer ? "nvarchar(max)" : "TEXT");
            e.Property(x => x.SchemaVersion).HasDefaultValue(1);
            e.Property(x => x.TrackingVersion).HasMaxLength(80);
            e.Property(x => x.EventUtc).IsRequired();
            e.Property(x => x.ReceivedUtc).IsRequired();
            e.Property(x => x.AgentSlug).HasMaxLength(200);

            e.HasIndex(x => x.ReceivedUtc);
            e.HasIndex(x => x.EventType);
            e.HasIndex(x => x.PageKey);
            e.HasIndex(x => x.ElementKey);
            e.HasIndex(x => x.FormKey);
            e.HasIndex(x => x.SessionId);
            e.HasIndex(x => x.VisitorId);
            e.HasIndex(x => x.AgentTrackingProfileId);
            e.HasIndex(x => x.AgentSlug);
            e.HasIndex(x => x.ClientEventId)
                .IsUnique()
                .HasDatabaseName("UX_AnalyticsEvents_ClientEventId")
                .HasFilter("[ClientEventId] IS NOT NULL");
            e.HasIndex(x => x.UtmSource);
            e.HasIndex(x => x.UtmCampaign);
            e.HasIndex(x => x.UtmId);
            e.HasIndex(x => new { x.AgentTrackingProfileId, x.EventUtc });
            e.HasIndex(x => new { x.Environment, x.EventUtc });
            e.HasIndex(x => new { x.EventType, x.EventUtc });
            e.HasIndex(x => new { x.PageKey, x.EventUtc });
            e.HasIndex(x => new { x.ElementKey, x.EventUtc });

            // ── Behavior Intelligence columns (all nullable, additive) ──
            e.Property(x => x.ReferrerHost).HasMaxLength(200);
            e.Property(x => x.DeviceType).HasMaxLength(60);
            e.Property(x => x.Browser).HasMaxLength(100);
            e.Property(x => x.OperatingSystem).HasMaxLength(100);
            e.Property(x => x.TimeZone).HasMaxLength(100);
            e.Property(x => x.Language).HasMaxLength(40);

            e.Property(x => x.UserAgent).HasMaxLength(2048);
            e.Property(x => x.IpAddress).HasMaxLength(100);
            e.Property(x => x.ScreenWidth);
            e.Property(x => x.ScreenHeight);
            e.Property(x => x.ViewportWidth);
            e.Property(x => x.ViewportHeight);
            e.Property(x => x.ScrollPercent);
            e.Property(x => x.HumanInteractionCount);
            e.Property(x => x.DwellMilliseconds);
            e.Property(x => x.EngagedMilliseconds);
            e.Property(x => x.IsBounceCandidate);
            e.Property(x => x.IsExitPage);
            e.Property(x => x.UtmTerm).HasMaxLength(160);
            e.Property(x => x.UtmContent).HasMaxLength(160);
            e.Property(x => x.MetaCampaignId).HasMaxLength(200);
            e.Property(x => x.MetaCampaignName).HasMaxLength(200);
            e.Property(x => x.MetaAdSetId).HasMaxLength(200);
            e.Property(x => x.MetaAdSetName).HasMaxLength(200);
            e.Property(x => x.MetaAdId).HasMaxLength(200);
            e.Property(x => x.MetaAdName).HasMaxLength(200);
            e.Property(x => x.Placement).HasMaxLength(100);
            e.Property(x => x.FormId).HasMaxLength(120);
            e.Property(x => x.FieldName).HasMaxLength(120);
            e.Property(x => x.ElementId).HasMaxLength(120);

            // Behavior intelligence indexes
            e.HasIndex(x => x.DeviceType);
            e.HasIndex(x => x.SessionId).HasDatabaseName("IX_AnalyticsEvents_SessionId_Behavior");
        });

        modelBuilder.Entity<MetaSignalEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.CreatedUtc).IsRequired();
            e.Property(x => x.EventId).IsRequired().HasMaxLength(120);
            e.Property(x => x.EventName).IsRequired().HasMaxLength(120);
            e.Property(x => x.EventCategory).HasMaxLength(80);
            e.Property(x => x.SessionId).HasMaxLength(120);
            e.Property(x => x.VisitorId).HasMaxLength(120);
            e.Property(x => x.QuoteType).HasMaxLength(80);
            e.Property(x => x.PageKey).HasMaxLength(120);
            e.Property(x => x.EffectivePageKey).HasMaxLength(120);
            e.Property(x => x.PageVariant).HasMaxLength(80);
            e.Property(x => x.PageMode).HasMaxLength(80);
            e.Property(x => x.TrafficType).HasMaxLength(40);
            e.Property(x => x.StepName).HasMaxLength(120);
            e.Property(x => x.ScoreTier).HasMaxLength(40);
            e.Property(x => x.MetaDeduplicationKey).HasMaxLength(220);
            e.Property(x => x.UtmSource).HasMaxLength(160);
            e.Property(x => x.UtmMedium).HasMaxLength(160);
            e.Property(x => x.UtmCampaign).HasMaxLength(160);
            e.Property(x => x.UtmId).HasMaxLength(160);
            e.Property(x => x.UtmContent).HasMaxLength(160);
            e.Property(x => x.Referrer).HasMaxLength(500);
            e.Property(x => x.UserAgentHash).HasMaxLength(128);
            e.Property(x => x.IpHash).HasMaxLength(128);
            e.Property(x => x.AgentSlug).HasMaxLength(200);
            e.Property(x => x.Environment).HasMaxLength(40);
            e.Property(x => x.Host).HasMaxLength(160);
            e.Property(x => x.MetadataJson).HasColumnType(isSqlServer ? "nvarchar(max)" : "TEXT");

            e.HasIndex(x => x.CreatedUtc);
            e.HasIndex(x => x.SessionId);
            e.HasIndex(x => x.VisitorId);
            e.HasIndex(x => x.LeadId);
            e.HasIndex(x => x.QuoteType);
            e.HasIndex(x => x.EventName);
            e.HasIndex(x => x.PageMode);
            e.HasIndex(x => x.ScoreTier);
            e.HasIndex(x => x.TrafficType);
            e.HasIndex(x => x.UtmCampaign);
            e.HasIndex(x => x.AgentTrackingProfileId);
            e.HasIndex(x => x.AgentSlug);
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.SessionId, x.QuoteType, x.CreatedUtc });
            e.HasIndex(x => new { x.EventName, x.CreatedUtc });
        });

        modelBuilder.Entity<AnalyticsDriftAlert>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.IncidentKey).IsRequired().HasMaxLength(160);
            e.Property(x => x.MetricKey).IsRequired().HasMaxLength(120);
            e.Property(x => x.EventType).IsRequired().HasMaxLength(160);
            e.Property(x => x.Category).IsRequired().HasMaxLength(80);
            e.Property(x => x.Severity).IsRequired().HasMaxLength(32);
            e.Property(x => x.MetricUnit).IsRequired().HasMaxLength(32);
            e.Property(x => x.ScopeKey).IsRequired().HasMaxLength(120);
            e.Property(x => x.CurrentValue).HasPrecision(18, 4);
            e.Property(x => x.BaselineValue).HasPrecision(18, 4);
            e.Property(x => x.DeviationPercent).HasPrecision(18, 4);
            e.Property(x => x.Summary).HasMaxLength(500);
            e.Property(x => x.DetailsJson).HasColumnType(isSqlServer ? "nvarchar(max)" : "TEXT");

            e.HasIndex(x => x.IsActive);
            e.HasIndex(x => x.ObservedUtc);
            e.HasIndex(x => x.Severity);
            e.HasIndex(x => x.EventType);
            e.HasIndex(x => x.IncidentKey);
            e.HasIndex(x => new { x.IsActive, x.Severity, x.ObservedUtc });
            e.HasIndex(x => new { x.ScopeKey, x.ObservedUtc });
        });

        // WEBSITE LEADS
        modelBuilder.Entity<WebsiteLead>(e =>
        {
            e.HasIndex(x => x.AgentTrackingProfileId);
            e.HasIndex(x => x.AgentSlug);
            e.HasIndex(x => x.CreatedUtc);
            e.HasIndex(x => new { x.AgentTrackingProfileId, x.CreatedUtc });
            e.HasIndex(x => new { x.Environment, x.CreatedUtc });
            e.HasIndex(x => x.SourcePageKey);
            e.HasIndex(x => x.SourceCtaKey);
            e.HasIndex(x => x.UtmSource);
            e.HasIndex(x => x.UtmCampaign);
        });

        modelBuilder.Entity<WebsiteLeadIntakeLink>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.WorkstationLeadId).IsRequired().HasMaxLength(64);
            e.Property(x => x.AgentUserId).IsRequired().HasMaxLength(180);
            e.Property(x => x.Bucket).IsRequired().HasMaxLength(80);
            e.Property(x => x.SourcePageKey).HasMaxLength(160);
            e.Property(x => x.SourceCtaKey).HasMaxLength(160);
            e.Property(x => x.PageVariant).HasMaxLength(80);
            e.Property(x => x.PageMode).HasMaxLength(80);
            e.Property(x => x.PagePath).HasMaxLength(300);
            e.Property(x => x.LandingPageUrl).HasMaxLength(500);
            e.Property(x => x.ReferrerUrl).HasMaxLength(500);
            e.Property(x => x.InterestType).HasMaxLength(120);
            e.Property(x => x.OfferKey).HasMaxLength(120);
            e.Property(x => x.ProductType).HasMaxLength(120);
            e.Property(x => x.UtmSource).HasMaxLength(160);
            e.Property(x => x.UtmMedium).HasMaxLength(160);
            e.Property(x => x.UtmCampaign).HasMaxLength(160);
            e.Property(x => x.UtmId).HasMaxLength(160);
            e.Property(x => x.UtmTerm).HasMaxLength(160);
            e.Property(x => x.UtmContent).HasMaxLength(160);
            e.Property(x => x.Fbclid).HasMaxLength(160);
            e.Property(x => x.Fbp).HasMaxLength(256);
            e.Property(x => x.Fbc).HasMaxLength(512);
            e.Property(x => x.ClientIpAddress).HasMaxLength(128);
            e.Property(x => x.ClientUserAgent).HasMaxLength(1024);
            e.Property(x => x.MetaCampaignId).HasMaxLength(160);
            e.Property(x => x.MetaAdSetId).HasMaxLength(160);
            e.Property(x => x.MetaAdId).HasMaxLength(160);
            e.Property(x => x.SessionId).HasMaxLength(120);
            e.Property(x => x.VisitorId).HasMaxLength(120);
            e.Property(x => x.DiscoverySummaryJson).HasColumnType(isSqlServer ? "nvarchar(max)" : "TEXT");
            e.Property(x => x.EstimateSummary).HasMaxLength(600);
            e.Property(x => x.RecommendationPrimaryKey).HasMaxLength(160);
            e.Property(x => x.RecommendationPrimaryTitle).HasMaxLength(240);
            e.Property(x => x.RecommendationSecondaryKey).HasMaxLength(160);
            e.Property(x => x.RecommendationSecondaryTitle).HasMaxLength(240);
            e.Property(x => x.SnapshotJson).HasColumnType(isSqlServer ? "nvarchar(max)" : "TEXT");

            e.HasIndex(x => x.WebsiteLeadRowId).IsUnique();
            e.HasIndex(x => new { x.WorkstationLeadId, x.SubmittedUtc });
            e.HasIndex(x => new { x.AgentUserId, x.SubmittedUtc });

            e.HasOne<WebsiteLead>()
                .WithMany()
                .HasForeignKey(x => x.WebsiteLeadRowId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne<WorkstationLeadProfile>()
                .WithMany()
                .HasForeignKey(x => x.WorkstationLeadId)
                .HasPrincipalKey(x => x.LeadId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AGENT TRACKING
        modelBuilder.Entity<AgentTrackingProfile>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AgentUserId).IsRequired().HasMaxLength(450);
            e.Property(x => x.AgentUpn).IsRequired().HasMaxLength(450);
            e.Property(x => x.Slug).IsRequired().HasMaxLength(200);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.Status).IsRequired().HasMaxLength(40);
            e.Property(x => x.PreferredEnvironment).HasMaxLength(40);
            e.Property(x => x.CreatedUtc).IsRequired();
            e.Property(x => x.UpdatedUtc).IsRequired();
            e.HasIndex(x => x.AgentUserId).IsUnique();
            e.HasIndex(x => x.AgentUpn);
            e.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<AgentTrackingAlias>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Slug).IsRequired().HasMaxLength(200);
            e.Property(x => x.CreatedUtc).IsRequired();
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => new { x.AgentTrackingProfileId, x.IsCanonical });
            e.HasOne(x => x.Profile)
                .WithMany(p => p.Aliases)
                .HasForeignKey(x => x.AgentTrackingProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OnboardingSubmission>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FirstName).IsRequired().HasMaxLength(120);
            e.Property(x => x.MiddleName).HasMaxLength(120);
            e.Property(x => x.LastName).IsRequired().HasMaxLength(120);
            e.Property(x => x.PreferredName).HasMaxLength(120);
            e.Property(x => x.Phone).IsRequired().HasMaxLength(60);
            e.Property(x => x.Email).IsRequired().HasMaxLength(320);
            e.Property(x => x.CurrentAddress).HasMaxLength(240);
            e.Property(x => x.City).HasMaxLength(160);
            e.Property(x => x.State).HasMaxLength(80);
            e.Property(x => x.Zip).HasMaxLength(40);
            e.Property(x => x.MailingAddress).HasMaxLength(240);
            e.Property(x => x.EmergencyContactName).HasMaxLength(160);
            e.Property(x => x.EmergencyContactPhone).HasMaxLength(60);
            e.Property(x => x.EmergencyContactRelationship).HasMaxLength(120);

            e.Property(x => x.RoleType).IsRequired().HasMaxLength(80);
            e.Property(x => x.JobTitle).HasMaxLength(160);
            e.Property(x => x.Department).HasMaxLength(160);
            e.Property(x => x.Manager).HasMaxLength(160);
            e.Property(x => x.WorkState).HasMaxLength(80);
            e.Property(x => x.WorkLocation).HasMaxLength(200);
            e.Property(x => x.EmploymentType).HasMaxLength(80);
            e.Property(x => x.PayType).HasMaxLength(80);
            e.Property(x => x.WorkNotes).HasColumnType("text");

            e.Property(x => x.SsnLast4).HasMaxLength(400);   // widened: stores encrypted ciphertext
            e.Property(x => x.SsnNote).HasMaxLength(400);
            e.Property(x => x.DriverLicenseNumber).HasMaxLength(400); // widened: stores encrypted ciphertext
            e.Property(x => x.DriverLicenseState).HasMaxLength(40);
            e.Property(x => x.WorkAuthorizationStatus).HasMaxLength(160);
            e.Property(x => x.CitizenshipStatus).HasMaxLength(160);

            e.Property(x => x.TaxFilingStatus).HasMaxLength(120);
            e.Property(x => x.FederalWithholding).HasMaxLength(120);
            e.Property(x => x.StateWithholding).HasMaxLength(120);
            e.Property(x => x.BankName).HasMaxLength(160);
            e.Property(x => x.BankAccountType).HasMaxLength(80);
            e.Property(x => x.BankRoutingNumber).HasMaxLength(400); // widened: stores encrypted ciphertext
            e.Property(x => x.BankAccountNumber).HasMaxLength(400); // widened: stores encrypted ciphertext

            e.Property(x => x.ElectronicSignatureName).HasMaxLength(200);

            e.Property(x => x.ResidentStateLicense).HasMaxLength(80);
            e.Property(x => x.NonResidentStates).HasMaxLength(400);
            e.Property(x => x.LicensesHeld).HasMaxLength(400);
            e.Property(x => x.LicenseNumbers).HasMaxLength(400);
            e.Property(x => x.CarrierAppointments).HasMaxLength(400);
            e.Property(x => x.EOCoverage).HasMaxLength(400);
            e.Property(x => x.SupervisionNotes).HasColumnType("text");

            e.Property(x => x.RegulatoryExplanation).HasColumnType("text");
            e.Property(x => x.CriminalExplanation).HasColumnType("text");
            e.Property(x => x.AdministrativeExplanation).HasColumnType("text");
            e.Property(x => x.TerminationExplanation).HasColumnType("text");
            e.Property(x => x.OtherDisclosuresExplanation).HasColumnType("text");

            e.Property(x => x.DocumentNotes).HasColumnType("text");

            e.HasIndex(x => x.InviteId).IsUnique();

            e.HasOne(x => x.Invite)
                .WithMany(i => i.Submissions)
                .HasForeignKey(x => x.InviteId)
                .OnDelete(DeleteBehavior.Cascade);
        });
            // Raw Email unique index removed (Stage 1 identity hardening).
            // NormalizedEmail is the enforced uniqueness guardrail (configured above).
        modelBuilder.Entity<WorkstationLeadProfile>(e =>
        {
            e.HasKey(x => x.LeadId);
            e.ToTable("WorkstationLeadProfiles");
            e.Property(x => x.LeadId).HasMaxLength(64);
            e.Property(x => x.AgentUserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.Bucket).HasMaxLength(80).IsRequired();
            e.Property(x => x.OriginalLeadType).HasMaxLength(80).IsRequired(false);
            e.Property(x => x.FirstName).HasMaxLength(120);
            e.Property(x => x.LastName).HasMaxLength(120);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.Phone).HasMaxLength(60);
            e.Property(x => x.Phone2).HasMaxLength(60);
            e.Property(x => x.AddressLine).HasMaxLength(240);
            e.Property(x => x.City).HasMaxLength(160);
            e.Property(x => x.State).HasMaxLength(40);
            e.Property(x => x.County).HasMaxLength(120);
            e.Property(x => x.ZipCode).HasMaxLength(24);
            e.Property(x => x.Age).HasMaxLength(12);
            e.Property(x => x.Gender).HasMaxLength(20);
            e.Property(x => x.MortgageLender).HasMaxLength(160);
            e.Property(x => x.LoanAmount).HasMaxLength(80);
            e.Property(x => x.Btc).HasMaxLength(40);
            e.Property(x => x.CrmStatus).HasMaxLength(60);
            e.Property(x => x.CrmStage).HasMaxLength(80);
            e.Property(x => x.CrmNotes).HasColumnType("text");

            e.HasIndex(x => x.AgentUserId);
            e.HasIndex(x => new { x.AgentUserId, x.Phone });
            e.HasIndex(x => x.Bucket);
            e.HasIndex(x => x.OriginalLeadType);
            e.HasIndex(x => new { x.AgentUserId, x.OriginalLeadType });
            e.HasIndex(x => x.Phone);
            e.HasIndex(x => x.Email);

            if (isSqlServer)
                e.Property(x => x.RowVersion).IsRowVersion();
            else
                e.Property(x => x.RowVersion)
                    .IsRequired()
                    .IsConcurrencyToken()
                    .HasDefaultValueSql("X''")
                    .ValueGeneratedNever();
        });


        modelBuilder.Entity<GraphCalendarSubscription>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("GraphCalendarSubscriptions");
            e.Property(x => x.AgentUserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.CalendarUserId).HasMaxLength(450);
            e.Property(x => x.CalendarEmail).HasMaxLength(320);
            e.Property(x => x.GraphSubscriptionId).HasMaxLength(256).IsRequired();
            e.Property(x => x.Resource).HasMaxLength(512).IsRequired();
            e.Property(x => x.ChangeType).HasMaxLength(80).IsRequired();
            e.Property(x => x.ClientState).HasMaxLength(256).IsRequired();
            e.Property(x => x.LastError).HasMaxLength(2048);

            e.HasIndex(x => x.GraphSubscriptionId).IsUnique();
            e.HasIndex(x => new { x.AgentUserId, x.CalendarEmail });
            e.HasIndex(x => new { x.IsActive, x.ExpirationUtc });
        });

        modelBuilder.Entity<AppointmentSyncLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("AppointmentSyncLogs");
            e.Property(x => x.WorkstationLeadId).HasMaxLength(64);
            e.Property(x => x.ClientProfileId).HasMaxLength(450);
            e.Property(x => x.AgentUserId).HasMaxLength(450);
            e.Property(x => x.CalendarUserId).HasMaxLength(450);
            e.Property(x => x.CalendarEmail).HasMaxLength(320);
            e.Property(x => x.GraphSubscriptionId).HasMaxLength(256);
            e.Property(x => x.GraphEventId).HasMaxLength(256);
            e.Property(x => x.Operation).HasMaxLength(80).IsRequired();
            e.Property(x => x.Source).HasMaxLength(80).IsRequired();
            e.Property(x => x.Error).HasMaxLength(2048);
            e.Property(x => x.DiagnosticJson).HasColumnType("text");

            e.HasIndex(x => x.AppointmentId);
            e.HasIndex(x => x.WorkstationLeadId);
            e.HasIndex(x => x.GraphEventId);
            e.HasIndex(x => x.CreatedUtc);
        });

        modelBuilder.Entity<LeadAppointment>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("LeadAppointments");
            e.Property(x => x.WorkstationLeadId).HasMaxLength(64).IsRequired(false);
            e.Property(x => x.OwnerAgentUserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.WebsiteLeadId).HasMaxLength(64);
            e.Property(x => x.ClientProfileId).HasMaxLength(450);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.BookingProvider).HasMaxLength(80);
            e.Property(x => x.BookingSource).HasMaxLength(80).IsRequired();
            e.Property(x => x.RequestedBookingSource).HasMaxLength(80).IsRequired();
            e.Property(x => x.ConfirmationSource).HasMaxLength(80);
            e.Property(x => x.BookingConfigurationSource).HasMaxLength(80);
            e.Property(x => x.BookingAgentSlug).HasMaxLength(200);
            e.Property(x => x.BookingAgentUserId).HasMaxLength(450);
            e.Property(x => x.BookingCalendarUserId).HasMaxLength(450);
            e.Property(x => x.BookingCalendarEmail).HasMaxLength(320);
            e.Property(x => x.BookingPageIdOrMailbox).HasMaxLength(320);
            e.Property(x => x.CalendarEventId).HasMaxLength(256);
            e.Property(x => x.CalendarEventWebLink).HasMaxLength(2048);
            e.Property(x => x.MeetingUrl).HasMaxLength(2048);
            e.Property(x => x.LastSyncStatus).HasMaxLength(80);
            e.Property(x => x.LastSyncError).HasMaxLength(2048);
            e.Property(x => x.RawProviderPayloadJson).HasColumnType("text");

            e.HasIndex(x => x.WorkstationLeadId);
            e.HasIndex(x => new { x.WorkstationLeadId, x.UpdatedUtc });
            e.HasIndex(x => new { x.WorkstationLeadId, x.ScheduledStartUtc });
            e.HasIndex(x => new { x.OwnerAgentUserId, x.Status, x.ScheduledStartUtc });
            e.HasIndex(x => x.CalendarEventId);
            e.HasIndex(x => x.WebsiteLeadIntakeLinkId);
            e.HasIndex(x => x.WebsiteLeadId);
            e.HasIndex(x => x.ClientProfileId);
            e.HasIndex(x => new { x.BookingProvider, x.CalendarEventId });

            e.HasOne(x => x.WorkstationLead)
                .WithMany()
                .HasForeignKey(x => x.WorkstationLeadId)
                .OnDelete(isSqlServer ? DeleteBehavior.NoAction : DeleteBehavior.Cascade);

            e.HasOne(x => x.WebsiteLeadIntakeLink)
                .WithMany()
                .HasForeignKey(x => x.WebsiteLeadIntakeLinkId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // PROPOSALS
        modelBuilder.Entity<Proposal>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("Proposals");
            e.Property(x => x.LeadId).HasMaxLength(128).IsRequired();
            e.Property(x => x.LeadName).HasMaxLength(240).IsRequired(false);
            e.Property(x => x.AgentUserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.BucketsJson).IsRequired();
            e.Property(x => x.QueueKey).HasMaxLength(80).IsRequired(false);
            e.Property(x => x.ScopeKey).HasMaxLength(200).IsRequired(false);
            e.Property(x => x.LeadKey).HasMaxLength(200).IsRequired(false);
            e.Property(x => x.PageTitle).HasMaxLength(240).IsRequired(false);
            e.Property(x => x.IsDraft).IsRequired();
            e.Property(x => x.CreatedUtc).IsRequired();
            e.Property(x => x.UpdatedUtc).IsRequired();

            e.HasIndex(x => new { x.AgentUserId, x.LeadId });
            e.HasIndex(x => x.AgentUserId);
        });

        modelBuilder.Entity<UnderwritingRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.ToTable("UnderwritingRecords");
            e.Property(x => x.LeadId).HasMaxLength(128).IsRequired(false);
            e.Property(x => x.LeadName).HasMaxLength(240).IsRequired(false);
            e.Property(x => x.AgentUserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.PayloadJson).IsRequired();
            e.Property(x => x.ProductCode).HasMaxLength(32).IsRequired(false);
            e.Property(x => x.QueueKey).HasMaxLength(80).IsRequired(false);
            e.Property(x => x.ScopeKey).HasMaxLength(200).IsRequired(false);
            e.Property(x => x.PageTitle).HasMaxLength(240).IsRequired(false);
            e.Property(x => x.IsDraft).IsRequired();
            e.Property(x => x.CreatedUtc).IsRequired();
            e.Property(x => x.UpdatedUtc).IsRequired();

            e.HasIndex(x => new { x.AgentUserId, x.LeadId });
            e.HasIndex(x => x.AgentUserId);
            e.HasIndex(x => x.ProductCode);
        });

        // ==========================================================
        // FINANCIAL INTELLIGENCE IMPORT + RECONCILIATION
        //
        // FinanceToolStates remains the planning authority. These
        // records contain provider facts, detected recurring streams,
        // and links to stable existing Expense Lens item identifiers.
        // ==========================================================
        modelBuilder.Entity<FinancialDataConnection>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.ProviderKey)
                .IsRequired()
                .HasMaxLength(50);

            e.Property(x => x.ProviderItemId)
                .IsRequired()
                .HasMaxLength(200);

            e.Property(x => x.ProviderInstitutionId)
                .HasMaxLength(200);

            e.Property(x => x.DisplayName)
                .HasMaxLength(200);

            e.Property(x => x.Status)
                .IsRequired()
                .HasMaxLength(40);

            e.Property(x => x.EncryptedAccessToken)
                .HasMaxLength(4000);

            e.Property(x => x.SyncCursor)
                .HasMaxLength(4000);

            e.Property(x => x.LastErrorCode)
                .HasMaxLength(100);

            e.Property(x => x.LastErrorMessage)
                .HasMaxLength(2000);

            e.HasIndex(x => new
                {
                    x.ClientProfileId,
                    x.ProviderKey,
                    x.ProviderItemId
                })
                .IsUnique();

            e.HasIndex(x => new
                {
                    x.ClientProfileId,
                    x.Status
                });

            e.HasOne<ClientProfile>()
                .WithMany()
                .HasForeignKey(x => x.ClientProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ImportedFinancialAccount>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.ProviderAccountId)
                .IsRequired()
                .HasMaxLength(200);

            e.Property(x => x.PersistentAccountKey)
                .HasMaxLength(200);

            e.Property(x => x.Name)
                .IsRequired()
                .HasMaxLength(200);

            e.Property(x => x.OfficialName)
                .HasMaxLength(300);

            e.Property(x => x.Mask)
                .HasMaxLength(20);

            e.Property(x => x.AccountType)
                .IsRequired()
                .HasMaxLength(50);

            e.Property(x => x.AccountSubtype)
                .HasMaxLength(80);

            e.Property(x => x.CurrencyCode)
                .IsRequired()
                .HasMaxLength(3);

            e.HasIndex(x => new
                {
                    x.FinancialDataConnectionId,
                    x.ProviderAccountId
                })
                .IsUnique();

            e.HasIndex(x => new
                {
                    x.ClientProfileId,
                    x.IsClosed
                });

            e.HasOne<ClientProfile>()
                .WithMany()
                .HasForeignKey(x => x.ClientProfileId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne<FinancialDataConnection>()
                .WithMany()
                .HasForeignKey(x => x.FinancialDataConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ImportedFinancialTransaction>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.ProviderTransactionId)
                .IsRequired()
                .HasMaxLength(200);

            e.Property(x => x.ProviderPendingTransactionId)
                .HasMaxLength(200);

            e.Property(x => x.OriginalName)
                .IsRequired()
                .HasMaxLength(500);

            e.Property(x => x.OriginalMerchantName)
                .HasMaxLength(500);

            e.Property(x => x.CurrencyCode)
                .IsRequired()
                .HasMaxLength(3);

            e.Property(x => x.ProviderPayloadJson)
                .IsRequired();

            e.HasIndex(x => new
                {
                    x.FinancialDataConnectionId,
                    x.ProviderTransactionId
                })
                .IsUnique();

            e.HasIndex(x => new
                {
                    x.ClientProfileId,
                    x.PostedUtc
                });

            e.HasIndex(x => new
                {
                    x.ImportedFinancialAccountId,
                    x.PostedUtc
                });

            e.HasIndex(x => new
                {
                    x.ClientProfileId,
                    x.IsPending,
                    x.IsRemoved
                });

            e.HasOne<ClientProfile>()
                .WithMany()
                .HasForeignKey(x => x.ClientProfileId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne<FinancialDataConnection>()
                .WithMany()
                .HasForeignKey(x => x.FinancialDataConnectionId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne<ImportedFinancialAccount>()
                .WithMany()
                .HasForeignKey(x => x.ImportedFinancialAccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RecurringFinancialStream>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.StreamKey)
                .IsRequired()
                .HasMaxLength(240);

            e.Property(x => x.NormalizedMerchantKey)
                .IsRequired()
                .HasMaxLength(240);

            e.Property(x => x.DisplayName)
                .IsRequired()
                .HasMaxLength(300);

            e.Property(x => x.Cadence)
                .IsRequired()
                .HasMaxLength(40);

            e.Property(x => x.Status)
                .IsRequired()
                .HasMaxLength(40);

            e.Property(x => x.Confidence)
                .HasPrecision(5, 4);

            e.Property(x => x.EvidenceJson)
                .IsRequired();

            e.HasIndex(x => new
                {
                    x.ClientProfileId,
                    x.StreamKey
                })
                .IsUnique();

            e.HasIndex(x => new
                {
                    x.ClientProfileId,
                    x.Status
                });

            e.HasOne<ClientProfile>()
                .WithMany()
                .HasForeignKey(x => x.ClientProfileId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne<FinancialDataConnection>()
                .WithMany()
                .HasForeignKey(x => x.FinancialDataConnectionId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne<ImportedFinancialAccount>()
                .WithMany()
                .HasForeignKey(x => x.ImportedFinancialAccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ExpenseLensStreamLink>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.ExpenseLensToolId)
                .IsRequired()
                .HasMaxLength(100);

            e.Property(x => x.ExpenseLensItemId)
                .IsRequired()
                .HasMaxLength(200);

            e.Property(x => x.Status)
                .IsRequired()
                .HasMaxLength(40);

            e.Property(x => x.ConfirmedByUserId)
                .HasMaxLength(450);

            e.HasIndex(x => x.RecurringFinancialStreamId)
                .IsUnique();

            e.HasIndex(x => new
                {
                    x.ClientProfileId,
                    x.ExpenseLensToolId,
                    x.ExpenseLensItemId
                });

            e.HasOne<ClientProfile>()
                .WithMany()
                .HasForeignKey(x => x.ClientProfileId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne<RecurringFinancialStream>()
                .WithMany()
                .HasForeignKey(x => x.RecurringFinancialStreamId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ==========================================================
        // FINANCIAL INTELLIGENCE EVALUATION
        //
        // These records are additive, normalized evaluation results. They do
        // not replace FinanceToolState or imported provider facts.
        // ==========================================================
        modelBuilder.Entity<ClientFinancialIntelligenceProfile>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).IsRequired().HasMaxLength(40);
            e.Property(x => x.DataCompletenessScore).HasPrecision(5, 4);
            e.Property(x => x.BehavioralBaselineStatus).IsRequired().HasMaxLength(40);
            e.Property(x => x.PersonalizationMaturity).IsRequired().HasMaxLength(40);
            e.Property(x => x.RecommendationResponseSummary).IsRequired().HasMaxLength(1000);
            e.Property(x => x.CurrentRiskSummary).IsRequired().HasMaxLength(600);
            e.Property(x => x.CurrentOpportunitySummary).IsRequired().HasMaxLength(600);
            e.Property(x => x.CurrentLeakageSummary).IsRequired().HasMaxLength(600);
            e.HasIndex(x => x.ClientProfileId).IsUnique();
            e.HasIndex(x => x.LastEvaluatedUtc);
            e.HasOne<ClientProfile>()
                .WithMany()
                .HasForeignKey(x => x.ClientProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FinancialObservation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ObservationKey).IsRequired().HasMaxLength(240);
            e.Property(x => x.RuleIdentifier).IsRequired().HasMaxLength(120);
            e.Property(x => x.ObservationType).IsRequired().HasMaxLength(100);
            e.Property(x => x.SourceType).IsRequired().HasMaxLength(100);
            e.Property(x => x.SourceReference).HasMaxLength(500);
            e.Property(x => x.NumericValue).HasPrecision(19, 4);
            e.Property(x => x.PreviousValue).HasPrecision(19, 4);
            e.Property(x => x.Unit).HasMaxLength(80);
            e.Property(x => x.Confidence).HasPrecision(5, 4);
            e.Property(x => x.EvidenceSummary).IsRequired().HasMaxLength(2000);
            e.Property(x => x.Status).IsRequired().HasMaxLength(40);
            e.HasIndex(x => new { x.ClientProfileId, x.ObservationKey }).IsUnique();
            e.HasIndex(x => new { x.ClientProfileId, x.ObservationType, x.Status });
            e.HasIndex(x => new { x.ClientProfileId, x.PeriodEndUtc });
            e.HasIndex(x => new { x.RuleIdentifier, x.RuleVersion });
            e.HasOne<ClientProfile>()
                .WithMany()
                .HasForeignKey(x => x.ClientProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FinancialFinding>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FindingKey).IsRequired().HasMaxLength(240);
            e.Property(x => x.RuleIdentifier).IsRequired().HasMaxLength(120);
            e.Property(x => x.Category).IsRequired().HasMaxLength(40);
            e.Property(x => x.FindingType).IsRequired().HasMaxLength(100);
            e.Property(x => x.Title).IsRequired().HasMaxLength(240);
            e.Property(x => x.Explanation).IsRequired().HasMaxLength(2000);
            e.Property(x => x.EstimatedImpact).HasPrecision(19, 4);
            e.Property(x => x.ImpactUnit).HasMaxLength(80);
            e.Property(x => x.Confidence).HasPrecision(5, 4);
            e.Property(x => x.PriorityScore).HasPrecision(6, 2);
            e.Property(x => x.Urgency).IsRequired().HasMaxLength(40);
            e.Property(x => x.Difficulty).IsRequired().HasMaxLength(40);
            e.Property(x => x.EvidenceSummary).IsRequired().HasMaxLength(4000);
            e.Property(x => x.ClientFacingSummary).IsRequired().HasMaxLength(2000);
            e.Property(x => x.AgentFacingSummary).IsRequired().HasMaxLength(2000);
            e.Property(x => x.Disclaimer).HasMaxLength(1000);
            e.Property(x => x.AgentReviewedByUserId).HasMaxLength(450);
            e.Property(x => x.Status).IsRequired().HasMaxLength(40);
            e.HasIndex(x => new { x.ClientProfileId, x.FindingKey }).IsUnique();
            e.HasIndex(x => new { x.ClientProfileId, x.Status, x.PriorityScore });
            e.HasIndex(x => new { x.ClientProfileId, x.FindingType });
            e.HasIndex(x => new { x.RuleIdentifier, x.RuleVersion });
            e.HasOne<ClientProfile>()
                .WithMany()
                .HasForeignKey(x => x.ClientProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FinancialFindingObservation>(e =>
        {
            e.HasKey(x => new { x.FinancialFindingId, x.FinancialObservationId });
            e.HasIndex(x => x.FinancialObservationId);
            e.HasOne<FinancialFinding>()
                .WithMany()
                .HasForeignKey(x => x.FinancialFindingId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FinancialObservation>()
                .WithMany()
                .HasForeignKey(x => x.FinancialObservationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FinancialFindingFeedback>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ActorType).IsRequired().HasMaxLength(40);
            e.Property(x => x.ActorUserId).IsRequired().HasMaxLength(450);
            e.Property(x => x.FeedbackType).IsRequired().HasMaxLength(80);
            e.Property(x => x.ReasonCode).HasMaxLength(120);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.HasIndex(x => new { x.FinancialFindingId, x.CreatedUtc });
            e.HasIndex(x => new { x.ClientProfileId, x.FeedbackType, x.CreatedUtc });
            e.HasOne<FinancialFinding>()
                .WithMany()
                .HasForeignKey(x => x.FinancialFindingId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ClientProfile>()
                .WithMany()
                .HasForeignKey(x => x.ClientProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ==========================================================
        // FINANCE TOOL STATE
        // ==========================================================
        modelBuilder.Entity<FinanceToolState>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.ToolId)
                .IsRequired()
                .HasMaxLength(100);

            e.Property(x => x.JsonState)
                .IsRequired();

            e.HasIndex(x => new { x.HouseholdAccountId, x.ToolId })
                .IsUnique();

            e.HasOne<HouseholdAccount>()
                .WithMany()
                .HasForeignKey(x => x.HouseholdAccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AgentFinanceToolState>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.AgentUserId)
                .IsRequired()
                .HasMaxLength(450);

            e.Property(x => x.ToolId)
                .IsRequired()
                .HasMaxLength(100);

            e.Property(x => x.JsonState)
                .IsRequired();

            e.HasIndex(x => new { x.AgentUserId, x.ToolId })
                .IsUnique();
        });

        // ==========================================================
        // AGENT CLIENT
        // ==========================================================
        modelBuilder.Entity<AgentClient>(e =>
        {
            e.Property(x => x.Id).HasMaxLength(450);
            e.Property(x => x.AgentUserId).HasMaxLength(450);
            e.Property(x => x.ClientUserId).HasMaxLength(450);
            e.Property(x => x.AgentUpn).HasMaxLength(320);

            // Collaboration rule: a client can be shared with multiple permitted agents.
            e.HasIndex(x => x.ClientUserId);

            // no duplicate pairs
            e.HasIndex(x => new { x.AgentUserId, x.ClientUserId }).IsUnique();

            // FK: AgentClient.ClientUserId -> ClientProfile.ClientUserId
            e.HasOne<ClientProfile>()
                .WithMany()
                .HasForeignKey(x => x.ClientUserId)
                .HasPrincipalKey(x => x.ClientUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ==========================================================
        // AGENT ASSISTANT
        // ==========================================================
        modelBuilder.Entity<AgentAssistant>(e =>
        {
            e.Property(x => x.ParentAgentUserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.AssistantUserId).HasMaxLength(450);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.FirstName).HasMaxLength(100);
            e.Property(x => x.LastName).HasMaxLength(100);

            e.HasIndex(x => x.ParentAgentUserId);
            // Null filter: multiple rows may have a NULL AssistantUserId while awaiting invite acceptance.
            if (isSqlServer)
                e.HasIndex(x => x.AssistantUserId).IsUnique().HasFilter("[AssistantUserId] IS NOT NULL");
            else
                e.HasIndex(x => x.AssistantUserId).IsUnique();
            e.HasIndex(x => new { x.ParentAgentUserId, x.Email }).IsUnique();
        });

        // ==========================================================
        // HOUSEHOLD AUTHORITY
        // ==========================================================
        modelBuilder.Entity<HouseholdAccount>(e =>
        {
            e.ToTable("HouseholdAccounts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.StatusReasonCode).HasMaxLength(120);
            e.HasIndex(x => x.SubscriptionOwnerClientProfileId).IsUnique();

            e.HasOne<ClientProfile>()
                .WithMany()
                .HasForeignKey(x => x.SubscriptionOwnerClientProfileId)
                .OnDelete(DeleteBehavior.Restrict);

            if (isSqlServer)
                e.Property(x => x.RowVersion).IsRowVersion();
            else
                e.Property(x => x.RowVersion)
                    .IsRequired()
                    .IsConcurrencyToken()
                    .HasDefaultValueSql("X''")
                    .ValueGeneratedNever();
        });

        modelBuilder.Entity<HouseholdMembership>(e =>
        {
            e.ToTable("HouseholdMemberships");
            e.HasKey(x => x.Id);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.NormalizedEmail).HasMaxLength(320).IsRequired();
            e.Property(x => x.ExternalIdentityObjectId).HasMaxLength(450);
            e.Property(x => x.StatusReasonCode).HasMaxLength(120);
            e.Property(x => x.CreatedByUserId).HasMaxLength(450);
            e.Property(x => x.UpdatedByUserId).HasMaxLength(450);

            if (isSqlServer)
            {
                e.HasIndex(x => new { x.HouseholdAccountId, x.NormalizedEmail })
                    .IsUnique()
                    .HasFilter("[Status] <> 'Removed'");
            }
            else
            {
                e.HasIndex(x => new { x.HouseholdAccountId, x.NormalizedEmail, x.Status })
                    .IsUnique();
            }
            e.HasIndex(x => new { x.HouseholdAccountId, x.Role }).IsUnique();

            if (isSqlServer)
            {
                e.HasIndex(x => x.ClientProfileId).IsUnique().HasFilter("[ClientProfileId] IS NOT NULL");
                e.HasIndex(x => x.ExternalIdentityObjectId)
                    .IsUnique()
                    .HasFilter("[ExternalIdentityObjectId] IS NOT NULL AND [Status] <> 'Removed'");
            }
            else
            {
                e.HasIndex(x => x.ClientProfileId).IsUnique();
                e.HasIndex(x => new { x.ExternalIdentityObjectId, x.Status }).IsUnique();
            }

            e.HasOne<HouseholdAccount>()
                .WithMany()
                .HasForeignKey(x => x.HouseholdAccountId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne<ClientProfile>()
                .WithMany()
                .HasForeignKey(x => x.ClientProfileId)
                .OnDelete(DeleteBehavior.Restrict);

            if (isSqlServer)
                e.Property(x => x.RowVersion).IsRowVersion();
            else
                e.Property(x => x.RowVersion)
                    .IsRequired()
                    .IsConcurrencyToken()
                    .HasDefaultValueSql("X''")
                    .ValueGeneratedNever();
        });

        modelBuilder.Entity<HouseholdMemberInvitation>(e =>
        {
            e.ToTable("HouseholdMemberInvitations");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.IntendedNormalizedEmail).HasMaxLength(320).IsRequired();
            e.Property(x => x.InvitedFirstName).HasMaxLength(100).IsRequired();
            e.Property(x => x.InvitedLastName).HasMaxLength(100).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.DeclineReasonCode).HasMaxLength(120);
            e.Property(x => x.CreatedByUserId).HasMaxLength(450).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.HouseholdAccountId, x.IntendedNormalizedEmail, x.Status });

            e.HasOne<HouseholdAccount>()
                .WithMany()
                .HasForeignKey(x => x.HouseholdAccountId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne<HouseholdMembership>()
                .WithMany()
                .HasForeignKey(x => x.HouseholdMembershipId)
                .OnDelete(DeleteBehavior.Restrict);

            if (isSqlServer)
                e.Property(x => x.RowVersion).IsRowVersion();
            else
                e.Property(x => x.RowVersion)
                    .IsRequired()
                    .IsConcurrencyToken()
                    .HasDefaultValueSql("X''")
                    .ValueGeneratedNever();
        });

        // ==========================================================
        // LEGACY HOUSEHOLD CONTACT DETAIL (non-authoritative)
        // ==========================================================
        modelBuilder.Entity<HouseholdMember>(e =>
        {
            e.Property(x => x.Id).HasMaxLength(450);
            e.Property(x => x.ClientUserId).HasMaxLength(450);
            e.Property(x => x.RelationshipType).HasMaxLength(200);

            e.HasIndex(x => x.ClientUserId);
            e.HasIndex(x => new { x.ClientUserId, x.RelationshipType }).IsUnique();

            // FK: HouseholdMember.ClientUserId -> ClientProfile.ClientUserId
            e.HasOne<ClientProfile>()
                .WithMany()
                .HasForeignKey(x => x.ClientUserId)
                .HasPrincipalKey(x => x.ClientUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ==========================================================
        // BOOKKEEPING ENTRY
        // ==========================================================
        modelBuilder.Entity<BookkeepingEntry>(e =>
        {
            e.Property(x => x.OwnerUserId)
                .HasMaxLength(450)
                .IsRequired();

            e.Property(x => x.AgentUserId)
                .HasMaxLength(450);

            e.Property(x => x.Scope)
                .HasConversion<int>()
                .IsRequired();

            e.Property(x => x.Amount).HasColumnType("decimal(18,2)");
            e.Property(x => x.Notes).HasMaxLength(240);

            e.HasIndex(x => new { x.OwnerUserId, x.Scope, x.EntryDate });

            e.HasOne(x => x.RecurringExpense)
                .WithMany()
                .HasForeignKey(x => x.RecurringExpenseId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ==========================================================
        // WEBSITE LEAD (Protect-Website opt-in leads)
        // ==========================================================
        modelBuilder.Entity<WebsiteLead>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.LeadId).IsRequired();
            e.Property(x => x.FirstName).IsRequired().HasMaxLength(120);
            e.Property(x => x.LastName).HasMaxLength(120);
            e.Property(x => x.Email).IsRequired().HasMaxLength(320);
            e.Property(x => x.Phone).HasMaxLength(80);
            e.Property(x => x.PreferredContactMethod).HasMaxLength(60);
            e.Property(x => x.InterestType).HasMaxLength(120);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.SourcePageKey).HasMaxLength(120);
            e.Property(x => x.SourceCtaKey).HasMaxLength(160);
            e.Property(x => x.UtmSource).HasMaxLength(160);
            e.Property(x => x.UtmMedium).HasMaxLength(160);
            e.Property(x => x.UtmCampaign).HasMaxLength(160);
            e.Property(x => x.UtmId).HasMaxLength(160);
            e.Property(x => x.MetaCampaignId).HasMaxLength(200);
            e.Property(x => x.MetaAdSetId).HasMaxLength(200);
            e.Property(x => x.MetaAdId).HasMaxLength(200);
            e.Property(x => x.Fbclid).HasMaxLength(120);
            e.Property(x => x.SessionId).HasMaxLength(120);
            e.Property(x => x.VisitorId).HasMaxLength(120);
            e.Property(x => x.Environment).HasMaxLength(40);
            e.Property(x => x.Host).HasMaxLength(160);
            e.Property(x => x.Status).HasMaxLength(40).IsRequired();
            e.Property(x => x.MetadataJson).HasColumnType(isSqlServer ? "nvarchar(max)" : "TEXT");
            e.Property(x => x.IsDeleted).HasDefaultValue(false);
            e.Property(x => x.DeletedByUserId).HasMaxLength(200);
            e.Property(x => x.DeleteReason).HasMaxLength(500);
            e.Property(x => x.CreatedUtc).IsRequired();
            e.Property(x => x.AgentSlug).HasMaxLength(200);

            e.HasIndex(x => x.CreatedUtc);
            e.HasIndex(x => x.SourcePageKey);
            e.HasIndex(x => x.SourceCtaKey);
            e.HasIndex(x => x.InterestType);
            e.HasIndex(x => x.Email);
            e.HasIndex(x => x.UtmId);
            e.HasIndex(x => x.MetaCampaignId);
            e.HasIndex(x => x.AgentTrackingProfileId);
            e.HasIndex(x => x.AgentSlug);
        });

        // ==========================================================
        // RECURRING EXPENSE
        // ==========================================================
        modelBuilder.Entity<RecurringExpense>(e =>
        {
            e.Property(x => x.OwnerUserId)
                .HasMaxLength(450)
                .IsRequired();

            e.Property(x => x.AgentUserId)
                .HasMaxLength(450);

            e.Property(x => x.Scope)
                .HasConversion<int>()
                .IsRequired();

            e.Property(x => x.Name)
                .HasMaxLength(120)
                .IsRequired();

            e.Property(x => x.Amount).HasColumnType("decimal(18,2)");
            e.Property(x => x.Notes).HasMaxLength(240);

            e.HasIndex(x => new { x.OwnerUserId, x.Scope, x.IsActive });
        });

        // ==========================================================
        // PRODUCTION RECORDS
        // ==========================================================
        modelBuilder.Entity<ProductionRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AgentUserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.LeadId).HasMaxLength(128);
            e.Property(x => x.ClientUserId).HasMaxLength(450);
            e.Property(x => x.Amount).HasColumnType("decimal(18,2)");
            e.Property(x => x.PersonalAmount).HasColumnType("decimal(18,2)");
            e.Property(x => x.Notes).HasMaxLength(240);

            e.HasIndex(x => x.AgentUserId);
            e.HasIndex(x => new { x.AgentUserId, x.Side });
            e.HasIndex(x => x.LeadId);
            e.HasIndex(x => x.ClientUserId);
            e.HasIndex(x => x.Status);

            if (isSqlServer)
                e.Property(x => x.RowVersion).IsRowVersion();
            else
                e.Property(x => x.RowVersion)
                    .IsRequired()
                    .IsConcurrencyToken()
                    .HasDefaultValueSql("X''")
                    .ValueGeneratedNever();
        });
    }
}
