using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBehaviorIntelligenceColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameIndex(
                name: "IX_AnalyticsEvents_SessionId",
                table: "AnalyticsEvents",
                newName: "IX_AnalyticsEvents_SessionId_Behavior");

            migrationBuilder.AlterColumn<string>(
                name: "UpdatedBy",
                table: "ClientFinancialPlans",
                type: "TEXT",
                maxLength: 320,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 320,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Browser",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeviceType",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DwellMilliseconds",
                table: "AnalyticsEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ElementId",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EngagedMilliseconds",
                table: "AnalyticsEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FieldName",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FormId",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsBounceCandidate",
                table: "AnalyticsEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsExitPage",
                table: "AnalyticsEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaAdId",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaAdName",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaAdSetId",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaAdSetName",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaCampaignId",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaCampaignName",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OperatingSystem",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Placement",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferrerHost",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScreenHeight",
                table: "AnalyticsEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScreenWidth",
                table: "AnalyticsEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScrollPercent",
                table: "AnalyticsEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UtmContent",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UtmTerm",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ViewportHeight",
                table: "AnalyticsEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ViewportWidth",
                table: "AnalyticsEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "QuickBooksConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    RealmId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AccessTokenCipher = table.Column<string>(type: "TEXT", nullable: false),
                    RefreshTokenCipher = table.Column<string>(type: "TEXT", nullable: false),
                    AccessTokenExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RefreshTokenExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ConnectedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSyncUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncStatus = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastSyncError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuickBooksConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QuickBooksFinancialSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    RealmId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SyncedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevenueMtd = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    RevenueYtd = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ExpensesMtd = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ExpensesYtd = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    NetProfitMtd = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    NetProfitYtd = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CashPosition = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SourceTag = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AccountsCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TopExpenseCategoriesJson = table.Column<string>(type: "TEXT", nullable: true),
                    ProfitTrendJson = table.Column<string>(type: "TEXT", nullable: true),
                    RecentTransactionsJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuickBooksFinancialSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_DeviceType",
                table: "AnalyticsEvents",
                column: "DeviceType");

            migrationBuilder.CreateIndex(
                name: "IX_QuickBooksConnections_IsActive",
                table: "QuickBooksConnections",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_QuickBooksConnections_OwnerUserId",
                table: "QuickBooksConnections",
                column: "OwnerUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuickBooksFinancialSnapshots_OwnerUserId",
                table: "QuickBooksFinancialSnapshots",
                column: "OwnerUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuickBooksFinancialSnapshots_SyncedUtc",
                table: "QuickBooksFinancialSnapshots",
                column: "SyncedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuickBooksConnections");

            migrationBuilder.DropTable(
                name: "QuickBooksFinancialSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_AnalyticsEvents_DeviceType",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "Browser",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "DeviceType",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "DwellMilliseconds",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "ElementId",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "EngagedMilliseconds",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "FieldName",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "FormId",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "IsBounceCandidate",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "IsExitPage",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "MetaAdId",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "MetaAdName",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "MetaAdSetId",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "MetaAdSetName",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "MetaCampaignId",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "MetaCampaignName",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "OperatingSystem",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "Placement",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "ReferrerHost",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "ScreenHeight",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "ScreenWidth",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "ScrollPercent",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "UtmContent",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "UtmTerm",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "ViewportHeight",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "ViewportWidth",
                table: "AnalyticsEvents");

            migrationBuilder.RenameIndex(
                name: "IX_AnalyticsEvents_SessionId_Behavior",
                table: "AnalyticsEvents",
                newName: "IX_AnalyticsEvents_SessionId");

            migrationBuilder.AlterColumn<string>(
                name: "UpdatedBy",
                table: "ClientFinancialPlans",
                type: "TEXT",
                maxLength: 320,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 320);
        }
    }
}
