using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessScopedMerchantPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fail closed without rewriting any historical checkout identity or exposing row contents.
            if (ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer")
                migrationBuilder.Sql("""
                    IF EXISTS (SELECT 1 FROM [CommerceOrders] WHERE [CheckoutAttemptId] IS NOT NULL
                        GROUP BY [CommerceBusinessId], [CheckoutAttemptId] HAVING COUNT_BIG(*) > 1)
                        THROW 51001, 'MERCHANT_CHECKOUT_DUPLICATE_IDENTITY_REQUIRES_REVIEW', 1;
                    """);

            migrationBuilder.AddColumn<string>(
                name: "PaymentEnvironment",
                table: "CommerceOrders",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentProvider",
                table: "CommerceOrders",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentReference",
                table: "CommerceOrders",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CommerceMerchantAuthorizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommerceBusinessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProtectedReturnTicket = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StateHash = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CallbackUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ExpectedAccountId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConsumedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceMerchantAuthorizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceMerchantAuthorizations_CommerceBusinesses_CommerceBusinessId",
                        column: x => x.CommerceBusinessId,
                        principalTable: "CommerceBusinesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommerceMerchantConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommerceBusinessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LocationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    ProtectedCredential = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GrantedScopesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReadinessJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    State = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DisconnectedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceMerchantConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceMerchantConnections_CommerceBusinesses_CommerceBusinessId",
                        column: x => x.CommerceBusinessId,
                        principalTable: "CommerceBusinesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommerceMerchantConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommerceBusinessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DefaultConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceMerchantConfigurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceMerchantConfigurations_CommerceBusinesses_CommerceBusinessId",
                        column: x => x.CommerceBusinessId,
                        principalTable: "CommerceBusinesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommerceMerchantConfigurations_CommerceMerchantConnections_DefaultConnectionId",
                        column: x => x.DefaultConnectionId,
                        principalTable: "CommerceMerchantConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommerceMerchantConnectionRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommerceBusinessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LocationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    GrantedScopesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReadinessJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    State = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ProtectedCredential = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceMerchantConnectionRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceMerchantConnectionRevisions_CommerceMerchantConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "CommerceMerchantConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommercePaymentAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommerceBusinessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommerceOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LocationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ProviderReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PaymentReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CheckoutUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RefundedMinor = table.Column<long>(type: "bigint", nullable: false),
                    DisputeState = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CaptureRequestedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SignalContextJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommercePaymentAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommercePaymentAttempts_CommerceMerchantConnectionRevisions_ConnectionRevisionId",
                        column: x => x.ConnectionRevisionId,
                        principalTable: "CommerceMerchantConnectionRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommercePaymentAttempts_CommerceOrders_CommerceOrderId",
                        column: x => x.CommerceOrderId,
                        principalTable: "CommerceOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommercePaymentEffects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    SafeErrorCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommercePaymentEffects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommercePaymentEffects_CommercePaymentAttempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "CommercePaymentAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommercePaymentReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EventId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    State = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    SafeErrorCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommercePaymentReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommercePaymentReceipts_CommercePaymentAttempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "CommercePaymentAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommercePaymentRefunds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ProviderReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommercePaymentRefunds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommercePaymentRefunds_CommercePaymentAttempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "CommercePaymentAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommerceOrders_CommerceBusinessId_CheckoutAttemptId",
                table: "CommerceOrders",
                columns: new[] { "CommerceBusinessId", "CheckoutAttemptId" },
                unique: true,
                filter: "[CheckoutAttemptId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CommerceMerchantAuthorizations_CommerceBusinessId",
                table: "CommerceMerchantAuthorizations",
                column: "CommerceBusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_CommerceMerchantAuthorizations_StateHash",
                table: "CommerceMerchantAuthorizations",
                column: "StateHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceMerchantConfigurations_CommerceBusinessId_Environment",
                table: "CommerceMerchantConfigurations",
                columns: new[] { "CommerceBusinessId", "Environment" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceMerchantConfigurations_DefaultConnectionId",
                table: "CommerceMerchantConfigurations",
                column: "DefaultConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_CommerceMerchantConnectionRevisions_ConnectionId_Revision",
                table: "CommerceMerchantConnectionRevisions",
                columns: new[] { "ConnectionId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceMerchantConnections_CommerceBusinessId",
                table: "CommerceMerchantConnections",
                column: "CommerceBusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_CommerceMerchantConnections_Provider_Environment_AccountId",
                table: "CommerceMerchantConnections",
                columns: new[] { "Provider", "Environment", "AccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommercePaymentAttempts_CommerceOrderId",
                table: "CommercePaymentAttempts",
                column: "CommerceOrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommercePaymentAttempts_ConnectionRevisionId",
                table: "CommercePaymentAttempts",
                column: "ConnectionRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_CommercePaymentAttempts_Provider_Environment_AccountId_IdempotencyKey",
                table: "CommercePaymentAttempts",
                columns: new[] { "Provider", "Environment", "AccountId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommercePaymentEffects_AttemptId_Kind",
                table: "CommercePaymentEffects",
                columns: new[] { "AttemptId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommercePaymentReceipts_AttemptId",
                table: "CommercePaymentReceipts",
                column: "AttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_CommercePaymentReceipts_Provider_Environment_AccountId_EventId",
                table: "CommercePaymentReceipts",
                columns: new[] { "Provider", "Environment", "AccountId", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommercePaymentRefunds_AttemptId_IdempotencyKey",
                table: "CommercePaymentRefunds",
                columns: new[] { "AttemptId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommerceMerchantAuthorizations");

            migrationBuilder.DropTable(
                name: "CommerceMerchantConfigurations");

            migrationBuilder.DropTable(
                name: "CommercePaymentEffects");

            migrationBuilder.DropTable(
                name: "CommercePaymentReceipts");

            migrationBuilder.DropTable(
                name: "CommercePaymentRefunds");

            migrationBuilder.DropTable(
                name: "CommercePaymentAttempts");

            migrationBuilder.DropTable(
                name: "CommerceMerchantConnectionRevisions");

            migrationBuilder.DropTable(
                name: "CommerceMerchantConnections");

            migrationBuilder.DropIndex(
                name: "IX_CommerceOrders_CommerceBusinessId_CheckoutAttemptId",
                table: "CommerceOrders");

            migrationBuilder.DropColumn(
                name: "PaymentEnvironment",
                table: "CommerceOrders");

            migrationBuilder.DropColumn(
                name: "PaymentProvider",
                table: "CommerceOrders");

            migrationBuilder.DropColumn(
                name: "PaymentReference",
                table: "CommerceOrders");
        }
    }
}
