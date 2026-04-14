using System;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data;

public class MasterAppDbContext : DbContext
{
    public MasterAppDbContext(DbContextOptions<MasterAppDbContext> options) : base(options) { }

    public DbSet<ClientProfile> ClientProfiles => Set<ClientProfile>();
    public DbSet<AgentClient> AgentClients => Set<AgentClient>();
    public DbSet<AgentAssistant> AgentAssistants => Set<AgentAssistant>();
    public DbSet<HouseholdMember> HouseholdMembers => Set<HouseholdMember>();
    public DbSet<FinanceToolState> FinanceToolStates => Set<FinanceToolState>();
    public DbSet<BookkeepingEntry> BookkeepingEntries => Set<BookkeepingEntry>();
    public DbSet<RecurringExpense> RecurringExpenses => Set<RecurringExpense>();
public DbSet<WorkstationLeadProfile> WorkstationLeadProfiles => Set<WorkstationLeadProfile>();
    public DbSet<Proposal> Proposals => Set<Proposal>();
    public DbSet<UnderwritingRecord> UnderwritingRecords => Set<UnderwritingRecord>();
    public DbSet<OnboardingInvite> OnboardingInvites => Set<OnboardingInvite>();
    public DbSet<OnboardingSubmission> OnboardingSubmissions => Set<OnboardingSubmission>();
    public DbSet<AgentProfile> AgentProfiles => Set<AgentProfile>();
    public DbSet<ProductionRecord> ProductionRecords => Set<ProductionRecord>();
    public DbSet<WebsiteLead> WebsiteLeads => Set<WebsiteLead>();
    public DbSet<AnalyticsEvent> AnalyticsEvents => Set<AnalyticsEvent>();
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

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
        var isSqlServer = Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true;

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

            if (isSqlServer)
                e.HasIndex(x => x.NormalizedEmail).IsUnique().HasFilter("[NormalizedEmail] IS NOT NULL");
            else
                e.HasIndex(x => x.NormalizedEmail).IsUnique();

            if (isSqlServer)
                e.HasIndex(x => x.AgentUserId).IsUnique().HasFilter("[AgentUserId] IS NOT NULL");
            else
                e.HasIndex(x => x.AgentUserId).IsUnique();
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
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.NormalizedEmail).HasMaxLength(320);

            if (isSqlServer)
                e.HasIndex(x => x.NormalizedEmail).IsUnique().HasFilter("[NormalizedEmail] IS NOT NULL");
            else
                e.HasIndex(x => x.NormalizedEmail).IsUnique();

            e.Property(x => x.RowVersion).IsRowVersion();
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

            if (isSqlServer)
                e.HasIndex(x => x.ClientId).IsUnique().HasFilter("[IsDeleted] = 0");
            else
                e.HasIndex(x => new { x.ClientId, x.IsDeleted }).IsUnique();
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
            e.Property(x => x.Fbclid).HasMaxLength(120);
            e.Property(x => x.Environment).HasMaxLength(40);
            e.Property(x => x.Host).HasMaxLength(160);
            e.Property(x => x.SubmitOutcome).HasMaxLength(40);
            e.Property(x => x.MetadataJson).HasColumnType(isSqlServer ? "nvarchar(max)" : "TEXT");
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
            e.Property(x => x.ScreenWidth);
            e.Property(x => x.ScreenHeight);
            e.Property(x => x.ViewportWidth);
            e.Property(x => x.ViewportHeight);
            e.Property(x => x.ScrollPercent);
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

            e.Property(x => x.RowVersion).IsRowVersion();
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

            e.HasIndex(x => new { x.ClientProfileId, x.ToolId })
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
        // AGENT PROFILE (per-agent stored settings like NPN)
        // ==========================================================
        modelBuilder.Entity<AgentProfile>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.AgentUserId)
                .HasMaxLength(450)
                .IsRequired();

            e.Property(x => x.AgentUpn)
                .HasMaxLength(320);

            e.Property(x => x.FullName)
                .HasMaxLength(200);

            e.Property(x => x.Title)
                .HasMaxLength(120);

            e.Property(x => x.Npn)
                .HasMaxLength(64);

            e.Property(x => x.Phone)
                .HasMaxLength(64);

            e.Property(x => x.DisplayOrder);

            e.HasIndex(x => x.AgentUserId).IsUnique();
            e.HasIndex(x => x.AgentUpn);
        });

        // ==========================================================
        // HOUSEHOLD MEMBER
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
            e.Property(x => x.Fbclid).HasMaxLength(120);
            e.Property(x => x.SessionId).HasMaxLength(120);
            e.Property(x => x.VisitorId).HasMaxLength(120);
            e.Property(x => x.Environment).HasMaxLength(40);
            e.Property(x => x.Host).HasMaxLength(160);
            e.Property(x => x.Status).HasMaxLength(40).IsRequired();
            e.Property(x => x.MetadataJson).HasColumnType(isSqlServer ? "nvarchar(max)" : "TEXT");
            e.Property(x => x.CreatedUtc).IsRequired();
            e.Property(x => x.AgentSlug).HasMaxLength(200);

            e.HasIndex(x => x.CreatedUtc);
            e.HasIndex(x => x.SourcePageKey);
            e.HasIndex(x => x.SourceCtaKey);
            e.HasIndex(x => x.InterestType);
            e.HasIndex(x => x.Email);
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

            e.Property(x => x.RowVersion).IsRowVersion();
        });
    }
}
