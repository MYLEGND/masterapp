using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReceptionistCoordination : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReceptionistActiveConfigurationId",
                table: "MarketingConnections",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReceptionistAdmissionEnabled",
                table: "MarketingConnections",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ReceptionistControlRevision",
                table: "MarketingConnections",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "ReceptionistIngressKey",
                table: "MarketingConnections",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "ReceptionistProviderProjectKey",
                table: "MarketingConnections",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.CreateTable(
                name: "ReceptionistBudgetAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AdmissionEnabled = table.Column<bool>(type: "bit", nullable: false),
                    ControlRevision = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Revision = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScopeKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    TimeZoneId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceptionistBudgetAccounts", x => x.Id);
                    table.CheckConstraint("CK_ReceptionistBudgetAccount_Kind", "[Kind] IN ('owner','funding')");
                });

            migrationBuilder.CreateTable(
                name: "ReceptionistConfigurationVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AcceptedBindingJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConfigurationJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConfigurationSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedByActorId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MarketingConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceptionistConfigurationVersions", x => x.Id);
                    table.UniqueConstraint("AK_ReceptionistConfigurationVersions_MarketingConnectionId_Id", x => new { x.MarketingConnectionId, x.Id });
                    table.CheckConstraint("CK_ReceptionistConfigurationVersion_Version", "[Version] > 0");
                    table.ForeignKey(
                        name: "FK_ReceptionistConfigurationVersions_MarketingConnections_MarketingConnectionId",
                        column: x => x.MarketingConnectionId,
                        principalTable: "MarketingConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReceptionistBudgetPeriods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BudgetAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CashCommitted = table.Column<decimal>(type: "decimal(20,6)", precision: 20, scale: 6, nullable: false),
                    CashLimit = table.Column<decimal>(type: "decimal(20,6)", precision: 20, scale: 6, nullable: false),
                    CeilingApprovalReceipt = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true, collation: "Latin1_General_100_BIN2"),
                    ConsumptionCommitted = table.Column<decimal>(type: "decimal(20,6)", precision: 20, scale: 6, nullable: false),
                    ConsumptionLimit = table.Column<decimal>(type: "decimal(20,6)", precision: 20, scale: 6, nullable: false),
                    EndUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MonthKey = table.Column<int>(type: "int", nullable: false),
                    OpeningCashCeiling = table.Column<decimal>(type: "decimal(20,6)", precision: 20, scale: 6, nullable: false),
                    OpeningCashCommitted = table.Column<decimal>(type: "decimal(20,6)", precision: 20, scale: 6, nullable: false),
                    OpeningConsumptionCeiling = table.Column<decimal>(type: "decimal(20,6)", precision: 20, scale: 6, nullable: false),
                    OpeningConsumptionCommitted = table.Column<decimal>(type: "decimal(20,6)", precision: 20, scale: 6, nullable: false),
                    OpeningReceipt = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OpeningReconciled = table.Column<bool>(type: "bit", nullable: false),
                    OpeningVoiceSeconds = table.Column<long>(type: "bigint", nullable: false),
                    ReconciledThroughUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Revision = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    VoiceSecondsCommitted = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceptionistBudgetPeriods", x => x.Id);
                    table.UniqueConstraint("AK_ReceptionistBudgetPeriods_BudgetAccountId_Id", x => new { x.BudgetAccountId, x.Id });
                    table.CheckConstraint("CK_ReceptionistBudgetPeriod_Amounts", "[CashLimit] >= 0 AND [ConsumptionLimit] >= 0 AND [CashCommitted] >= 0 AND [ConsumptionCommitted] >= 0 AND [VoiceSecondsCommitted] >= 0 AND [OpeningCashCommitted] >= 0 AND [OpeningConsumptionCommitted] >= 0 AND [OpeningCashCeiling] >= 0 AND [OpeningConsumptionCeiling] >= 0 AND [OpeningVoiceSeconds] >= 0 AND [CashCommitted] >= [OpeningCashCommitted] AND [ConsumptionCommitted] >= [OpeningConsumptionCommitted] AND [VoiceSecondsCommitted] >= [OpeningVoiceSeconds]");
                    table.CheckConstraint("CK_ReceptionistBudgetPeriod_Month", "[MonthKey] >= 101 AND [MonthKey] % 100 BETWEEN 1 AND 12 AND [StartUtc] < [EndUtc] AND [ReconciledThroughUtc] >= [StartUtc] AND [ReconciledThroughUtc] < [EndUtc]");
                    table.ForeignKey(
                        name: "FK_ReceptionistBudgetPeriods_ReceptionistBudgetAccounts_BudgetAccountId",
                        column: x => x.BudgetAccountId,
                        principalTable: "ReceptionistBudgetAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReceptionistAccountCharges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CashDebitedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CashFinal = table.Column<bool>(type: "bit", nullable: false),
                    CashFundingBudgetPeriodId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CashObserved = table.Column<decimal>(type: "decimal(20,6)", precision: 20, scale: 6, nullable: false),
                    CashOwnerBudgetPeriodId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ComponentKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ConsumptionFinal = table.Column<bool>(type: "bit", nullable: false),
                    ConsumptionFundingBudgetPeriodId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConsumptionIncurredUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConsumptionObserved = table.Column<decimal>(type: "decimal(20,6)", precision: 20, scale: 6, nullable: false),
                    ConsumptionOwnerBudgetPeriodId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FundingBudgetAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerBudgetAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderChargeKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Revision = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceptionistAccountCharges", x => x.Id);
                    table.CheckConstraint("CK_ReceptionistAccountCharge_Amounts", "[CashObserved] >= 0 AND [ConsumptionObserved] >= 0 AND [OwnerBudgetAccountId] <> [FundingBudgetAccountId]");
                    table.CheckConstraint("CK_ReceptionistAccountCharge_Dates", "(([CashDebitedUtc] IS NULL AND [CashOwnerBudgetPeriodId] IS NULL AND [CashFundingBudgetPeriodId] IS NULL AND CAST([CashObserved] AS decimal(20,6)) = 0) OR ([CashDebitedUtc] IS NOT NULL AND [CashOwnerBudgetPeriodId] IS NOT NULL AND [CashFundingBudgetPeriodId] IS NOT NULL)) AND (([ConsumptionIncurredUtc] IS NULL AND [ConsumptionOwnerBudgetPeriodId] IS NULL AND [ConsumptionFundingBudgetPeriodId] IS NULL AND CAST([ConsumptionObserved] AS decimal(20,6)) = 0) OR ([ConsumptionIncurredUtc] IS NOT NULL AND [ConsumptionOwnerBudgetPeriodId] IS NOT NULL AND [ConsumptionFundingBudgetPeriodId] IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_ReceptionistAccountCharges_ReceptionistBudgetAccounts_FundingBudgetAccountId",
                        column: x => x.FundingBudgetAccountId,
                        principalTable: "ReceptionistBudgetAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceptionistAccountCharges_ReceptionistBudgetAccounts_OwnerBudgetAccountId",
                        column: x => x.OwnerBudgetAccountId,
                        principalTable: "ReceptionistBudgetAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceptionistAccountCharges_ReceptionistBudgetPeriods_FundingBudgetAccountId_CashFundingBudgetPeriodId",
                        columns: x => new { x.FundingBudgetAccountId, x.CashFundingBudgetPeriodId },
                        principalTable: "ReceptionistBudgetPeriods",
                        principalColumns: new[] { "BudgetAccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceptionistAccountCharges_ReceptionistBudgetPeriods_FundingBudgetAccountId_ConsumptionFundingBudgetPeriodId",
                        columns: x => new { x.FundingBudgetAccountId, x.ConsumptionFundingBudgetPeriodId },
                        principalTable: "ReceptionistBudgetPeriods",
                        principalColumns: new[] { "BudgetAccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceptionistAccountCharges_ReceptionistBudgetPeriods_OwnerBudgetAccountId_CashOwnerBudgetPeriodId",
                        columns: x => new { x.OwnerBudgetAccountId, x.CashOwnerBudgetPeriodId },
                        principalTable: "ReceptionistBudgetPeriods",
                        principalColumns: new[] { "BudgetAccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceptionistAccountCharges_ReceptionistBudgetPeriods_OwnerBudgetAccountId_ConsumptionOwnerBudgetPeriodId",
                        columns: x => new { x.OwnerBudgetAccountId, x.ConsumptionOwnerBudgetPeriodId },
                        principalTable: "ReceptionistBudgetPeriods",
                        principalColumns: new[] { "BudgetAccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReceptionistCalls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfigurationVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionControlRevision = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ControlledTest = table.Column<bool>(type: "bit", nullable: false),
                    IngressKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    MarketingConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MustStopUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ObservedVoiceSeconds = table.Column<long>(type: "bigint", nullable: false),
                    OwnerBudgetAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerBudgetPeriodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerControlRevision = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProviderAcceptAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProviderAcceptAttemptedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProviderAcceptWorkerId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true, collation: "Latin1_General_100_BIN2"),
                    ProviderClosedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProviderProjectKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProviderSessionId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProviderStateReceipt = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true, collation: "Latin1_General_100_BIN2"),
                    ReservedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReservedVoiceSeconds = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SidebandFence = table.Column<long>(type: "bigint", nullable: false),
                    SidebandLeaseExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SidebandLeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SidebandObservationOnly = table.Column<bool>(type: "bit", nullable: false),
                    SidebandWorkerId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true, collation: "Latin1_General_100_BIN2"),
                    State = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, collation: "Latin1_General_100_BIN2"),
                    VoiceAllocationUncertain = table.Column<bool>(type: "bit", nullable: false),
                    VoiceUsageFinal = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceptionistCalls", x => x.Id);
                    table.UniqueConstraint("AK_ReceptionistCalls_Id_OwnerBudgetAccountId", x => new { x.Id, x.OwnerBudgetAccountId });
                    table.UniqueConstraint("AK_ReceptionistCalls_Id_OwnerBudgetAccountId_OwnerBudgetPeriodId", x => new { x.Id, x.OwnerBudgetAccountId, x.OwnerBudgetPeriodId });
                    table.CheckConstraint("CK_ReceptionistCall_Duration", "[ReservedVoiceSeconds] > 0 AND [ObservedVoiceSeconds] >= 0 AND [MustStopUtc] >= [ReservedUtc]");
                    table.CheckConstraint("CK_ReceptionistCall_State", "[State] IN ('reserved','accepted','uncertain','closed','rejected') AND (([ProviderClosedUtc] IS NULL AND [State] IN ('reserved','accepted','uncertain')) OR ([ProviderClosedUtc] IS NOT NULL AND [State] IN ('closed','rejected'))) AND ([VoiceUsageFinal] = 0 OR ([ProviderClosedUtc] IS NOT NULL AND [VoiceAllocationUncertain] = 0))");
                    table.CheckConstraint("CK_ReceptionistCall_WorkOwnership", "(([ProviderAcceptAttemptId] IS NULL AND [ProviderAcceptWorkerId] IS NULL AND [ProviderAcceptAttemptedUtc] IS NULL) OR ([ProviderAcceptAttemptId] IS NOT NULL AND [ProviderAcceptWorkerId] IS NOT NULL AND [ProviderAcceptAttemptedUtc] IS NOT NULL)) AND (([SidebandLeaseToken] IS NULL AND [SidebandWorkerId] IS NULL AND [SidebandLeaseExpiresUtc] IS NULL AND [SidebandFence] = 0 AND [SidebandObservationOnly] = 0) OR ([SidebandLeaseToken] IS NOT NULL AND [SidebandWorkerId] IS NOT NULL AND [SidebandLeaseExpiresUtc] IS NOT NULL AND [SidebandFence] > 0))");
                    table.ForeignKey(
                        name: "FK_ReceptionistCalls_ReceptionistBudgetPeriods_OwnerBudgetAccountId_OwnerBudgetPeriodId",
                        columns: x => new { x.OwnerBudgetAccountId, x.OwnerBudgetPeriodId },
                        principalTable: "ReceptionistBudgetPeriods",
                        principalColumns: new[] { "BudgetAccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceptionistCalls_ReceptionistConfigurationVersions_MarketingConnectionId_ConfigurationVersionId",
                        columns: x => new { x.MarketingConnectionId, x.ConfigurationVersionId },
                        principalTable: "ReceptionistConfigurationVersions",
                        principalColumns: new[] { "MarketingConnectionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReceptionistActionIntents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActualRecordReference = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true, collation: "Latin1_General_100_BIN2"),
                    ArgumentsSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    CallId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CapabilityKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ConfirmationReceipt = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExecutionFence = table.Column<long>(type: "bigint", nullable: false),
                    ExecutionLeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LogicalActionKey = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Revision = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RouteKey = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false, collation: "Latin1_General_100_BIN2"),
                    State = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, collation: "Latin1_General_100_BIN2"),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    VerificationReceipt = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true, collation: "Latin1_General_100_BIN2"),
                    WorkerId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceptionistActionIntents", x => x.Id);
                    table.CheckConstraint("CK_ReceptionistActionIntent_Attempt", "[AttemptNumber] > 0 AND [ExecutionFence] >= 0 AND ([ExecutionLeaseToken] IS NULL OR ([ExecutionFence] > 0 AND [WorkerId] IS NOT NULL))");
                    table.CheckConstraint("CK_ReceptionistActionIntent_State", "[State] IN ('pending','uncertain','verified','rejected') AND ([State] <> 'verified' OR ([ActualRecordReference] IS NOT NULL AND [VerificationReceipt] IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_ReceptionistActionIntents_ReceptionistCalls_CallId",
                        column: x => x.CallId,
                        principalTable: "ReceptionistCalls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReceptionistCostReservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CallId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CashFinal = table.Column<bool>(type: "bit", nullable: false),
                    CashObserved = table.Column<decimal>(type: "decimal(20,6)", precision: 20, scale: 6, nullable: false),
                    CashReserved = table.Column<decimal>(type: "decimal(20,6)", precision: 20, scale: 6, nullable: false),
                    ComponentKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ConsumptionFinal = table.Column<bool>(type: "bit", nullable: false),
                    ConsumptionObserved = table.Column<decimal>(type: "decimal(20,6)", precision: 20, scale: 6, nullable: false),
                    ConsumptionReserved = table.Column<decimal>(type: "decimal(20,6)", precision: 20, scale: 6, nullable: false),
                    FundingBudgetAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FundingBudgetPeriodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FundingControlRevision = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerBudgetAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerBudgetPeriodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceptionistCostReservations", x => x.Id);
                    table.UniqueConstraint("AK_ReceptionistCostReservations_Id_OwnerBudgetAccountId_FundingBudgetAccountId", x => new { x.Id, x.OwnerBudgetAccountId, x.FundingBudgetAccountId });
                    table.CheckConstraint("CK_ReceptionistCostReservation_Amounts", "[CashReserved] >= 0 AND [ConsumptionReserved] >= 0 AND [CashObserved] >= 0 AND [ConsumptionObserved] >= 0 AND [OwnerBudgetAccountId] <> [FundingBudgetAccountId]");
                    table.ForeignKey(
                        name: "FK_ReceptionistCostReservations_ReceptionistBudgetPeriods_FundingBudgetAccountId_FundingBudgetPeriodId",
                        columns: x => new { x.FundingBudgetAccountId, x.FundingBudgetPeriodId },
                        principalTable: "ReceptionistBudgetPeriods",
                        principalColumns: new[] { "BudgetAccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceptionistCostReservations_ReceptionistCalls_CallId_OwnerBudgetAccountId_OwnerBudgetPeriodId",
                        columns: x => new { x.CallId, x.OwnerBudgetAccountId, x.OwnerBudgetPeriodId },
                        principalTable: "ReceptionistCalls",
                        principalColumns: new[] { "Id", "OwnerBudgetAccountId", "OwnerBudgetPeriodId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReceptionistSessionDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CallId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IngressKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Provider = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProviderProjectKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProviderSessionId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Reason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceptionistSessionDecisions", x => x.Id);
                    table.CheckConstraint("CK_ReceptionistSessionDecision_Outcome", "([Outcome] = 'denied' AND [CallId] IS NULL) OR ([Outcome] = 'reserved' AND [CallId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ReceptionistSessionDecisions_ReceptionistCalls_CallId",
                        column: x => x.CallId,
                        principalTable: "ReceptionistCalls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReceptionistUsageReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountChargeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CallId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ComponentKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_BIN2"),
                    PayloadSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProviderReceiptKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RecordedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ScopeKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceptionistUsageReceipts", x => x.Id);
                    table.CheckConstraint("CK_ReceptionistUsageReceipt_Target", "([CallId] IS NOT NULL AND [AccountChargeId] IS NULL) OR ([CallId] IS NULL AND [AccountChargeId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ReceptionistUsageReceipts_ReceptionistAccountCharges_AccountChargeId",
                        column: x => x.AccountChargeId,
                        principalTable: "ReceptionistAccountCharges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceptionistUsageReceipts_ReceptionistCalls_CallId",
                        column: x => x.CallId,
                        principalTable: "ReceptionistCalls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReceptionistVoiceUsagePostings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CallId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CumulativeSeconds = table.Column<long>(type: "bigint", nullable: false),
                    OwnerBudgetAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerBudgetPeriodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServicePeriodEvidenceUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceptionistVoiceUsagePostings", x => x.Id);
                    table.CheckConstraint("CK_ReceptionistVoiceUsagePosting_Seconds", "[CumulativeSeconds] >= 0");
                    table.ForeignKey(
                        name: "FK_ReceptionistVoiceUsagePostings_ReceptionistBudgetPeriods_OwnerBudgetAccountId_OwnerBudgetPeriodId",
                        columns: x => new { x.OwnerBudgetAccountId, x.OwnerBudgetPeriodId },
                        principalTable: "ReceptionistBudgetPeriods",
                        principalColumns: new[] { "BudgetAccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceptionistVoiceUsagePostings_ReceptionistCalls_CallId_OwnerBudgetAccountId",
                        columns: x => new { x.CallId, x.OwnerBudgetAccountId },
                        principalTable: "ReceptionistCalls",
                        principalColumns: new[] { "Id", "OwnerBudgetAccountId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReceptionistCostPostings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CostReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CumulativeAmount = table.Column<decimal>(type: "decimal(20,6)", precision: 20, scale: 6, nullable: false),
                    FirstEffectiveUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FundingBudgetAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FundingBudgetPeriodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OwnerBudgetAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerBudgetPeriodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceptionistCostPostings", x => x.Id);
                    table.CheckConstraint("CK_ReceptionistCostPosting_Amounts", "[Kind] IN ('cash','consumption') AND [CumulativeAmount] >= 0 AND [OwnerBudgetAccountId] <> [FundingBudgetAccountId]");
                    table.ForeignKey(
                        name: "FK_ReceptionistCostPostings_ReceptionistBudgetPeriods_FundingBudgetAccountId_FundingBudgetPeriodId",
                        columns: x => new { x.FundingBudgetAccountId, x.FundingBudgetPeriodId },
                        principalTable: "ReceptionistBudgetPeriods",
                        principalColumns: new[] { "BudgetAccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceptionistCostPostings_ReceptionistBudgetPeriods_OwnerBudgetAccountId_OwnerBudgetPeriodId",
                        columns: x => new { x.OwnerBudgetAccountId, x.OwnerBudgetPeriodId },
                        principalTable: "ReceptionistBudgetPeriods",
                        principalColumns: new[] { "BudgetAccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceptionistCostPostings_ReceptionistCostReservations_CostReservationId_OwnerBudgetAccountId_FundingBudgetAccountId",
                        columns: x => new { x.CostReservationId, x.OwnerBudgetAccountId, x.FundingBudgetAccountId },
                        principalTable: "ReceptionistCostReservations",
                        principalColumns: new[] { "Id", "OwnerBudgetAccountId", "FundingBudgetAccountId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReceptionistIngressEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DecisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Provider = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProviderProjectKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ReceivedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceptionistIngressEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReceptionistIngressEvents_ReceptionistSessionDecisions_DecisionId",
                        column: x => x.DecisionId,
                        principalTable: "ReceptionistSessionDecisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketingConnections_Id_ReceptionistActiveConfigurationId",
                table: "MarketingConnections",
                columns: new[] { "Id", "ReceptionistActiveConfigurationId" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketingConnections_Provider_ReceptionistIngressKey",
                table: "MarketingConnections",
                columns: new[] { "Provider", "ReceptionistIngressKey" },
                unique: true,
                filter: "[ReceptionistIngressKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MarketingConnections_Provider_ReceptionistProviderProjectKey",
                table: "MarketingConnections",
                columns: new[] { "Provider", "ReceptionistProviderProjectKey" },
                unique: true,
                filter: "[ReceptionistProviderProjectKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistAccountCharges_FundingBudgetAccountId_CashFundingBudgetPeriodId",
                table: "ReceptionistAccountCharges",
                columns: new[] { "FundingBudgetAccountId", "CashFundingBudgetPeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistAccountCharges_FundingBudgetAccountId_ConsumptionFundingBudgetPeriodId",
                table: "ReceptionistAccountCharges",
                columns: new[] { "FundingBudgetAccountId", "ConsumptionFundingBudgetPeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistAccountCharges_FundingBudgetAccountId_ProviderChargeKey",
                table: "ReceptionistAccountCharges",
                columns: new[] { "FundingBudgetAccountId", "ProviderChargeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistAccountCharges_OwnerBudgetAccountId_CashOwnerBudgetPeriodId",
                table: "ReceptionistAccountCharges",
                columns: new[] { "OwnerBudgetAccountId", "CashOwnerBudgetPeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistAccountCharges_OwnerBudgetAccountId_ConsumptionOwnerBudgetPeriodId",
                table: "ReceptionistAccountCharges",
                columns: new[] { "OwnerBudgetAccountId", "ConsumptionOwnerBudgetPeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistActionIntents_AttemptId",
                table: "ReceptionistActionIntents",
                column: "AttemptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistActionIntents_CallId_LogicalActionKey",
                table: "ReceptionistActionIntents",
                columns: new[] { "CallId", "LogicalActionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistBudgetAccounts_Kind_ScopeKey",
                table: "ReceptionistBudgetAccounts",
                columns: new[] { "Kind", "ScopeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistBudgetPeriods_BudgetAccountId_MonthKey",
                table: "ReceptionistBudgetPeriods",
                columns: new[] { "BudgetAccountId", "MonthKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCalls_MarketingConnectionId_ConfigurationVersionId",
                table: "ReceptionistCalls",
                columns: new[] { "MarketingConnectionId", "ConfigurationVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCalls_OwnerBudgetAccountId_OwnerBudgetPeriodId",
                table: "ReceptionistCalls",
                columns: new[] { "OwnerBudgetAccountId", "OwnerBudgetPeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCalls_OwnerBudgetAccountId_ProviderClosedUtc",
                table: "ReceptionistCalls",
                columns: new[] { "OwnerBudgetAccountId", "ProviderClosedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCalls_OwnerBudgetAccountId_ReservedUtc",
                table: "ReceptionistCalls",
                columns: new[] { "OwnerBudgetAccountId", "ReservedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCalls_OwnerBudgetAccountId_VoiceAllocationUncertain",
                table: "ReceptionistCalls",
                columns: new[] { "OwnerBudgetAccountId", "VoiceAllocationUncertain" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCalls_OwnerBudgetAccountId_VoiceUsageFinal",
                table: "ReceptionistCalls",
                columns: new[] { "OwnerBudgetAccountId", "VoiceUsageFinal" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCalls_Provider_ProviderProjectKey_ProviderSessionId",
                table: "ReceptionistCalls",
                columns: new[] { "Provider", "ProviderProjectKey", "ProviderSessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCalls_ProviderAcceptAttemptId",
                table: "ReceptionistCalls",
                column: "ProviderAcceptAttemptId",
                unique: true,
                filter: "[ProviderAcceptAttemptId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCalls_ProviderClosedUtc_MustStopUtc",
                table: "ReceptionistCalls",
                columns: new[] { "ProviderClosedUtc", "MustStopUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCalls_State_SidebandLeaseExpiresUtc",
                table: "ReceptionistCalls",
                columns: new[] { "State", "SidebandLeaseExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistConfigurationVersions_MarketingConnectionId_Version",
                table: "ReceptionistConfigurationVersions",
                columns: new[] { "MarketingConnectionId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCostPostings_CostReservationId_Kind_OwnerBudgetPeriodId_FundingBudgetPeriodId",
                table: "ReceptionistCostPostings",
                columns: new[] { "CostReservationId", "Kind", "OwnerBudgetPeriodId", "FundingBudgetPeriodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCostPostings_CostReservationId_OwnerBudgetAccountId_FundingBudgetAccountId",
                table: "ReceptionistCostPostings",
                columns: new[] { "CostReservationId", "OwnerBudgetAccountId", "FundingBudgetAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCostPostings_FundingBudgetAccountId_FundingBudgetPeriodId",
                table: "ReceptionistCostPostings",
                columns: new[] { "FundingBudgetAccountId", "FundingBudgetPeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCostPostings_OwnerBudgetAccountId_OwnerBudgetPeriodId",
                table: "ReceptionistCostPostings",
                columns: new[] { "OwnerBudgetAccountId", "OwnerBudgetPeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCostReservations_CallId_ComponentKey",
                table: "ReceptionistCostReservations",
                columns: new[] { "CallId", "ComponentKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCostReservations_CallId_OwnerBudgetAccountId_OwnerBudgetPeriodId",
                table: "ReceptionistCostReservations",
                columns: new[] { "CallId", "OwnerBudgetAccountId", "OwnerBudgetPeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCostReservations_FundingBudgetAccountId_CashFinal_ConsumptionFinal",
                table: "ReceptionistCostReservations",
                columns: new[] { "FundingBudgetAccountId", "CashFinal", "ConsumptionFinal" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCostReservations_FundingBudgetAccountId_FundingBudgetPeriodId",
                table: "ReceptionistCostReservations",
                columns: new[] { "FundingBudgetAccountId", "FundingBudgetPeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCostReservations_OwnerBudgetAccountId_CashFinal_ConsumptionFinal",
                table: "ReceptionistCostReservations",
                columns: new[] { "OwnerBudgetAccountId", "CashFinal", "ConsumptionFinal" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistIngressEvents_DecisionId",
                table: "ReceptionistIngressEvents",
                column: "DecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistIngressEvents_Provider_ProviderProjectKey_EventId",
                table: "ReceptionistIngressEvents",
                columns: new[] { "Provider", "ProviderProjectKey", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistSessionDecisions_CallId",
                table: "ReceptionistSessionDecisions",
                column: "CallId",
                unique: true,
                filter: "[CallId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistSessionDecisions_Provider_ProviderProjectKey_ProviderSessionId",
                table: "ReceptionistSessionDecisions",
                columns: new[] { "Provider", "ProviderProjectKey", "ProviderSessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistUsageReceipts_AccountChargeId",
                table: "ReceptionistUsageReceipts",
                column: "AccountChargeId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistUsageReceipts_CallId",
                table: "ReceptionistUsageReceipts",
                column: "CallId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistUsageReceipts_ScopeKey_ProviderReceiptKey",
                table: "ReceptionistUsageReceipts",
                columns: new[] { "ScopeKey", "ProviderReceiptKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistVoiceUsagePostings_CallId_OwnerBudgetAccountId",
                table: "ReceptionistVoiceUsagePostings",
                columns: new[] { "CallId", "OwnerBudgetAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistVoiceUsagePostings_CallId_OwnerBudgetPeriodId",
                table: "ReceptionistVoiceUsagePostings",
                columns: new[] { "CallId", "OwnerBudgetPeriodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistVoiceUsagePostings_OwnerBudgetAccountId_OwnerBudgetPeriodId",
                table: "ReceptionistVoiceUsagePostings",
                columns: new[] { "OwnerBudgetAccountId", "OwnerBudgetPeriodId" });

            migrationBuilder.AddForeignKey(
                name: "FK_MarketingConnections_ReceptionistConfigurationVersions_Id_ReceptionistActiveConfigurationId",
                table: "MarketingConnections",
                columns: new[] { "Id", "ReceptionistActiveConfigurationId" },
                principalTable: "ReceptionistConfigurationVersions",
                principalColumns: new[] { "MarketingConnectionId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MarketingConnections_ReceptionistConfigurationVersions_Id_ReceptionistActiveConfigurationId",
                table: "MarketingConnections");

            migrationBuilder.DropTable(
                name: "ReceptionistActionIntents");

            migrationBuilder.DropTable(
                name: "ReceptionistCostPostings");

            migrationBuilder.DropTable(
                name: "ReceptionistIngressEvents");

            migrationBuilder.DropTable(
                name: "ReceptionistUsageReceipts");

            migrationBuilder.DropTable(
                name: "ReceptionistVoiceUsagePostings");

            migrationBuilder.DropTable(
                name: "ReceptionistCostReservations");

            migrationBuilder.DropTable(
                name: "ReceptionistSessionDecisions");

            migrationBuilder.DropTable(
                name: "ReceptionistAccountCharges");

            migrationBuilder.DropTable(
                name: "ReceptionistCalls");

            migrationBuilder.DropTable(
                name: "ReceptionistBudgetPeriods");

            migrationBuilder.DropTable(
                name: "ReceptionistConfigurationVersions");

            migrationBuilder.DropTable(
                name: "ReceptionistBudgetAccounts");

            migrationBuilder.DropIndex(
                name: "IX_MarketingConnections_Id_ReceptionistActiveConfigurationId",
                table: "MarketingConnections");

            migrationBuilder.DropIndex(
                name: "IX_MarketingConnections_Provider_ReceptionistIngressKey",
                table: "MarketingConnections");

            migrationBuilder.DropIndex(
                name: "IX_MarketingConnections_Provider_ReceptionistProviderProjectKey",
                table: "MarketingConnections");

            migrationBuilder.DropColumn(
                name: "ReceptionistActiveConfigurationId",
                table: "MarketingConnections");

            migrationBuilder.DropColumn(
                name: "ReceptionistAdmissionEnabled",
                table: "MarketingConnections");

            migrationBuilder.DropColumn(
                name: "ReceptionistControlRevision",
                table: "MarketingConnections");

            migrationBuilder.DropColumn(
                name: "ReceptionistIngressKey",
                table: "MarketingConnections");

            migrationBuilder.DropColumn(
                name: "ReceptionistProviderProjectKey",
                table: "MarketingConnections");
        }
    }
}
