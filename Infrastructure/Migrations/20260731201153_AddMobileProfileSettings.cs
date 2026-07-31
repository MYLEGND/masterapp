using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMobileProfileSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActionItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RelatedEntityType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    RelatedEntityId = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    EffectiveAgentOid = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    DueDateUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ActionSurface = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ActionCategory = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    PipelineStage = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    IsEscalated = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Priority = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    DecisionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BlockerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    SourceRef = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DismissedReason = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ActionLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    Verb = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentAssistants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentAgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    AssistantUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    FirstName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    InvitedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentAssistants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentFinanceToolStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    ToolId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    JsonState = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentFinanceToolStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    AgentUpn = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    ProfileImageContent = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ProfileImageContentType = table.Column<string>(type: "TEXT", maxLength: 127, nullable: true),
                    FullName = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Npn = table.Column<string>(type: "TEXT", nullable: true),
                    Phone = table.Column<string>(type: "TEXT", nullable: true),
                    ShortBio = table.Column<string>(type: "TEXT", maxLength: 280, nullable: true),
                    MetaPixelId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    MetaAccessToken = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    MetaTestEventCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    BookingEnabled = table.Column<bool>(type: "INTEGER", nullable: true),
                    MicrosoftBookingsEmbedUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    FallbackBookingUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    BookingPageIdOrMailbox = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    CalendarUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    CalendarEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    PreferModalOnMobile = table.Column<bool>(type: "INTEGER", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    DeactivatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeactivationReason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    DisplayOrder = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentTrackingProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    AgentUpn = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    PreferredEnvironment = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTrackingProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentZoomLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentUserId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentZoomLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AnalyticsDriftAlerts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IncidentKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    MetricKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MetricUnit = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CurrentValue = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    BaselineValue = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    DeviationPercent = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    ScopeKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    WindowStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    WindowEndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FirstDetectedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastDetectedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ObservedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastNotifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DetailsJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalyticsDriftAlerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AnalyticsEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientEventId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    PageKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    SectionKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    ElementKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    ButtonLabel = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    FormKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    QuoteType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Path = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Referrer = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    VisitorId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    UtmSource = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    UtmMedium = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    UtmCampaign = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    UtmId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    IsInternal = table.Column<bool>(type: "INTEGER", nullable: false),
                    Environment = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Host = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    AgentTrackingProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AgentSlug = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    EventUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SubmitOutcome = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: true),
                    SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    TrackingVersion = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    ReferrerHost = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DeviceType = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Browser = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    OperatingSystem = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    TimeZone = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Language = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    ScreenWidth = table.Column<int>(type: "INTEGER", nullable: true),
                    ScreenHeight = table.Column<int>(type: "INTEGER", nullable: true),
                    ViewportWidth = table.Column<int>(type: "INTEGER", nullable: true),
                    ViewportHeight = table.Column<int>(type: "INTEGER", nullable: true),
                    ScrollPercent = table.Column<int>(type: "INTEGER", nullable: true),
                    DwellMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    EngagedMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    IsBounceCandidate = table.Column<bool>(type: "INTEGER", nullable: true),
                    IsExitPage = table.Column<bool>(type: "INTEGER", nullable: true),
                    UtmTerm = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    UtmContent = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    MetaCampaignId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    MetaCampaignName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    MetaAdSetId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    MetaAdSetName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    MetaAdId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    MetaAdName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Placement = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    FormId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    FieldName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    ElementId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    Fbclid = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    WebDriver = table.Column<bool>(type: "INTEGER", nullable: true),
                    IsHeadless = table.Column<bool>(type: "INTEGER", nullable: true),
                    MouseMoveCount = table.Column<int>(type: "INTEGER", nullable: true),
                    HumanInteractionCount = table.Column<int>(type: "INTEGER", nullable: true),
                    VisibilityChangeCount = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalyticsEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppointmentSyncLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    WorkstationLeadId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ClientProfileId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    AgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    CalendarUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    CalendarEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    GraphSubscriptionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    GraphEventId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    DiagnosticJson = table.Column<string>(type: "text", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentSyncLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BillingAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    PreviousStatus = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    NewStatus = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    ActorType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ReasonCode = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SanitizedMetadataJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BillingProviderEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ProviderEnvironment = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ProviderEventId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    ProviderObjectId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    ReceivedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SignatureValidatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ProcessingStatus = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ProcessedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RetryUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SafeErrorCode = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    PayloadHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RetainedPayloadJson = table.Column<string>(type: "text", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false, defaultValueSql: "X''")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingProviderEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Blockers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RelatedEntityType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    RelatedEntityId = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    BlockerType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    BlockerReason = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    BlockerOwnerType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    BlockerOwnerId = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    UnblockDueDateUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResolvedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 800, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Blockers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientAgentMessagingGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    AgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    GrantedByAgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    GrantedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientAgentMessagingGrants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientUserId = table.Column<string>(type: "TEXT", nullable: false),
                    ExternalIdentityObjectId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    FirstName = table.Column<string>(type: "TEXT", nullable: false),
                    LastName = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    Phone = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileImageContent = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ProfileImageContentType = table.Column<string>(type: "TEXT", maxLength: 127, nullable: true),
                    DOB = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MaritalStatus = table.Column<string>(type: "TEXT", nullable: false),
                    SignificantOtherFirstName = table.Column<string>(type: "TEXT", nullable: true),
                    SignificantOtherLastName = table.Column<string>(type: "TEXT", nullable: true),
                    SignificantOtherDOB = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SignificantOtherEmail = table.Column<string>(type: "TEXT", nullable: true),
                    SignificantOtherPhone = table.Column<string>(type: "TEXT", nullable: true),
                    AgentNotes = table.Column<string>(type: "TEXT", nullable: false),
                    CrmStatus = table.Column<string>(type: "TEXT", nullable: true),
                    CrmPriority = table.Column<string>(type: "TEXT", nullable: true),
                    CrmLastTouch = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CrmNextDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CrmNextText = table.Column<string>(type: "TEXT", nullable: true),
                    CrmTags = table.Column<string>(type: "TEXT", nullable: true),
                    CrmNotes = table.Column<string>(type: "TEXT", nullable: true),
                    AccountManagementMode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "SharedAccount"),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false, defaultValueSql: "X''")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientProfiles", x => x.Id);
                    table.UniqueConstraint("AK_ClientProfiles_ClientUserId", x => x.ClientUserId);
                });

            migrationBuilder.CreateTable(
                name: "CommerceBusinesses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    LegalName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    BusinessType = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    PrimaryDomain = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    OwnerEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceBusinesses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Commitments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RelatedEntityType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    RelatedEntityId = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    PromisedByType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    PromisedById = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    PromisedToType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    PromisedToId = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    PromiseText = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    DueDateUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LinkedActionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FulfilledAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Commitments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DecisionRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RelatedEntityType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    RelatedEntityId = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Rationale = table.Column<string>(type: "TEXT", nullable: false),
                    RecommendationType = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DecisionRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FinanceToolStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToolId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    JsonState = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinanceToolStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GraphCalendarSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    CalendarUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    CalendarEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    GraphSubscriptionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Resource = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ChangeType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ClientState = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ExpirationUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastRenewedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastWebhookUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphCalendarSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JourneyCircleBlocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BlockerClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BlockedClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JourneyCircleBlocks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JourneyCircleConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConnectionKey = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    RequesterClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipientClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ConnectionReason = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    Introduction = table.Column<string>(type: "TEXT", maxLength: 600, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RespondedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JourneyCircleConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JourneyCircleModerationEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Surface = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    PolicyVersion = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RequiresReview = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JourneyCircleModerationEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JourneyCircleReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReporterClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReportedClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 600, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JourneyCircleReports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MessageConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    DirectConversationKey = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 240, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastMessageUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsClosed = table.Column<bool>(type: "INTEGER", nullable: false),
                    ClosedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageConversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MessagingAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    InternalMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessagingAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MetaSignalEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    EventName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    EventCategory = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    VisitorId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    LeadId = table.Column<Guid>(type: "TEXT", nullable: true),
                    QuoteType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    PageKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    EffectivePageKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    PageVariant = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    PageMode = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    TrafficType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    FunnelStep = table.Column<int>(type: "INTEGER", nullable: true),
                    StepName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    IntentScore = table.Column<int>(type: "INTEGER", nullable: false),
                    EngagementScore = table.Column<int>(type: "INTEGER", nullable: false),
                    QualificationScore = table.Column<int>(type: "INTEGER", nullable: false),
                    FrictionScore = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalSignalScore = table.Column<int>(type: "INTEGER", nullable: false),
                    ScoreTier = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    MetaBrowserSent = table.Column<bool>(type: "INTEGER", nullable: false),
                    MetaServerSent = table.Column<bool>(type: "INTEGER", nullable: false),
                    MetaDeduplicationKey = table.Column<string>(type: "TEXT", maxLength: 220, nullable: true),
                    UtmSource = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    UtmMedium = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    UtmCampaign = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    UtmId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    UtmContent = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    FbclidPresent = table.Column<bool>(type: "INTEGER", nullable: false),
                    FbcPresent = table.Column<bool>(type: "INTEGER", nullable: false),
                    FbpPresent = table.Column<bool>(type: "INTEGER", nullable: false),
                    Referrer = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    UserAgentHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    IpHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    AgentTrackingProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AgentSlug = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Environment = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Host = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: true),
                    DeviceType = table.Column<string>(type: "TEXT", nullable: true),
                    Browser = table.Column<string>(type: "TEXT", nullable: true),
                    OperatingSystem = table.Column<string>(type: "TEXT", nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", nullable: true),
                    ViewportWidth = table.Column<int>(type: "INTEGER", nullable: true),
                    ViewportHeight = table.Column<int>(type: "INTEGER", nullable: true),
                    ScreenWidth = table.Column<int>(type: "INTEGER", nullable: true),
                    ScreenHeight = table.Column<int>(type: "INTEGER", nullable: true),
                    WebDriver = table.Column<bool>(type: "INTEGER", nullable: true),
                    IsHeadless = table.Column<bool>(type: "INTEGER", nullable: true),
                    MouseMoveCount = table.Column<int>(type: "INTEGER", nullable: true),
                    HumanInteractionCount = table.Column<int>(type: "INTEGER", nullable: true),
                    VisibilityChangeCount = table.Column<int>(type: "INTEGER", nullable: true),
                    Language = table.Column<string>(type: "TEXT", nullable: true),
                    TimeZone = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaSignalEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MobileProfileSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParticipantType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    NormalizedUsername = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Bio = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Website = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Location = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    Pronouns = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    PublicEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    IsEmailVisible = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MobileProfileSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OnboardingInvites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    FirstName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    LastName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    RoleType = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SubmittedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnboardingInvites", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlaybookExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExecutionKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybookExecutions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProductionRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Side = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PersonalAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    LeadId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ClientUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 240, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false, defaultValueSql: "X''")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Proposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LeadId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    LeadName = table.Column<string>(type: "TEXT", maxLength: 240, nullable: true),
                    AgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    BucketsJson = table.Column<string>(type: "TEXT", nullable: false),
                    QueueKey = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    ScopeKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LeadKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PageTitle = table.Column<string>(type: "TEXT", maxLength: 240, nullable: true),
                    IsDraft = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Proposals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecurringExpenses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Scope = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Frequency = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<int>(type: "INTEGER", nullable: false),
                    StartDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NextDueDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 240, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringExpenses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SocialFollows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FollowerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    FollowerParticipantType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    FollowedUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    FollowedParticipantType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SourceSocialPostId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialFollows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SocialPosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AuthorUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    AuthorParticipantType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    AuthorProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Audience = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    RepostOfSocialPostId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    Location = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CommentsEnabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    PostedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPosts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SocialProfileVisits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    TargetParticipantType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    VisitorUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    VisitorParticipantType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SourceSocialPostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FirstVisitedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastVisitedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialProfileVisits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UnderwritingRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LeadId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LeadName = table.Column<string>(type: "TEXT", maxLength: 240, nullable: true),
                    AgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProductCode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    QueueKey = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    ScopeKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PageTitle = table.Column<string>(type: "TEXT", maxLength: 240, nullable: true),
                    IsDraft = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnderwritingRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebsiteLeads",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LeadId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FirstName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    LastName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    PreferredContactMethod = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    InterestType = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    SourcePageKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    SourceCtaKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    UtmSource = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    UtmMedium = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    UtmCampaign = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    UtmId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    MetaCampaignId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    MetaAdSetId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    MetaAdId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    VisitorId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    MarketingEmailConsent = table.Column<bool>(type: "INTEGER", nullable: false),
                    CallTextConsent = table.Column<bool>(type: "INTEGER", nullable: false),
                    TermsAccepted = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsInternal = table.Column<bool>(type: "INTEGER", nullable: false),
                    Environment = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Host = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    AgentTrackingProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AgentSlug = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedByUserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DeleteReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Fbclid = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    ClientIpAddress = table.Column<string>(type: "TEXT", nullable: true),
                    ClientUserAgent = table.Column<string>(type: "TEXT", nullable: true),
                    Fbp = table.Column<string>(type: "TEXT", nullable: true),
                    Fbc = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebsiteLeads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkstationLeadProfiles",
                columns: table => new
                {
                    LeadId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Bucket = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    OriginalLeadType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    FirstName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    LastName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Phone2 = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    AddressLine = table.Column<string>(type: "TEXT", maxLength: 240, nullable: true),
                    City = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    County = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    ZipCode = table.Column<string>(type: "TEXT", maxLength: 24, nullable: true),
                    Age = table.Column<string>(type: "TEXT", maxLength: 12, nullable: true),
                    DOB = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Gender = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    MortgageLender = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    LoanAmount = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    Btc = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    CrmStatus = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    CrmStage = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    CrmOrder = table.Column<long>(type: "INTEGER", nullable: false),
                    CrmNotes = table.Column<string>(type: "text", nullable: true),
                    CallCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CallsToday = table.Column<int>(type: "INTEGER", nullable: false),
                    CallsWeek = table.Column<int>(type: "INTEGER", nullable: false),
                    CallsMonth = table.Column<int>(type: "INTEGER", nullable: false),
                    CallsYear = table.Column<int>(type: "INTEGER", nullable: false),
                    CallsTodayDateUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CallsWeekStartUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CallsMonthStartUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CallsYearStartUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false, defaultValueSql: "X''")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkstationLeadProfiles", x => x.LeadId);
                });

            migrationBuilder.CreateTable(
                name: "AgentTrackingAliases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentTrackingProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsCanonical = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTrackingAliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentTrackingAliases_AgentTrackingProfiles_AgentTrackingProfileId",
                        column: x => x.AgentTrackingProfileId,
                        principalTable: "AgentTrackingProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentClients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", maxLength: 450, nullable: false),
                    AgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    ClientUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    AgentUpn = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentClients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentClients_ClientProfiles_ClientUserId",
                        column: x => x.ClientUserId,
                        principalTable: "ClientProfiles",
                        principalColumn: "ClientUserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientEntitlements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntitlementKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    EffectiveUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExpirationUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    GraceOrSuspensionUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReasonCode = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false, defaultValueSql: "X''")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientEntitlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientEntitlements_ClientProfiles_ClientProfileId",
                        column: x => x.ClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientFinancialIntelligenceProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    DataCompletenessScore = table.Column<decimal>(type: "TEXT", precision: 5, scale: 4, nullable: false),
                    BehavioralBaselineStatus = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    PersonalizationMaturity = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    RecommendationResponseSummary = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    CurrentRiskSummary = table.Column<string>(type: "TEXT", maxLength: 600, nullable: false),
                    CurrentOpportunitySummary = table.Column<string>(type: "TEXT", maxLength: 600, nullable: false),
                    CurrentLeakageSummary = table.Column<string>(type: "TEXT", maxLength: 600, nullable: false),
                    EvaluationSequence = table.Column<int>(type: "INTEGER", nullable: false),
                    LastEvaluatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientFinancialIntelligenceProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientFinancialIntelligenceProfiles_ClientProfiles_ClientProfileId",
                        column: x => x.ClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClientFinancialPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JsonData = table.Column<string>(type: "TEXT", nullable: false),
                    LastUpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientFinancialPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientFinancialPlans_ClientProfiles_ClientId",
                        column: x => x.ClientId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientPaymentMethods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ProviderEnvironment = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ProviderPaymentMethodId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    CardBrand = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Last4 = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    ExpirationMonth = table.Column<int>(type: "INTEGER", nullable: true),
                    ExpirationYear = table.Column<int>(type: "INTEGER", nullable: true),
                    CardholderName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    BillingAddressLine1 = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    BillingAddressLine2 = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    BillingCity = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    BillingState = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    BillingPostalCode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    BillingCountryCode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RetiredUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false, defaultValueSql: "X''")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientPaymentMethods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientPaymentMethods_ClientProfiles_ClientProfileId",
                        column: x => x.ClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientSubscriptionOffers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerAgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    PriceType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MonthlyAmountCents = table.Column<int>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    BillingAnchorSelectionMode = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SelectedBillingAnchorDay = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    EffectiveUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false, defaultValueSql: "X''")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientSubscriptionOffers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientSubscriptionOffers_ClientProfiles_ClientProfileId",
                        column: x => x.ClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FinancialDataConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ProviderItemId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ProviderInstitutionId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    EncryptedAccessToken = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    SyncCursor = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    LastSyncStartedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncCompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastWebhookUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialDataConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FinancialDataConnections_ClientProfiles_ClientProfileId",
                        column: x => x.ClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FinancialFindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FindingKey = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    RuleIdentifier = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    RuleVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    FindingType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Explanation = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    EstimatedImpact = table.Column<decimal>(type: "TEXT", precision: 19, scale: 4, nullable: true),
                    ImpactUnit = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    Confidence = table.Column<decimal>(type: "TEXT", precision: 5, scale: 4, nullable: false),
                    PriorityScore = table.Column<decimal>(type: "TEXT", precision: 6, scale: 2, nullable: false),
                    Urgency = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Difficulty = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    EvidenceSummary = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    ClientFacingSummary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    AgentFacingSummary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Disclaimer = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    RequiresAgentReview = table.Column<bool>(type: "INTEGER", nullable: false),
                    AgentReviewedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AgentReviewedByUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    FirstDetectedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastDetectedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FinancialFindings_ClientProfiles_ClientProfileId",
                        column: x => x.ClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FinancialObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ObservationKey = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    RuleIdentifier = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    RuleVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ObservationType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SourceReference = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    PeriodStartUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PeriodEndUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NumericValue = table.Column<decimal>(type: "TEXT", precision: 19, scale: 4, nullable: true),
                    PreviousValue = table.Column<decimal>(type: "TEXT", precision: 19, scale: 4, nullable: true),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    Confidence = table.Column<decimal>(type: "TEXT", precision: 5, scale: 4, nullable: false),
                    EvidenceSummary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SupersededUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialObservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FinancialObservations_ClientProfiles_ClientProfileId",
                        column: x => x.ClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HouseholdMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", maxLength: 450, nullable: false),
                    ClientUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    RelationshipType = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FirstName = table.Column<string>(type: "TEXT", nullable: false),
                    LastName = table.Column<string>(type: "TEXT", nullable: false),
                    DOB = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    Phone = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HouseholdMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HouseholdMembers_ClientProfiles_ClientUserId",
                        column: x => x.ClientUserId,
                        principalTable: "ClientProfiles",
                        principalColumn: "ClientUserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JourneyCircleProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsOptedIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDiscoverable = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowSuggestions = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowConnectionRequests = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LifeStage = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    LocationLabel = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Introduction = table.Column<string>(type: "TEXT", maxLength: 600, nullable: true),
                    GoalsJson = table.Column<string>(type: "TEXT", nullable: false),
                    InterestsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CircleCodesJson = table.Column<string>(type: "TEXT", nullable: false),
                    ConnectionTypesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CommunicationStyle = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    AccountabilityFrequency = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    CommunityAccessState = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    ConsentAffirmedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JourneyCircleProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JourneyCircleProfiles_ClientProfiles_ClientProfileId",
                        column: x => x.ClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommerceBusinessMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CommerceBusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    RoleKey = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CanManageStorefront = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanManageCatalog = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanManageOrders = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanManageAnalytics = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanManageTeam = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceBusinessMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceBusinessMembers_CommerceBusinesses_CommerceBusinessId",
                        column: x => x.CommerceBusinessId,
                        principalTable: "CommerceBusinesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommerceBusinessSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CommerceBusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ShippingFeeCents = table.Column<int>(type: "INTEGER", nullable: false),
                    TaxPercent = table.Column<decimal>(type: "TEXT", precision: 9, scale: 4, nullable: false),
                    GlobalDiscountCode = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    GlobalDiscountType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    GlobalDiscountAmount = table.Column<decimal>(type: "TEXT", precision: 9, scale: 4, nullable: false),
                    GlobalDiscountIsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceBusinessSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceBusinessSettings_CommerceBusinesses_CommerceBusinessId",
                        column: x => x.CommerceBusinessId,
                        principalTable: "CommerceBusinesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommerceBusinessStorefrontSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CommerceBusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BrandHeadline = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    BrandSubheadline = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    AccentColor = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    LogoUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    StorefrontStatus = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceBusinessStorefrontSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceBusinessStorefrontSettings_CommerceBusinesses_CommerceBusinessId",
                        column: x => x.CommerceBusinessId,
                        principalTable: "CommerceBusinesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommerceBusinessSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CommerceBusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlanKey = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    PlanName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    MonthlyPriceCents = table.Column<int>(type: "INTEGER", nullable: false),
                    BillingProvider = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    BillingCustomerId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    BillingSubscriptionId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    TrialEndsUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CurrentPeriodEndsUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceBusinessSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceBusinessSubscriptions_CommerceBusinesses_CommerceBusinessId",
                        column: x => x.CommerceBusinessId,
                        principalTable: "CommerceBusinesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommerceOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CommerceBusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrderNumber = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PaidUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ShippedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FulfilledUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    PaymentStatus = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    FulfillmentStatus = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ReturnStatus = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CheckoutAttemptId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    IsPaymentProcessing = table.Column<bool>(type: "INTEGER", nullable: false),
                    PaymentProcessingStartedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SquarePaymentId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    SquareError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    TrackingCarrier = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    TrackingNumber = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    AdminNotes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    FirstName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    LastName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    AddressLine1 = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    AddressLine2 = table.Column<string>(type: "TEXT", maxLength: 240, nullable: true),
                    City = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    PostalCode = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    RequestIp = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    SubtotalCents = table.Column<int>(type: "INTEGER", nullable: false),
                    DiscountCode = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    DiscountLabel = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    DiscountCents = table.Column<int>(type: "INTEGER", nullable: false),
                    RefundedCents = table.Column<int>(type: "INTEGER", nullable: false),
                    ShippingCents = table.Column<int>(type: "INTEGER", nullable: false),
                    TaxCents = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalCents = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceOrders_CommerceBusinesses_CommerceBusinessId",
                        column: x => x.CommerceBusinessId,
                        principalTable: "CommerceBusinesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommerceProducts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CommerceBusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExternalProductKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    PriceLabel = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Badge = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    PriceCents = table.Column<int>(type: "INTEGER", nullable: false),
                    CompareAtPriceCents = table.Column<int>(type: "INTEGER", nullable: false),
                    IsFeatured = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisplayOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceProducts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceProducts_CommerceBusinesses_CommerceBusinessId",
                        column: x => x.CommerceBusinessId,
                        principalTable: "CommerceBusinesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InternalMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SenderUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    SenderType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 10000, nullable: false),
                    SentUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EditedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    ClientMessageId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ReplyToMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InternalMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InternalMessages_InternalMessages_ReplyToMessageId",
                        column: x => x.ReplyToMessageId,
                        principalTable: "InternalMessages",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_InternalMessages_MessageConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "MessageConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MessageConversationParticipants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    ParticipantType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    JoinedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LeftUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastReadUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastReadMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsMuted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageConversationParticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageConversationParticipants_MessageConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "MessageConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OnboardingSubmissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InviteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SubmittedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FirstName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    MiddleName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    LastName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    PreferredName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    DateOfBirth = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CurrentAddress = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    City = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Zip = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    MailingAddress = table.Column<string>(type: "TEXT", maxLength: 240, nullable: true),
                    EmergencyContactName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    EmergencyContactPhone = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    EmergencyContactRelationship = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    RoleType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    JobTitle = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Department = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    Manager = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    StartDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    WorkState = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    WorkLocation = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    EmploymentType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    PayType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    WorkNotes = table.Column<string>(type: "text", nullable: true),
                    LegalNameConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    SsnLast4 = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    SsnNote = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    DriverLicenseNumber = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    DriverLicenseState = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    WorkAuthorizationStatus = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    CitizenshipStatus = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    EligibilityDocumentsAck = table.Column<bool>(type: "INTEGER", nullable: false),
                    TaxFilingStatus = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    FederalWithholding = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    StateWithholding = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    BankName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    BankAccountType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    BankRoutingNumber = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    BankAccountNumber = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    PayrollAcknowledgement = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConfidentialityAck = table.Column<bool>(type: "INTEGER", nullable: false),
                    HandbookAck = table.Column<bool>(type: "INTEGER", nullable: false),
                    TechnologyAck = table.Column<bool>(type: "INTEGER", nullable: false),
                    ComplianceAck = table.Column<bool>(type: "INTEGER", nullable: false),
                    CompensationAck = table.Column<bool>(type: "INTEGER", nullable: false),
                    NonSolicitAck = table.Column<bool>(type: "INTEGER", nullable: false),
                    ElectronicSignatureAck = table.Column<bool>(type: "INTEGER", nullable: false),
                    ElectronicSignatureName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ElectronicSignatureDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResidentStateLicense = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    NonResidentStates = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    LicensesHeld = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    LicenseNumbers = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    CarrierAppointments = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    EOCoverage = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    SupervisionNotes = table.Column<string>(type: "text", nullable: true),
                    HasRegulatoryIssues = table.Column<bool>(type: "INTEGER", nullable: true),
                    RegulatoryExplanation = table.Column<string>(type: "text", nullable: true),
                    HasCriminalHistory = table.Column<bool>(type: "INTEGER", nullable: true),
                    CriminalExplanation = table.Column<string>(type: "text", nullable: true),
                    HasAdministrativeActions = table.Column<bool>(type: "INTEGER", nullable: true),
                    AdministrativeExplanation = table.Column<string>(type: "text", nullable: true),
                    HasPriorTermination = table.Column<bool>(type: "INTEGER", nullable: true),
                    TerminationExplanation = table.Column<string>(type: "text", nullable: true),
                    HasOtherDisclosures = table.Column<bool>(type: "INTEGER", nullable: true),
                    OtherDisclosuresExplanation = table.Column<string>(type: "text", nullable: true),
                    HasIdDocument = table.Column<bool>(type: "INTEGER", nullable: true),
                    HasSsnDocument = table.Column<bool>(type: "INTEGER", nullable: true),
                    HasVoidedCheck = table.Column<bool>(type: "INTEGER", nullable: true),
                    HasLicenseCopy = table.Column<bool>(type: "INTEGER", nullable: true),
                    HasCertifications = table.Column<bool>(type: "INTEGER", nullable: true),
                    HasResume = table.Column<bool>(type: "INTEGER", nullable: true),
                    HasSignedAgreements = table.Column<bool>(type: "INTEGER", nullable: true),
                    DocumentNotes = table.Column<string>(type: "text", nullable: true),
                    CertificationTruthful = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnboardingSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnboardingSubmissions_OnboardingInvites_InviteId",
                        column: x => x.InviteId,
                        principalTable: "OnboardingInvites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BookkeepingEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Scope = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    EntryDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Category = table.Column<int>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 240, nullable: true),
                    RecurringExpenseId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookkeepingEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookkeepingEntries_RecurringExpenses_RecurringExpenseId",
                        column: x => x.RecurringExpenseId,
                        principalTable: "RecurringExpenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SocialPostComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SocialPostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentCommentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AuthorUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    AuthorParticipantType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    AuthorProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPostComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialPostComments_SocialPostComments_ParentCommentId",
                        column: x => x.ParentCommentId,
                        principalTable: "SocialPostComments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SocialPostComments_SocialPosts_SocialPostId",
                        column: x => x.SocialPostId,
                        principalTable: "SocialPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SocialPostMediaAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SocialPostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaKind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    StorageKey = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ThumbnailStorageKey = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    MimeType = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: true),
                    Height = table.Column<int>(type: "INTEGER", nullable: true),
                    AspectRatio = table.Column<decimal>(type: "TEXT", precision: 12, scale: 6, nullable: true),
                    DurationSeconds = table.Column<decimal>(type: "TEXT", precision: 12, scale: 3, nullable: true),
                    ProcessingState = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    AccessibilityText = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPostMediaAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialPostMediaAssets_SocialPosts_SocialPostId",
                        column: x => x.SocialPostId,
                        principalTable: "SocialPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SocialPostMusicAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SocialPostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ProviderTrackId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TrackTitle = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    ArtistName = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    TrackDurationSeconds = table.Column<decimal>(type: "TEXT", precision: 12, scale: 3, nullable: false),
                    PreviewUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    TrimStartSeconds = table.Column<decimal>(type: "TEXT", precision: 12, scale: 3, nullable: false),
                    TrimEndSeconds = table.Column<decimal>(type: "TEXT", precision: 12, scale: 3, nullable: false),
                    MusicVolume = table.Column<decimal>(type: "TEXT", precision: 5, scale: 2, nullable: false),
                    OriginalAudioVolume = table.Column<decimal>(type: "TEXT", precision: 5, scale: 2, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPostMusicAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialPostMusicAttachments_SocialPosts_SocialPostId",
                        column: x => x.SocialPostId,
                        principalTable: "SocialPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SocialPostReactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SocialPostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    ActorParticipantType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ReactionType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPostReactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialPostReactions_SocialPosts_SocialPostId",
                        column: x => x.SocialPostId,
                        principalTable: "SocialPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SocialPostReposts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SocialPostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    ActorParticipantType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPostReposts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialPostReposts_SocialPosts_SocialPostId",
                        column: x => x.SocialPostId,
                        principalTable: "SocialPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SocialPostSaves",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SocialPostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    ActorParticipantType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPostSaves", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialPostSaves_SocialPosts_SocialPostId",
                        column: x => x.SocialPostId,
                        principalTable: "SocialPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SocialPostShares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SocialPostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    ActorParticipantType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPostShares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialPostShares_SocialPosts_SocialPostId",
                        column: x => x.SocialPostId,
                        principalTable: "SocialPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SocialPostViews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SocialPostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ViewerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    ViewerParticipantType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    FirstViewedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastViewedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MaximumWatchDurationSeconds = table.Column<decimal>(type: "TEXT", precision: 12, scale: 3, nullable: true),
                    MaximumWatchCompletionPercentage = table.Column<decimal>(type: "TEXT", precision: 5, scale: 2, nullable: true),
                    StoryExitCount = table.Column<int>(type: "INTEGER", nullable: false),
                    StoryTapForwardCount = table.Column<int>(type: "INTEGER", nullable: false),
                    StoryTapBackwardCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPostViews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialPostViews_SocialPosts_SocialPostId",
                        column: x => x.SocialPostId,
                        principalTable: "SocialPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WebsiteLeadIntakeLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WebsiteLeadRowId = table.Column<long>(type: "INTEGER", nullable: false),
                    WebsiteLeadPublicId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkstationLeadId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AgentUserId = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    Bucket = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    SubmittedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CapturedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SourcePageKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    SourceCtaKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    PageVariant = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    PageMode = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    PagePath = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    LandingPageUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ReferrerUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    InterestType = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    OfferKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    ProductType = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    UtmSource = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    UtmMedium = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    UtmCampaign = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    UtmId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    UtmTerm = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    UtmContent = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    Fbclid = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    Fbp = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Fbc = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ClientIpAddress = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ClientUserAgent = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    MetaCampaignId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    MetaAdSetId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    MetaAdId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    VisitorId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    DiscoverySummaryJson = table.Column<string>(type: "TEXT", nullable: true),
                    EstimateSummary = table.Column<string>(type: "TEXT", maxLength: 600, nullable: true),
                    RecommendationPrimaryKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    RecommendationPrimaryTitle = table.Column<string>(type: "TEXT", maxLength: 240, nullable: true),
                    RecommendationSecondaryKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    RecommendationSecondaryTitle = table.Column<string>(type: "TEXT", maxLength: 240, nullable: true),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebsiteLeadIntakeLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebsiteLeadIntakeLinks_WebsiteLeads_WebsiteLeadRowId",
                        column: x => x.WebsiteLeadRowId,
                        principalTable: "WebsiteLeads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WebsiteLeadIntakeLinks_WorkstationLeadProfiles_WorkstationLeadId",
                        column: x => x.WorkstationLeadId,
                        principalTable: "WorkstationLeadProfiles",
                        principalColumn: "LeadId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AcceptedOfferId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerAgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ProviderEnvironment = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ProviderCustomerId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    DefaultPaymentMethodId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProviderSubscriptionId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    ProviderPlanVariationId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    MonthlyAmountCents = table.Column<int>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    BillingTimeZoneId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    BillingAnchorDay = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    PaymentStanding = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    FirstChargeUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FirstRecurringRenewalUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CurrentPeriodStartUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CurrentPeriodEndUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextBillingDateUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextChargeAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastChargeAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSuccessfulChargeUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsPlatformManaged = table.Column<bool>(type: "INTEGER", nullable: false),
                    PlatformManagedSinceUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ActivatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelledUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelAtPeriodEnd = table.Column<bool>(type: "INTEGER", nullable: false),
                    GracePeriodEndsUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false, defaultValueSql: "X''")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientSubscriptions_ClientPaymentMethods_DefaultPaymentMethodId",
                        column: x => x.DefaultPaymentMethodId,
                        principalTable: "ClientPaymentMethods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ClientSubscriptions_ClientProfiles_ClientProfileId",
                        column: x => x.ClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientSubscriptions_ClientSubscriptionOffers_AcceptedOfferId",
                        column: x => x.AcceptedOfferId,
                        principalTable: "ClientSubscriptionOffers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionActivationInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientSubscriptionOfferId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IntendedNormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ViewedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PaymentStartedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RedeemedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SupersededUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedByAgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSentUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SendCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false, defaultValueSql: "X''")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionActivationInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubscriptionActivationInvitations_ClientProfiles_ClientProfileId",
                        column: x => x.ClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SubscriptionActivationInvitations_ClientSubscriptionOffers_ClientSubscriptionOfferId",
                        column: x => x.ClientSubscriptionOfferId,
                        principalTable: "ClientSubscriptionOffers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImportedFinancialAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FinancialDataConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderAccountId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PersistentAccountKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    OfficialName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Mask = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    AccountType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    AccountSubtype = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    CurrencyCode = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    CurrentBalanceCents = table.Column<long>(type: "INTEGER", nullable: true),
                    AvailableBalanceCents = table.Column<long>(type: "INTEGER", nullable: true),
                    IsClosed = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedFinancialAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportedFinancialAccounts_ClientProfiles_ClientProfileId",
                        column: x => x.ClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ImportedFinancialAccounts_FinancialDataConnections_FinancialDataConnectionId",
                        column: x => x.FinancialDataConnectionId,
                        principalTable: "FinancialDataConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FinancialFindingFeedback",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FinancialFindingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ActorUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    FeedbackType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ReasonCode = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialFindingFeedback", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FinancialFindingFeedback_ClientProfiles_ClientProfileId",
                        column: x => x.ClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialFindingFeedback_FinancialFindings_FinancialFindingId",
                        column: x => x.FinancialFindingId,
                        principalTable: "FinancialFindings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FinancialFindingObservations",
                columns: table => new
                {
                    FinancialFindingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FinancialObservationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialFindingObservations", x => new { x.FinancialFindingId, x.FinancialObservationId });
                    table.ForeignKey(
                        name: "FK_FinancialFindingObservations_FinancialFindings_FinancialFindingId",
                        column: x => x.FinancialFindingId,
                        principalTable: "FinancialFindings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialFindingObservations_FinancialObservations_FinancialObservationId",
                        column: x => x.FinancialObservationId,
                        principalTable: "FinancialObservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommerceOrderLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CommerceOrderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductExternalKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    ProductName = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    ProductSlug = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    Size = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    UnitPriceCents = table.Column<int>(type: "INTEGER", nullable: false),
                    CompareAtPriceCents = table.Column<int>(type: "INTEGER", nullable: false),
                    ImageUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceOrderLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceOrderLines_CommerceOrders_CommerceOrderId",
                        column: x => x.CommerceOrderId,
                        principalTable: "CommerceOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommerceProductDiscounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CommerceProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExternalDiscountKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    DiscountType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 9, scale: 4, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceProductDiscounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceProductDiscounts_CommerceProducts_CommerceProductId",
                        column: x => x.CommerceProductId,
                        principalTable: "CommerceProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommerceProductImages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CommerceProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExternalImageKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    ImageUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    AltText = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisplayOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    ObjectFit = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ObjectPositionX = table.Column<int>(type: "INTEGER", nullable: false),
                    ObjectPositionY = table.Column<int>(type: "INTEGER", nullable: false),
                    Zoom = table.Column<decimal>(type: "TEXT", precision: 9, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceProductImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceProductImages_CommerceProducts_CommerceProductId",
                        column: x => x.CommerceProductId,
                        principalTable: "CommerceProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommerceProductInventoryItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CommerceProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExternalInventoryKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Size = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    StockQuantity = table.Column<int>(type: "INTEGER", nullable: false),
                    LowStockThreshold = table.Column<int>(type: "INTEGER", nullable: false),
                    DisplayOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceProductInventoryItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceProductInventoryItems_CommerceProducts_CommerceProductId",
                        column: x => x.CommerceProductId,
                        principalTable: "CommerceProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MessageAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InternalMessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginalFileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    StoredFileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    StoragePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    ScanStatus = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageAttachments_InternalMessages_InternalMessageId",
                        column: x => x.InternalMessageId,
                        principalTable: "InternalMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LeadAppointments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkstationLeadId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    OwnerAgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    WebsiteLeadIntakeLinkId = table.Column<Guid>(type: "TEXT", nullable: true),
                    WebsiteLeadId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ClientProfileId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BookingProvider = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    BookingSource = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    RequestedBookingSource = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ConfirmationSource = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    BookingConfigurationSource = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    BookingTrackingProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BookingAgentSlug = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    BookingAgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    BookingCalendarUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    BookingCalendarEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    BookingPageIdOrMailbox = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    CalendarEventId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CalendarEventWebLink = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ScheduledStartUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ScheduledEndUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MeetingUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    LastSyncedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncStatus = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    LastSyncError = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    RawProviderPayloadJson = table.Column<string>(type: "text", nullable: true),
                    MatchConfidence = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastStatusChangedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RequestedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BookedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ConfirmedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NoShowUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelledUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RescheduledUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadAppointments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadAppointments_WebsiteLeadIntakeLinks_WebsiteLeadIntakeLinkId",
                        column: x => x.WebsiteLeadIntakeLinkId,
                        principalTable: "WebsiteLeadIntakeLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_LeadAppointments_WorkstationLeadProfiles_WorkstationLeadId",
                        column: x => x.WorkstationLeadId,
                        principalTable: "WorkstationLeadProfiles",
                        principalColumn: "LeadId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientBillingNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientSubscriptionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    EventKey = table.Column<string>(type: "TEXT", maxLength: 220, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    PlainTextBody = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    NotBeforeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SafeFailureCode = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false, defaultValueSql: "X''")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientBillingNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientBillingNotifications_ClientProfiles_ClientProfileId",
                        column: x => x.ClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientBillingNotifications_ClientSubscriptions_ClientSubscriptionId",
                        column: x => x.ClientSubscriptionId,
                        principalTable: "ClientSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientSubscriptionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ClientPaymentMethodId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CommerceOrderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ProviderEnvironment = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ProviderPaymentId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    ProviderInvoiceId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    ProviderRefundId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    AmountCents = table.Column<int>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    AttemptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SafeFailureCode = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    BillingPeriodStartUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BillingPeriodEndUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ScheduledChargeUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClaimedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClaimToken = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    RetryNotBeforeUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Retryable = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProviderRequestId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    ProviderOccurredUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false, defaultValueSql: "X''")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubscriptionPayments_ClientPaymentMethods_ClientPaymentMethodId",
                        column: x => x.ClientPaymentMethodId,
                        principalTable: "ClientPaymentMethods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SubscriptionPayments_ClientSubscriptions_ClientSubscriptionId",
                        column: x => x.ClientSubscriptionId,
                        principalTable: "ClientSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SubscriptionPayments_CommerceOrders_CommerceOrderId",
                        column: x => x.CommerceOrderId,
                        principalTable: "CommerceOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ClientIdentityContinuations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubscriptionActivationInvitationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ClientSubscriptionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Purpose = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IntendedNormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    ReturnUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConsumedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false, defaultValueSql: "X''")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientIdentityContinuations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientIdentityContinuations_ClientProfiles_ClientProfileId",
                        column: x => x.ClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientIdentityContinuations_ClientSubscriptions_ClientSubscriptionId",
                        column: x => x.ClientSubscriptionId,
                        principalTable: "ClientSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ClientIdentityContinuations_SubscriptionActivationInvitations_SubscriptionActivationInvitationId",
                        column: x => x.SubscriptionActivationInvitationId,
                        principalTable: "SubscriptionActivationInvitations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ImportedFinancialTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FinancialDataConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ImportedFinancialAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderTransactionId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ProviderPendingTransactionId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    OriginalName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    OriginalMerchantName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    AuthorizedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PostedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AmountCents = table.Column<long>(type: "INTEGER", nullable: false),
                    CurrencyCode = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    IsPending = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsRemoved = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProviderCategoryJson = table.Column<string>(type: "TEXT", nullable: true),
                    ProviderPayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    ImportedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedFinancialTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportedFinancialTransactions_ClientProfiles_ClientProfileId",
                        column: x => x.ClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ImportedFinancialTransactions_FinancialDataConnections_FinancialDataConnectionId",
                        column: x => x.FinancialDataConnectionId,
                        principalTable: "FinancialDataConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ImportedFinancialTransactions_ImportedFinancialAccounts_ImportedFinancialAccountId",
                        column: x => x.ImportedFinancialAccountId,
                        principalTable: "ImportedFinancialAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecurringFinancialStreams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FinancialDataConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ImportedFinancialAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StreamKey = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    NormalizedMerchantKey = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Cadence = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    AverageAmountCents = table.Column<long>(type: "INTEGER", nullable: false),
                    NextExpectedDateUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Confidence = table.Column<decimal>(type: "TEXT", precision: 5, scale: 4, nullable: false),
                    EvidenceJson = table.Column<string>(type: "TEXT", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringFinancialStreams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecurringFinancialStreams_ClientProfiles_ClientProfileId",
                        column: x => x.ClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecurringFinancialStreams_FinancialDataConnections_FinancialDataConnectionId",
                        column: x => x.FinancialDataConnectionId,
                        principalTable: "FinancialDataConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecurringFinancialStreams_ImportedFinancialAccounts_ImportedFinancialAccountId",
                        column: x => x.ImportedFinancialAccountId,
                        principalTable: "ImportedFinancialAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExpenseLensStreamLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecurringFinancialStreamId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExpenseLensToolId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ExpenseLensItemId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ConfirmedByUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    ConfirmedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseLensStreamLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExpenseLensStreamLinks_ClientProfiles_ClientProfileId",
                        column: x => x.ClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExpenseLensStreamLinks_RecurringFinancialStreams_RecurringFinancialStreamId",
                        column: x => x.RecurringFinancialStreamId,
                        principalTable: "RecurringFinancialStreams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActionItems_EffectiveAgentOid_Status_DueDateUtc",
                table: "ActionItems",
                columns: new[] { "EffectiveAgentOid", "Status", "DueDateUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionItems_OwnerId_Status_DueDateUtc",
                table: "ActionItems",
                columns: new[] { "OwnerId", "Status", "DueDateUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionItems_RelatedEntityType_RelatedEntityId",
                table: "ActionItems",
                columns: new[] { "RelatedEntityType", "RelatedEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionItems_Source_SourceRef",
                table: "ActionItems",
                columns: new[] { "Source", "SourceRef" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionItems_Status_DueDateUtc",
                table: "ActionItems",
                columns: new[] { "Status", "DueDateUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionLogs_ActionId_OccurredUtc",
                table: "ActionLogs",
                columns: new[] { "ActionId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentAssistants_AssistantUserId",
                table: "AgentAssistants",
                column: "AssistantUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentAssistants_NormalizedEmail",
                table: "AgentAssistants",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentAssistants_ParentAgentUserId",
                table: "AgentAssistants",
                column: "ParentAgentUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentAssistants_ParentAgentUserId_Email",
                table: "AgentAssistants",
                columns: new[] { "ParentAgentUserId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentClients_AgentUserId_ClientUserId",
                table: "AgentClients",
                columns: new[] { "AgentUserId", "ClientUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentClients_ClientUserId",
                table: "AgentClients",
                column: "ClientUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentFinanceToolStates_AgentUserId_ToolId",
                table: "AgentFinanceToolStates",
                columns: new[] { "AgentUserId", "ToolId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentProfiles_AgentUserId",
                table: "AgentProfiles",
                column: "AgentUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentProfiles_NormalizedEmail",
                table: "AgentProfiles",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentTrackingAliases_AgentTrackingProfileId_IsCanonical",
                table: "AgentTrackingAliases",
                columns: new[] { "AgentTrackingProfileId", "IsCanonical" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentTrackingAliases_Slug",
                table: "AgentTrackingAliases",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentTrackingProfiles_AgentUpn",
                table: "AgentTrackingProfiles",
                column: "AgentUpn");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTrackingProfiles_AgentUserId",
                table: "AgentTrackingProfiles",
                column: "AgentUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentTrackingProfiles_Slug",
                table: "AgentTrackingProfiles",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsDriftAlerts_EventType",
                table: "AnalyticsDriftAlerts",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsDriftAlerts_IncidentKey",
                table: "AnalyticsDriftAlerts",
                column: "IncidentKey");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsDriftAlerts_IsActive",
                table: "AnalyticsDriftAlerts",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsDriftAlerts_IsActive_Severity_ObservedUtc",
                table: "AnalyticsDriftAlerts",
                columns: new[] { "IsActive", "Severity", "ObservedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsDriftAlerts_ObservedUtc",
                table: "AnalyticsDriftAlerts",
                column: "ObservedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsDriftAlerts_ScopeKey_ObservedUtc",
                table: "AnalyticsDriftAlerts",
                columns: new[] { "ScopeKey", "ObservedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsDriftAlerts_Severity",
                table: "AnalyticsDriftAlerts",
                column: "Severity");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_AgentSlug",
                table: "AnalyticsEvents",
                column: "AgentSlug");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_AgentTrackingProfileId",
                table: "AnalyticsEvents",
                column: "AgentTrackingProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_AgentTrackingProfileId_EventUtc",
                table: "AnalyticsEvents",
                columns: new[] { "AgentTrackingProfileId", "EventUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_DeviceType",
                table: "AnalyticsEvents",
                column: "DeviceType");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_ElementKey",
                table: "AnalyticsEvents",
                column: "ElementKey");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_ElementKey_EventUtc",
                table: "AnalyticsEvents",
                columns: new[] { "ElementKey", "EventUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_Environment_EventUtc",
                table: "AnalyticsEvents",
                columns: new[] { "Environment", "EventUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_EventType",
                table: "AnalyticsEvents",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_EventType_EventUtc",
                table: "AnalyticsEvents",
                columns: new[] { "EventType", "EventUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_FormKey",
                table: "AnalyticsEvents",
                column: "FormKey");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_PageKey",
                table: "AnalyticsEvents",
                column: "PageKey");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_PageKey_EventUtc",
                table: "AnalyticsEvents",
                columns: new[] { "PageKey", "EventUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_ReceivedUtc",
                table: "AnalyticsEvents",
                column: "ReceivedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_SessionId_Behavior",
                table: "AnalyticsEvents",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_UtmCampaign",
                table: "AnalyticsEvents",
                column: "UtmCampaign");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_UtmId",
                table: "AnalyticsEvents",
                column: "UtmId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_UtmSource",
                table: "AnalyticsEvents",
                column: "UtmSource");

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_VisitorId",
                table: "AnalyticsEvents",
                column: "VisitorId");

            migrationBuilder.CreateIndex(
                name: "UX_AnalyticsEvents_ClientEventId",
                table: "AnalyticsEvents",
                column: "ClientEventId",
                unique: true,
                filter: "[ClientEventId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentSyncLogs_AppointmentId",
                table: "AppointmentSyncLogs",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentSyncLogs_CreatedUtc",
                table: "AppointmentSyncLogs",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentSyncLogs_GraphEventId",
                table: "AppointmentSyncLogs",
                column: "GraphEventId");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentSyncLogs_WorkstationLeadId",
                table: "AppointmentSyncLogs",
                column: "WorkstationLeadId");

            migrationBuilder.CreateIndex(
                name: "IX_BillingAuditEntries_ActorId_OccurredUtc",
                table: "BillingAuditEntries",
                columns: new[] { "ActorId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingAuditEntries_EntityType_EntityId_OccurredUtc",
                table: "BillingAuditEntries",
                columns: new[] { "EntityType", "EntityId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingProviderEvents_ProcessingStatus_RetryUtc",
                table: "BillingProviderEvents",
                columns: new[] { "ProcessingStatus", "RetryUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingProviderEvents_Provider_ProviderEnvironment_ProviderEventId",
                table: "BillingProviderEvents",
                columns: new[] { "Provider", "ProviderEnvironment", "ProviderEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingProviderEvents_ProviderObjectId",
                table: "BillingProviderEvents",
                column: "ProviderObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Blockers_RelatedEntityType_RelatedEntityId_Status",
                table: "Blockers",
                columns: new[] { "RelatedEntityType", "RelatedEntityId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Blockers_UnblockDueDateUtc",
                table: "Blockers",
                column: "UnblockDueDateUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BookkeepingEntries_OwnerUserId_Scope_EntryDate",
                table: "BookkeepingEntries",
                columns: new[] { "OwnerUserId", "Scope", "EntryDate" });

            migrationBuilder.CreateIndex(
                name: "IX_BookkeepingEntries_RecurringExpenseId",
                table: "BookkeepingEntries",
                column: "RecurringExpenseId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientAgentMessagingGrants_AgentUserId_IsActive",
                table: "ClientAgentMessagingGrants",
                columns: new[] { "AgentUserId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientAgentMessagingGrants_ClientUserId_AgentUserId",
                table: "ClientAgentMessagingGrants",
                columns: new[] { "ClientUserId", "AgentUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientAgentMessagingGrants_ClientUserId_IsActive",
                table: "ClientAgentMessagingGrants",
                columns: new[] { "ClientUserId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientBillingNotifications_ClientProfileId",
                table: "ClientBillingNotifications",
                column: "ClientProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientBillingNotifications_ClientSubscriptionId_Kind",
                table: "ClientBillingNotifications",
                columns: new[] { "ClientSubscriptionId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientBillingNotifications_EventKey",
                table: "ClientBillingNotifications",
                column: "EventKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientBillingNotifications_SentUtc_NotBeforeUtc_NextAttemptUtc",
                table: "ClientBillingNotifications",
                columns: new[] { "SentUtc", "NotBeforeUtc", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientEntitlements_ClientProfileId_EntitlementKey",
                table: "ClientEntitlements",
                columns: new[] { "ClientProfileId", "EntitlementKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientEntitlements_Status",
                table: "ClientEntitlements",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ClientFinancialIntelligenceProfiles_ClientProfileId",
                table: "ClientFinancialIntelligenceProfiles",
                column: "ClientProfileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientFinancialIntelligenceProfiles_LastEvaluatedUtc",
                table: "ClientFinancialIntelligenceProfiles",
                column: "LastEvaluatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ClientFinancialPlans_ClientId_IsDeleted",
                table: "ClientFinancialPlans",
                columns: new[] { "ClientId", "IsDeleted" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientIdentityContinuations_ClientProfileId_ConsumedUtc",
                table: "ClientIdentityContinuations",
                columns: new[] { "ClientProfileId", "ConsumedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientIdentityContinuations_ClientProfileId_ExpiresUtc",
                table: "ClientIdentityContinuations",
                columns: new[] { "ClientProfileId", "ExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientIdentityContinuations_ClientSubscriptionId",
                table: "ClientIdentityContinuations",
                column: "ClientSubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientIdentityContinuations_SubscriptionActivationInvitationId",
                table: "ClientIdentityContinuations",
                column: "SubscriptionActivationInvitationId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientIdentityContinuations_TokenHash",
                table: "ClientIdentityContinuations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientPaymentMethods_ClientProfileId_RetiredUtc",
                table: "ClientPaymentMethods",
                columns: new[] { "ClientProfileId", "RetiredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientPaymentMethods_Provider_ProviderEnvironment_ProviderPaymentMethodId",
                table: "ClientPaymentMethods",
                columns: new[] { "Provider", "ProviderEnvironment", "ProviderPaymentMethodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientProfiles_ExternalIdentityObjectId",
                table: "ClientProfiles",
                column: "ExternalIdentityObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientProfiles_NormalizedEmail",
                table: "ClientProfiles",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientSubscriptionOffers_ClientProfileId",
                table: "ClientSubscriptionOffers",
                column: "ClientProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSubscriptionOffers_ClientProfileId_Status",
                table: "ClientSubscriptionOffers",
                columns: new[] { "ClientProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientSubscriptionOffers_OwnerAgentUserId",
                table: "ClientSubscriptionOffers",
                column: "OwnerAgentUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSubscriptions_AcceptedOfferId",
                table: "ClientSubscriptions",
                column: "AcceptedOfferId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSubscriptions_ClientProfileId_Status",
                table: "ClientSubscriptions",
                columns: new[] { "ClientProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientSubscriptions_ClientProfileId_UpdatedUtc",
                table: "ClientSubscriptions",
                columns: new[] { "ClientProfileId", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientSubscriptions_DefaultPaymentMethodId",
                table: "ClientSubscriptions",
                column: "DefaultPaymentMethodId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSubscriptions_IsPlatformManaged_Status_NextBillingDateUtc",
                table: "ClientSubscriptions",
                columns: new[] { "IsPlatformManaged", "Status", "NextBillingDateUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientSubscriptions_Provider_ProviderEnvironment_ProviderCustomerId",
                table: "ClientSubscriptions",
                columns: new[] { "Provider", "ProviderEnvironment", "ProviderCustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientSubscriptions_Provider_ProviderEnvironment_ProviderSubscriptionId",
                table: "ClientSubscriptions",
                columns: new[] { "Provider", "ProviderEnvironment", "ProviderSubscriptionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientSubscriptions_Status_NextBillingDateUtc",
                table: "ClientSubscriptions",
                columns: new[] { "Status", "NextBillingDateUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CommerceBusinesses_IsActive",
                table: "CommerceBusinesses",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_CommerceBusinesses_Key",
                table: "CommerceBusinesses",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceBusinessMembers_CommerceBusinessId_NormalizedEmail",
                table: "CommerceBusinessMembers",
                columns: new[] { "CommerceBusinessId", "NormalizedEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceBusinessMembers_NormalizedEmail",
                table: "CommerceBusinessMembers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "IX_CommerceBusinessSettings_CommerceBusinessId",
                table: "CommerceBusinessSettings",
                column: "CommerceBusinessId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceBusinessStorefrontSettings_CommerceBusinessId",
                table: "CommerceBusinessStorefrontSettings",
                column: "CommerceBusinessId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceBusinessSubscriptions_CommerceBusinessId",
                table: "CommerceBusinessSubscriptions",
                column: "CommerceBusinessId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceBusinessSubscriptions_Status",
                table: "CommerceBusinessSubscriptions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_CommerceOrderLines_CommerceOrderId",
                table: "CommerceOrderLines",
                column: "CommerceOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_CommerceOrders_CheckoutAttemptId",
                table: "CommerceOrders",
                column: "CheckoutAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_CommerceOrders_CommerceBusinessId_CreatedUtc",
                table: "CommerceOrders",
                columns: new[] { "CommerceBusinessId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CommerceOrders_CommerceBusinessId_OrderNumber",
                table: "CommerceOrders",
                columns: new[] { "CommerceBusinessId", "OrderNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceOrders_CommerceBusinessId_PaymentStatus_FulfillmentStatus",
                table: "CommerceOrders",
                columns: new[] { "CommerceBusinessId", "PaymentStatus", "FulfillmentStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_CommerceProductDiscounts_CommerceProductId_Code",
                table: "CommerceProductDiscounts",
                columns: new[] { "CommerceProductId", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_CommerceProductDiscounts_CommerceProductId_ExternalDiscountKey",
                table: "CommerceProductDiscounts",
                columns: new[] { "CommerceProductId", "ExternalDiscountKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceProductImages_CommerceProductId_DisplayOrder",
                table: "CommerceProductImages",
                columns: new[] { "CommerceProductId", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_CommerceProductImages_CommerceProductId_ExternalImageKey",
                table: "CommerceProductImages",
                columns: new[] { "CommerceProductId", "ExternalImageKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceProductInventoryItems_CommerceProductId_Size",
                table: "CommerceProductInventoryItems",
                columns: new[] { "CommerceProductId", "Size" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceProducts_CommerceBusinessId_ExternalProductKey",
                table: "CommerceProducts",
                columns: new[] { "CommerceBusinessId", "ExternalProductKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceProducts_CommerceBusinessId_IsActive_DisplayOrder",
                table: "CommerceProducts",
                columns: new[] { "CommerceBusinessId", "IsActive", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_CommerceProducts_CommerceBusinessId_Slug",
                table: "CommerceProducts",
                columns: new[] { "CommerceBusinessId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Commitments_DueDateUtc_Status",
                table: "Commitments",
                columns: new[] { "DueDateUtc", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Commitments_PromisedById_Status",
                table: "Commitments",
                columns: new[] { "PromisedById", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Commitments_RelatedEntityType_RelatedEntityId",
                table: "Commitments",
                columns: new[] { "RelatedEntityType", "RelatedEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_DecisionRecords_RelatedEntityType_RelatedEntityId_CreatedUtc",
                table: "DecisionRecords",
                columns: new[] { "RelatedEntityType", "RelatedEntityId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseLensStreamLinks_ClientProfileId_ExpenseLensToolId_ExpenseLensItemId",
                table: "ExpenseLensStreamLinks",
                columns: new[] { "ClientProfileId", "ExpenseLensToolId", "ExpenseLensItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseLensStreamLinks_RecurringFinancialStreamId",
                table: "ExpenseLensStreamLinks",
                column: "RecurringFinancialStreamId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinanceToolStates_ClientProfileId_ToolId",
                table: "FinanceToolStates",
                columns: new[] { "ClientProfileId", "ToolId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialDataConnections_ClientProfileId_ProviderKey_ProviderItemId",
                table: "FinancialDataConnections",
                columns: new[] { "ClientProfileId", "ProviderKey", "ProviderItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialDataConnections_ClientProfileId_Status",
                table: "FinancialDataConnections",
                columns: new[] { "ClientProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialFindingFeedback_ClientProfileId_FeedbackType_CreatedUtc",
                table: "FinancialFindingFeedback",
                columns: new[] { "ClientProfileId", "FeedbackType", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialFindingFeedback_FinancialFindingId_CreatedUtc",
                table: "FinancialFindingFeedback",
                columns: new[] { "FinancialFindingId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialFindingObservations_FinancialObservationId",
                table: "FinancialFindingObservations",
                column: "FinancialObservationId");

            migrationBuilder.CreateIndex(
                name: "IX_FinancialFindings_ClientProfileId_FindingKey",
                table: "FinancialFindings",
                columns: new[] { "ClientProfileId", "FindingKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialFindings_ClientProfileId_FindingType",
                table: "FinancialFindings",
                columns: new[] { "ClientProfileId", "FindingType" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialFindings_ClientProfileId_Status_PriorityScore",
                table: "FinancialFindings",
                columns: new[] { "ClientProfileId", "Status", "PriorityScore" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialFindings_RuleIdentifier_RuleVersion",
                table: "FinancialFindings",
                columns: new[] { "RuleIdentifier", "RuleVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialObservations_ClientProfileId_ObservationKey",
                table: "FinancialObservations",
                columns: new[] { "ClientProfileId", "ObservationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialObservations_ClientProfileId_ObservationType_Status",
                table: "FinancialObservations",
                columns: new[] { "ClientProfileId", "ObservationType", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialObservations_ClientProfileId_PeriodEndUtc",
                table: "FinancialObservations",
                columns: new[] { "ClientProfileId", "PeriodEndUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialObservations_RuleIdentifier_RuleVersion",
                table: "FinancialObservations",
                columns: new[] { "RuleIdentifier", "RuleVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_GraphCalendarSubscriptions_AgentUserId_CalendarEmail",
                table: "GraphCalendarSubscriptions",
                columns: new[] { "AgentUserId", "CalendarEmail" });

            migrationBuilder.CreateIndex(
                name: "IX_GraphCalendarSubscriptions_GraphSubscriptionId",
                table: "GraphCalendarSubscriptions",
                column: "GraphSubscriptionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GraphCalendarSubscriptions_IsActive_ExpirationUtc",
                table: "GraphCalendarSubscriptions",
                columns: new[] { "IsActive", "ExpirationUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdMembers_ClientUserId",
                table: "HouseholdMembers",
                column: "ClientUserId");

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdMembers_ClientUserId_RelationshipType",
                table: "HouseholdMembers",
                columns: new[] { "ClientUserId", "RelationshipType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportedFinancialAccounts_ClientProfileId_IsClosed",
                table: "ImportedFinancialAccounts",
                columns: new[] { "ClientProfileId", "IsClosed" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportedFinancialAccounts_FinancialDataConnectionId_ProviderAccountId",
                table: "ImportedFinancialAccounts",
                columns: new[] { "FinancialDataConnectionId", "ProviderAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportedFinancialTransactions_ClientProfileId_IsPending_IsRemoved",
                table: "ImportedFinancialTransactions",
                columns: new[] { "ClientProfileId", "IsPending", "IsRemoved" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportedFinancialTransactions_ClientProfileId_PostedUtc",
                table: "ImportedFinancialTransactions",
                columns: new[] { "ClientProfileId", "PostedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportedFinancialTransactions_FinancialDataConnectionId_ProviderTransactionId",
                table: "ImportedFinancialTransactions",
                columns: new[] { "FinancialDataConnectionId", "ProviderTransactionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportedFinancialTransactions_ImportedFinancialAccountId_PostedUtc",
                table: "ImportedFinancialTransactions",
                columns: new[] { "ImportedFinancialAccountId", "PostedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InternalMessages_ClientMessageId",
                table: "InternalMessages",
                column: "ClientMessageId",
                unique: true,
                filter: "\"ClientMessageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InternalMessages_ConversationId_SentUtc",
                table: "InternalMessages",
                columns: new[] { "ConversationId", "SentUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InternalMessages_ReplyToMessageId",
                table: "InternalMessages",
                column: "ReplyToMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_JourneyCircleBlocks_BlockerClientProfileId_BlockedClientProfileId",
                table: "JourneyCircleBlocks",
                columns: new[] { "BlockerClientProfileId", "BlockedClientProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JourneyCircleConnections_ConnectionKey",
                table: "JourneyCircleConnections",
                column: "ConnectionKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JourneyCircleConnections_RecipientClientProfileId_Status",
                table: "JourneyCircleConnections",
                columns: new[] { "RecipientClientProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_JourneyCircleConnections_RequesterClientProfileId_Status",
                table: "JourneyCircleConnections",
                columns: new[] { "RequesterClientProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_JourneyCircleModerationEvents_RequiresReview_CreatedUtc",
                table: "JourneyCircleModerationEvents",
                columns: new[] { "RequiresReview", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_JourneyCircleProfiles_ClientProfileId",
                table: "JourneyCircleProfiles",
                column: "ClientProfileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JourneyCircleProfiles_IsOptedIn_IsDiscoverable_AllowSuggestions",
                table: "JourneyCircleProfiles",
                columns: new[] { "IsOptedIn", "IsDiscoverable", "AllowSuggestions" });

            migrationBuilder.CreateIndex(
                name: "IX_JourneyCircleReports_Status_CreatedUtc",
                table: "JourneyCircleReports",
                columns: new[] { "Status", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadAppointments_BookingProvider_CalendarEventId",
                table: "LeadAppointments",
                columns: new[] { "BookingProvider", "CalendarEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadAppointments_CalendarEventId",
                table: "LeadAppointments",
                column: "CalendarEventId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadAppointments_ClientProfileId",
                table: "LeadAppointments",
                column: "ClientProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadAppointments_OwnerAgentUserId_Status_ScheduledStartUtc",
                table: "LeadAppointments",
                columns: new[] { "OwnerAgentUserId", "Status", "ScheduledStartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadAppointments_WebsiteLeadId",
                table: "LeadAppointments",
                column: "WebsiteLeadId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadAppointments_WebsiteLeadIntakeLinkId",
                table: "LeadAppointments",
                column: "WebsiteLeadIntakeLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadAppointments_WorkstationLeadId",
                table: "LeadAppointments",
                column: "WorkstationLeadId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadAppointments_WorkstationLeadId_ScheduledStartUtc",
                table: "LeadAppointments",
                columns: new[] { "WorkstationLeadId", "ScheduledStartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadAppointments_WorkstationLeadId_UpdatedUtc",
                table: "LeadAppointments",
                columns: new[] { "WorkstationLeadId", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageAttachments_InternalMessageId",
                table: "MessageAttachments",
                column: "InternalMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageConversationParticipants_ConversationId_UserId_ParticipantType",
                table: "MessageConversationParticipants",
                columns: new[] { "ConversationId", "UserId", "ParticipantType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageConversationParticipants_UserId_IsActive",
                table: "MessageConversationParticipants",
                columns: new[] { "UserId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageConversations_DirectConversationKey",
                table: "MessageConversations",
                column: "DirectConversationKey",
                unique: true,
                filter: "\"DirectConversationKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MessageConversations_IsClosed",
                table: "MessageConversations",
                column: "IsClosed");

            migrationBuilder.CreateIndex(
                name: "IX_MessageConversations_LastMessageUtc",
                table: "MessageConversations",
                column: "LastMessageUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MessagingAuditEntries_ActorUserId",
                table: "MessagingAuditEntries",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MessagingAuditEntries_ConversationId",
                table: "MessagingAuditEntries",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_MessagingAuditEntries_CreatedUtc",
                table: "MessagingAuditEntries",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MetaSignalEvents_AgentSlug",
                table: "MetaSignalEvents",
                column: "AgentSlug");

            migrationBuilder.CreateIndex(
                name: "IX_MetaSignalEvents_AgentTrackingProfileId",
                table: "MetaSignalEvents",
                column: "AgentTrackingProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaSignalEvents_CreatedUtc",
                table: "MetaSignalEvents",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MetaSignalEvents_EventId",
                table: "MetaSignalEvents",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetaSignalEvents_EventName",
                table: "MetaSignalEvents",
                column: "EventName");

            migrationBuilder.CreateIndex(
                name: "IX_MetaSignalEvents_EventName_CreatedUtc",
                table: "MetaSignalEvents",
                columns: new[] { "EventName", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MetaSignalEvents_LeadId",
                table: "MetaSignalEvents",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaSignalEvents_PageMode",
                table: "MetaSignalEvents",
                column: "PageMode");

            migrationBuilder.CreateIndex(
                name: "IX_MetaSignalEvents_QuoteType",
                table: "MetaSignalEvents",
                column: "QuoteType");

            migrationBuilder.CreateIndex(
                name: "IX_MetaSignalEvents_ScoreTier",
                table: "MetaSignalEvents",
                column: "ScoreTier");

            migrationBuilder.CreateIndex(
                name: "IX_MetaSignalEvents_SessionId",
                table: "MetaSignalEvents",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaSignalEvents_SessionId_QuoteType_CreatedUtc",
                table: "MetaSignalEvents",
                columns: new[] { "SessionId", "QuoteType", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MetaSignalEvents_TrafficType",
                table: "MetaSignalEvents",
                column: "TrafficType");

            migrationBuilder.CreateIndex(
                name: "IX_MetaSignalEvents_UtmCampaign",
                table: "MetaSignalEvents",
                column: "UtmCampaign");

            migrationBuilder.CreateIndex(
                name: "IX_MetaSignalEvents_VisitorId",
                table: "MetaSignalEvents",
                column: "VisitorId");

            migrationBuilder.CreateIndex(
                name: "IX_MobileProfileSettings_NormalizedUsername",
                table: "MobileProfileSettings",
                column: "NormalizedUsername",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MobileProfileSettings_ProfileId_ParticipantType",
                table: "MobileProfileSettings",
                columns: new[] { "ProfileId", "ParticipantType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnboardingInvites_NormalizedEmail",
                table: "OnboardingInvites",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnboardingInvites_TokenHash",
                table: "OnboardingInvites",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnboardingSubmissions_InviteId",
                table: "OnboardingSubmissions",
                column: "InviteId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlaybookExecutions_ExecutionKey",
                table: "PlaybookExecutions",
                column: "ExecutionKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionRecords_AgentUserId",
                table: "ProductionRecords",
                column: "AgentUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionRecords_AgentUserId_Side",
                table: "ProductionRecords",
                columns: new[] { "AgentUserId", "Side" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductionRecords_ClientUserId",
                table: "ProductionRecords",
                column: "ClientUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionRecords_LeadId",
                table: "ProductionRecords",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionRecords_Status",
                table: "ProductionRecords",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Proposals_AgentUserId",
                table: "Proposals",
                column: "AgentUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Proposals_AgentUserId_LeadId",
                table: "Proposals",
                columns: new[] { "AgentUserId", "LeadId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpenses_OwnerUserId_Scope_IsActive",
                table: "RecurringExpenses",
                columns: new[] { "OwnerUserId", "Scope", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringFinancialStreams_ClientProfileId_Status",
                table: "RecurringFinancialStreams",
                columns: new[] { "ClientProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringFinancialStreams_ClientProfileId_StreamKey",
                table: "RecurringFinancialStreams",
                columns: new[] { "ClientProfileId", "StreamKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecurringFinancialStreams_FinancialDataConnectionId",
                table: "RecurringFinancialStreams",
                column: "FinancialDataConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringFinancialStreams_ImportedFinancialAccountId",
                table: "RecurringFinancialStreams",
                column: "ImportedFinancialAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SocialFollows_FollowedUserId_FollowedParticipantType",
                table: "SocialFollows",
                columns: new[] { "FollowedUserId", "FollowedParticipantType" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialFollows_FollowerUserId_FollowerParticipantType_FollowedUserId_FollowedParticipantType",
                table: "SocialFollows",
                columns: new[] { "FollowerUserId", "FollowerParticipantType", "FollowedUserId", "FollowedParticipantType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialFollows_SourceSocialPostId",
                table: "SocialFollows",
                column: "SourceSocialPostId");

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostComments_ParentCommentId",
                table: "SocialPostComments",
                column: "ParentCommentId");

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostComments_SocialPostId_DeletedUtc_CreatedUtc",
                table: "SocialPostComments",
                columns: new[] { "SocialPostId", "DeletedUtc", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostMediaAssets_ProcessingState_CreatedUtc",
                table: "SocialPostMediaAssets",
                columns: new[] { "ProcessingState", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostMediaAssets_SocialPostId_DisplayOrder",
                table: "SocialPostMediaAssets",
                columns: new[] { "SocialPostId", "DisplayOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostMediaAssets_StorageKey",
                table: "SocialPostMediaAssets",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostMusicAttachments_ProviderId_ProviderTrackId",
                table: "SocialPostMusicAttachments",
                columns: new[] { "ProviderId", "ProviderTrackId" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostMusicAttachments_SocialPostId",
                table: "SocialPostMusicAttachments",
                column: "SocialPostId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostReactions_SocialPostId_ActorUserId_ActorParticipantType",
                table: "SocialPostReactions",
                columns: new[] { "SocialPostId", "ActorUserId", "ActorParticipantType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostReposts_SocialPostId_ActorUserId_ActorParticipantType",
                table: "SocialPostReposts",
                columns: new[] { "SocialPostId", "ActorUserId", "ActorParticipantType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostReposts_SocialPostId_CreatedUtc",
                table: "SocialPostReposts",
                columns: new[] { "SocialPostId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPosts_AuthorUserId_AuthorParticipantType_PostedUtc",
                table: "SocialPosts",
                columns: new[] { "AuthorUserId", "AuthorParticipantType", "PostedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPosts_DeletedUtc_ExpiresUtc_PostedUtc",
                table: "SocialPosts",
                columns: new[] { "DeletedUtc", "ExpiresUtc", "PostedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPosts_RepostOfSocialPostId",
                table: "SocialPosts",
                column: "RepostOfSocialPostId");

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostSaves_SocialPostId_ActorUserId_ActorParticipantType",
                table: "SocialPostSaves",
                columns: new[] { "SocialPostId", "ActorUserId", "ActorParticipantType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostSaves_SocialPostId_CreatedUtc",
                table: "SocialPostSaves",
                columns: new[] { "SocialPostId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostShares_SocialPostId_ActorUserId_ActorParticipantType",
                table: "SocialPostShares",
                columns: new[] { "SocialPostId", "ActorUserId", "ActorParticipantType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostShares_SocialPostId_CreatedUtc",
                table: "SocialPostShares",
                columns: new[] { "SocialPostId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostViews_SocialPostId_FirstViewedUtc",
                table: "SocialPostViews",
                columns: new[] { "SocialPostId", "FirstViewedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostViews_SocialPostId_ViewerUserId_ViewerParticipantType",
                table: "SocialPostViews",
                columns: new[] { "SocialPostId", "ViewerUserId", "ViewerParticipantType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialProfileVisits_TargetUserId_TargetParticipantType_FirstVisitedUtc",
                table: "SocialProfileVisits",
                columns: new[] { "TargetUserId", "TargetParticipantType", "FirstVisitedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialProfileVisits_TargetUserId_TargetParticipantType_VisitorUserId_VisitorParticipantType_SourceSocialPostId",
                table: "SocialProfileVisits",
                columns: new[] { "TargetUserId", "TargetParticipantType", "VisitorUserId", "VisitorParticipantType", "SourceSocialPostId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionActivationInvitations_ClientProfileId_Status",
                table: "SubscriptionActivationInvitations",
                columns: new[] { "ClientProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionActivationInvitations_ClientSubscriptionOfferId",
                table: "SubscriptionActivationInvitations",
                column: "ClientSubscriptionOfferId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionActivationInvitations_TokenHash",
                table: "SubscriptionActivationInvitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPayments_ClientPaymentMethodId",
                table: "SubscriptionPayments",
                column: "ClientPaymentMethodId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPayments_ClientSubscriptionId",
                table: "SubscriptionPayments",
                column: "ClientSubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPayments_ClientSubscriptionId_BillingPeriodStartUtc_AttemptNumber",
                table: "SubscriptionPayments",
                columns: new[] { "ClientSubscriptionId", "BillingPeriodStartUtc", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPayments_CommerceOrderId",
                table: "SubscriptionPayments",
                column: "CommerceOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPayments_Provider_ProviderEnvironment_IdempotencyKey",
                table: "SubscriptionPayments",
                columns: new[] { "Provider", "ProviderEnvironment", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPayments_Provider_ProviderEnvironment_ProviderPaymentId",
                table: "SubscriptionPayments",
                columns: new[] { "Provider", "ProviderEnvironment", "ProviderPaymentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPayments_Provider_ProviderEnvironment_ProviderRefundId",
                table: "SubscriptionPayments",
                columns: new[] { "Provider", "ProviderEnvironment", "ProviderRefundId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPayments_Status_RetryNotBeforeUtc_ScheduledChargeUtc",
                table: "SubscriptionPayments",
                columns: new[] { "Status", "RetryNotBeforeUtc", "ScheduledChargeUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_UnderwritingRecords_AgentUserId",
                table: "UnderwritingRecords",
                column: "AgentUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UnderwritingRecords_AgentUserId_LeadId",
                table: "UnderwritingRecords",
                columns: new[] { "AgentUserId", "LeadId" });

            migrationBuilder.CreateIndex(
                name: "IX_UnderwritingRecords_ProductCode",
                table: "UnderwritingRecords",
                column: "ProductCode");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeadIntakeLinks_AgentUserId_SubmittedUtc",
                table: "WebsiteLeadIntakeLinks",
                columns: new[] { "AgentUserId", "SubmittedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeadIntakeLinks_WebsiteLeadRowId",
                table: "WebsiteLeadIntakeLinks",
                column: "WebsiteLeadRowId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeadIntakeLinks_WorkstationLeadId_SubmittedUtc",
                table: "WebsiteLeadIntakeLinks",
                columns: new[] { "WorkstationLeadId", "SubmittedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_AgentSlug",
                table: "WebsiteLeads",
                column: "AgentSlug");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_AgentTrackingProfileId",
                table: "WebsiteLeads",
                column: "AgentTrackingProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_AgentTrackingProfileId_CreatedUtc",
                table: "WebsiteLeads",
                columns: new[] { "AgentTrackingProfileId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_CreatedUtc",
                table: "WebsiteLeads",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_Email",
                table: "WebsiteLeads",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_Environment_CreatedUtc",
                table: "WebsiteLeads",
                columns: new[] { "Environment", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_InterestType",
                table: "WebsiteLeads",
                column: "InterestType");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_MetaCampaignId",
                table: "WebsiteLeads",
                column: "MetaCampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_SourceCtaKey",
                table: "WebsiteLeads",
                column: "SourceCtaKey");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_SourcePageKey",
                table: "WebsiteLeads",
                column: "SourcePageKey");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_UtmCampaign",
                table: "WebsiteLeads",
                column: "UtmCampaign");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_UtmId",
                table: "WebsiteLeads",
                column: "UtmId");

            migrationBuilder.CreateIndex(
                name: "IX_WebsiteLeads_UtmSource",
                table: "WebsiteLeads",
                column: "UtmSource");

            migrationBuilder.CreateIndex(
                name: "IX_WorkstationLeadProfiles_AgentUserId",
                table: "WorkstationLeadProfiles",
                column: "AgentUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkstationLeadProfiles_AgentUserId_OriginalLeadType",
                table: "WorkstationLeadProfiles",
                columns: new[] { "AgentUserId", "OriginalLeadType" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkstationLeadProfiles_AgentUserId_Phone",
                table: "WorkstationLeadProfiles",
                columns: new[] { "AgentUserId", "Phone" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkstationLeadProfiles_Bucket",
                table: "WorkstationLeadProfiles",
                column: "Bucket");

            migrationBuilder.CreateIndex(
                name: "IX_WorkstationLeadProfiles_Email",
                table: "WorkstationLeadProfiles",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_WorkstationLeadProfiles_OriginalLeadType",
                table: "WorkstationLeadProfiles",
                column: "OriginalLeadType");

            migrationBuilder.CreateIndex(
                name: "IX_WorkstationLeadProfiles_Phone",
                table: "WorkstationLeadProfiles",
                column: "Phone");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActionItems");

            migrationBuilder.DropTable(
                name: "ActionLogs");

            migrationBuilder.DropTable(
                name: "AgentAssistants");

            migrationBuilder.DropTable(
                name: "AgentClients");

            migrationBuilder.DropTable(
                name: "AgentFinanceToolStates");

            migrationBuilder.DropTable(
                name: "AgentProfiles");

            migrationBuilder.DropTable(
                name: "AgentTrackingAliases");

            migrationBuilder.DropTable(
                name: "AgentZoomLinks");

            migrationBuilder.DropTable(
                name: "AnalyticsDriftAlerts");

            migrationBuilder.DropTable(
                name: "AnalyticsEvents");

            migrationBuilder.DropTable(
                name: "AppointmentSyncLogs");

            migrationBuilder.DropTable(
                name: "BillingAuditEntries");

            migrationBuilder.DropTable(
                name: "BillingProviderEvents");

            migrationBuilder.DropTable(
                name: "Blockers");

            migrationBuilder.DropTable(
                name: "BookkeepingEntries");

            migrationBuilder.DropTable(
                name: "ClientAgentMessagingGrants");

            migrationBuilder.DropTable(
                name: "ClientBillingNotifications");

            migrationBuilder.DropTable(
                name: "ClientEntitlements");

            migrationBuilder.DropTable(
                name: "ClientFinancialIntelligenceProfiles");

            migrationBuilder.DropTable(
                name: "ClientFinancialPlans");

            migrationBuilder.DropTable(
                name: "ClientIdentityContinuations");

            migrationBuilder.DropTable(
                name: "CommerceBusinessMembers");

            migrationBuilder.DropTable(
                name: "CommerceBusinessSettings");

            migrationBuilder.DropTable(
                name: "CommerceBusinessStorefrontSettings");

            migrationBuilder.DropTable(
                name: "CommerceBusinessSubscriptions");

            migrationBuilder.DropTable(
                name: "CommerceOrderLines");

            migrationBuilder.DropTable(
                name: "CommerceProductDiscounts");

            migrationBuilder.DropTable(
                name: "CommerceProductImages");

            migrationBuilder.DropTable(
                name: "CommerceProductInventoryItems");

            migrationBuilder.DropTable(
                name: "Commitments");

            migrationBuilder.DropTable(
                name: "DecisionRecords");

            migrationBuilder.DropTable(
                name: "ExpenseLensStreamLinks");

            migrationBuilder.DropTable(
                name: "FinanceToolStates");

            migrationBuilder.DropTable(
                name: "FinancialFindingFeedback");

            migrationBuilder.DropTable(
                name: "FinancialFindingObservations");

            migrationBuilder.DropTable(
                name: "GraphCalendarSubscriptions");

            migrationBuilder.DropTable(
                name: "HouseholdMembers");

            migrationBuilder.DropTable(
                name: "ImportedFinancialTransactions");

            migrationBuilder.DropTable(
                name: "JourneyCircleBlocks");

            migrationBuilder.DropTable(
                name: "JourneyCircleConnections");

            migrationBuilder.DropTable(
                name: "JourneyCircleModerationEvents");

            migrationBuilder.DropTable(
                name: "JourneyCircleProfiles");

            migrationBuilder.DropTable(
                name: "JourneyCircleReports");

            migrationBuilder.DropTable(
                name: "LeadAppointments");

            migrationBuilder.DropTable(
                name: "MessageAttachments");

            migrationBuilder.DropTable(
                name: "MessageConversationParticipants");

            migrationBuilder.DropTable(
                name: "MessagingAuditEntries");

            migrationBuilder.DropTable(
                name: "MetaSignalEvents");

            migrationBuilder.DropTable(
                name: "MobileProfileSettings");

            migrationBuilder.DropTable(
                name: "OnboardingSubmissions");

            migrationBuilder.DropTable(
                name: "PlaybookExecutions");

            migrationBuilder.DropTable(
                name: "ProductionRecords");

            migrationBuilder.DropTable(
                name: "Proposals");

            migrationBuilder.DropTable(
                name: "SocialFollows");

            migrationBuilder.DropTable(
                name: "SocialPostComments");

            migrationBuilder.DropTable(
                name: "SocialPostMediaAssets");

            migrationBuilder.DropTable(
                name: "SocialPostMusicAttachments");

            migrationBuilder.DropTable(
                name: "SocialPostReactions");

            migrationBuilder.DropTable(
                name: "SocialPostReposts");

            migrationBuilder.DropTable(
                name: "SocialPostSaves");

            migrationBuilder.DropTable(
                name: "SocialPostShares");

            migrationBuilder.DropTable(
                name: "SocialPostViews");

            migrationBuilder.DropTable(
                name: "SocialProfileVisits");

            migrationBuilder.DropTable(
                name: "SubscriptionPayments");

            migrationBuilder.DropTable(
                name: "UnderwritingRecords");

            migrationBuilder.DropTable(
                name: "AgentTrackingProfiles");

            migrationBuilder.DropTable(
                name: "RecurringExpenses");

            migrationBuilder.DropTable(
                name: "SubscriptionActivationInvitations");

            migrationBuilder.DropTable(
                name: "CommerceProducts");

            migrationBuilder.DropTable(
                name: "RecurringFinancialStreams");

            migrationBuilder.DropTable(
                name: "FinancialFindings");

            migrationBuilder.DropTable(
                name: "FinancialObservations");

            migrationBuilder.DropTable(
                name: "WebsiteLeadIntakeLinks");

            migrationBuilder.DropTable(
                name: "InternalMessages");

            migrationBuilder.DropTable(
                name: "OnboardingInvites");

            migrationBuilder.DropTable(
                name: "SocialPosts");

            migrationBuilder.DropTable(
                name: "ClientSubscriptions");

            migrationBuilder.DropTable(
                name: "CommerceOrders");

            migrationBuilder.DropTable(
                name: "ImportedFinancialAccounts");

            migrationBuilder.DropTable(
                name: "WebsiteLeads");

            migrationBuilder.DropTable(
                name: "WorkstationLeadProfiles");

            migrationBuilder.DropTable(
                name: "MessageConversations");

            migrationBuilder.DropTable(
                name: "ClientPaymentMethods");

            migrationBuilder.DropTable(
                name: "ClientSubscriptionOffers");

            migrationBuilder.DropTable(
                name: "CommerceBusinesses");

            migrationBuilder.DropTable(
                name: "FinancialDataConnections");

            migrationBuilder.DropTable(
                name: "ClientProfiles");
        }
    }
}
