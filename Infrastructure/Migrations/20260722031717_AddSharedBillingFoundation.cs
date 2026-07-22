using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedBillingFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BillingAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    PreviousStatus = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    NewStatus = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ActorType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ProviderEnvironment = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ProviderEventId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    ProviderObjectId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    ReceivedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SignatureValidatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProcessingStatus = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    ProcessedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RetryUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SafeErrorCode = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    PayloadHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RetainedPayloadJson = table.Column<string>(type: "text", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingProviderEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientEntitlements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntitlementKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    EffectiveUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpirationUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    GraceOrSuspensionUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
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
                name: "ClientSubscriptionOffers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerAgentUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    PriceType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    MonthlyAmountCents = table.Column<int>(type: "int", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    BillingAnchorSelectionMode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SelectedBillingAnchorDay = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    EffectiveUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
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
                name: "ClientSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AcceptedOfferId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerAgentUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ProviderEnvironment = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ProviderCustomerId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    ProviderPaymentMethodId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    ProviderSubscriptionId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    ProviderPlanVariationId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    MonthlyAmountCents = table.Column<int>(type: "int", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    BillingAnchorDay = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PaymentStanding = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CurrentPeriodStartUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CurrentPeriodEndUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextBillingDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ActivatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelledUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelAtPeriodEnd = table.Column<bool>(type: "bit", nullable: false),
                    GracePeriodEndsUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientSubscriptions", x => x.Id);
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientSubscriptionOfferId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IntendedNormalizedEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ViewedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PaymentStartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RedeemedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SupersededUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByAgentUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSentUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SendCount = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
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
                name: "SubscriptionPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientSubscriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CommerceOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Provider = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ProviderEnvironment = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ProviderPaymentId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    ProviderInvoiceId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    ProviderRefundId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    AmountCents = table.Column<int>(type: "int", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SafeFailureCode = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    BillingPeriodStartUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BillingPeriodEndUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProviderOccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionPayments", x => x.Id);
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
                name: "IX_ClientEntitlements_ClientProfileId_EntitlementKey",
                table: "ClientEntitlements",
                columns: new[] { "ClientProfileId", "EntitlementKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientEntitlements_Status",
                table: "ClientEntitlements",
                column: "Status");

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
                name: "IX_ClientSubscriptions_ClientProfileId",
                table: "ClientSubscriptions",
                column: "ClientProfileId",
                unique: true,
                filter: "[Status] <> 'Canceled' AND [Status] <> 'ActivationFailed'");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSubscriptions_ClientProfileId_UpdatedUtc",
                table: "ClientSubscriptions",
                columns: new[] { "ClientProfileId", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientSubscriptions_Provider_ProviderEnvironment_ProviderCustomerId",
                table: "ClientSubscriptions",
                columns: new[] { "Provider", "ProviderEnvironment", "ProviderCustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientSubscriptions_Provider_ProviderEnvironment_ProviderSubscriptionId",
                table: "ClientSubscriptions",
                columns: new[] { "Provider", "ProviderEnvironment", "ProviderSubscriptionId" },
                unique: true,
                filter: "[ProviderSubscriptionId] IS NOT NULL");

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
                name: "IX_SubscriptionPayments_ClientSubscriptionId",
                table: "SubscriptionPayments",
                column: "ClientSubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPayments_CommerceOrderId",
                table: "SubscriptionPayments",
                column: "CommerceOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPayments_Provider_ProviderEnvironment_ProviderPaymentId",
                table: "SubscriptionPayments",
                columns: new[] { "Provider", "ProviderEnvironment", "ProviderPaymentId" },
                unique: true,
                filter: "[ProviderPaymentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPayments_Provider_ProviderEnvironment_ProviderRefundId",
                table: "SubscriptionPayments",
                columns: new[] { "Provider", "ProviderEnvironment", "ProviderRefundId" },
                unique: true,
                filter: "[ProviderRefundId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingAuditEntries");

            migrationBuilder.DropTable(
                name: "BillingProviderEvents");

            migrationBuilder.DropTable(
                name: "ClientEntitlements");

            migrationBuilder.DropTable(
                name: "SubscriptionActivationInvitations");

            migrationBuilder.DropTable(
                name: "SubscriptionPayments");

            migrationBuilder.DropTable(
                name: "ClientSubscriptions");

            migrationBuilder.DropTable(
                name: "ClientSubscriptionOffers");
        }
    }
}
