using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialIntelligenceFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                            name: "FinancialDataConnections",
                            columns: table => new
                            {
                                Id = table.Column<Guid>(nullable: false),
                                ClientProfileId = table.Column<Guid>(nullable: false),
                                ProviderKey = table.Column<string>(maxLength: 50, nullable: false),
                                ProviderItemId = table.Column<string>(maxLength: 200, nullable: false),
                                ProviderInstitutionId = table.Column<string>(maxLength: 200, nullable: true),
                                DisplayName = table.Column<string>(maxLength: 200, nullable: true),
                                Status = table.Column<string>(maxLength: 40, nullable: false),
                                EncryptedAccessToken = table.Column<string>(maxLength: 4000, nullable: true),
                                SyncCursor = table.Column<string>(maxLength: 4000, nullable: true),
                                LastSyncStartedUtc = table.Column<DateTime>(nullable: true),
                                LastSyncCompletedUtc = table.Column<DateTime>(nullable: true),
                                LastWebhookUtc = table.Column<DateTime>(nullable: true),
                                LastErrorCode = table.Column<string>(maxLength: 100, nullable: true),
                                LastErrorMessage = table.Column<string>(maxLength: 2000, nullable: true),
                                CreatedUtc = table.Column<DateTime>(nullable: false),
                                UpdatedUtc = table.Column<DateTime>(nullable: false)
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
                            name: "ImportedFinancialAccounts",
                            columns: table => new
                            {
                                Id = table.Column<Guid>(nullable: false),
                                ClientProfileId = table.Column<Guid>(nullable: false),
                                FinancialDataConnectionId = table.Column<Guid>(nullable: false),
                                ProviderAccountId = table.Column<string>(maxLength: 200, nullable: false),
                                PersistentAccountKey = table.Column<string>(maxLength: 200, nullable: true),
                                Name = table.Column<string>(maxLength: 200, nullable: false),
                                OfficialName = table.Column<string>(maxLength: 300, nullable: true),
                                Mask = table.Column<string>(maxLength: 20, nullable: true),
                                AccountType = table.Column<string>(maxLength: 50, nullable: false),
                                AccountSubtype = table.Column<string>(maxLength: 80, nullable: true),
                                CurrencyCode = table.Column<string>(maxLength: 3, nullable: false),
                                CurrentBalanceCents = table.Column<long>(nullable: true),
                                AvailableBalanceCents = table.Column<long>(nullable: true),
                                IsClosed = table.Column<bool>(nullable: false),
                                CreatedUtc = table.Column<DateTime>(nullable: false),
                                UpdatedUtc = table.Column<DateTime>(nullable: false)
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
                            name: "ImportedFinancialTransactions",
                            columns: table => new
                            {
                                Id = table.Column<Guid>(nullable: false),
                                ClientProfileId = table.Column<Guid>(nullable: false),
                                FinancialDataConnectionId = table.Column<Guid>(nullable: false),
                                ImportedFinancialAccountId = table.Column<Guid>(nullable: false),
                                ProviderTransactionId = table.Column<string>(maxLength: 200, nullable: false),
                                ProviderPendingTransactionId = table.Column<string>(maxLength: 200, nullable: true),
                                OriginalName = table.Column<string>(maxLength: 500, nullable: false),
                                OriginalMerchantName = table.Column<string>(maxLength: 500, nullable: true),
                                AuthorizedUtc = table.Column<DateTime>(nullable: true),
                                PostedUtc = table.Column<DateTime>(nullable: false),
                                AmountCents = table.Column<long>(nullable: false),
                                CurrencyCode = table.Column<string>(maxLength: 3, nullable: false),
                                IsPending = table.Column<bool>(nullable: false),
                                IsRemoved = table.Column<bool>(nullable: false),
                                ProviderCategoryJson = table.Column<string>(nullable: true),
                                ProviderPayloadJson = table.Column<string>(nullable: false),
                                ImportedUtc = table.Column<DateTime>(nullable: false),
                                UpdatedUtc = table.Column<DateTime>(nullable: false)
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
                                Id = table.Column<Guid>(nullable: false),
                                ClientProfileId = table.Column<Guid>(nullable: false),
                                FinancialDataConnectionId = table.Column<Guid>(nullable: true),
                                ImportedFinancialAccountId = table.Column<Guid>(nullable: true),
                                StreamKey = table.Column<string>(maxLength: 240, nullable: false),
                                NormalizedMerchantKey = table.Column<string>(maxLength: 240, nullable: false),
                                DisplayName = table.Column<string>(maxLength: 300, nullable: false),
                                Cadence = table.Column<string>(maxLength: 40, nullable: false),
                                AverageAmountCents = table.Column<long>(nullable: false),
                                NextExpectedDateUtc = table.Column<DateTime>(nullable: true),
                                Status = table.Column<string>(maxLength: 40, nullable: false),
                                Confidence = table.Column<decimal>(precision: 5, scale: 4, nullable: false),
                                EvidenceJson = table.Column<string>(nullable: false),
                                FirstSeenUtc = table.Column<DateTime>(nullable: false),
                                LastSeenUtc = table.Column<DateTime>(nullable: false),
                                CreatedUtc = table.Column<DateTime>(nullable: false),
                                UpdatedUtc = table.Column<DateTime>(nullable: false)
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
                                Id = table.Column<Guid>(nullable: false),
                                ClientProfileId = table.Column<Guid>(nullable: false),
                                RecurringFinancialStreamId = table.Column<Guid>(nullable: false),
                                ExpenseLensToolId = table.Column<string>(maxLength: 100, nullable: false),
                                ExpenseLensItemId = table.Column<string>(maxLength: 200, nullable: false),
                                Status = table.Column<string>(maxLength: 40, nullable: false),
                                ConfirmedByUserId = table.Column<string>(maxLength: 450, nullable: true),
                                ConfirmedUtc = table.Column<DateTime>(nullable: true),
                                CreatedUtc = table.Column<DateTime>(nullable: false),
                                UpdatedUtc = table.Column<DateTime>(nullable: false)
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
                            name: "IX_ExpenseLensStreamLinks_ClientProfileId_ExpenseLensToolId_ExpenseLensItemId",
                            table: "ExpenseLensStreamLinks",
                            columns: new[] { "ClientProfileId", "ExpenseLensToolId", "ExpenseLensItemId" });

            migrationBuilder.CreateIndex(
                            name: "IX_ExpenseLensStreamLinks_RecurringFinancialStreamId",
                            table: "ExpenseLensStreamLinks",
                            column: "RecurringFinancialStreamId",
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                            name: "ExpenseLensStreamLinks");

            migrationBuilder.DropTable(
                            name: "ImportedFinancialTransactions");

            migrationBuilder.DropTable(
                            name: "RecurringFinancialStreams");

            migrationBuilder.DropTable(
                            name: "ImportedFinancialAccounts");

            migrationBuilder.DropTable(
                            name: "FinancialDataConnections");
        }
    }
}
