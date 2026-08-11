using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendConnectTranslationEntitlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ContextualCharactersAvoided",
                table: "LegendTranslationSystemUsages",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "GroupUniqueTargetReuseCount",
                table: "LegendTranslationSystemUsages",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ProviderBillableCharacters",
                table: "LegendTranslationSystemUsages",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ProviderFailureCount",
                table: "LegendTranslationSystemUsages",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ProviderOperationCount",
                table: "LegendTranslationSystemUsages",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "QuotaDeniedRequestCount",
                table: "LegendTranslationSystemUsages",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SameLanguageCharactersAvoided",
                table: "LegendTranslationSystemUsages",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TranslationMemoryCharactersAvoided",
                table: "LegendTranslationSystemUsages",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "LegendTranslationEntitlements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    MonthlyCharacterAllowance = table.Column<long>(type: "bigint", nullable: false),
                    IsUnlimited = table.Column<bool>(type: "bit", nullable: false),
                    EntitlementSource = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    IsFounderOverride = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendTranslationEntitlements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegendTranslationUsageLedgers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestReference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    SourceLanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TargetLanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    BillableCharacters = table.Column<long>(type: "bigint", nullable: false),
                    ProviderExecuted = table.Column<bool>(type: "bit", nullable: false),
                    Succeeded = table.Column<bool>(type: "bit", nullable: false),
                    State = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    FailureCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    ReservationExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendTranslationUsageLedgers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegendTranslationUsagePeriods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    ConsumedCharacters = table.Column<long>(type: "bigint", nullable: false),
                    ReservedCharacters = table.Column<long>(type: "bigint", nullable: false),
                    ProviderBillableCharacters = table.Column<long>(type: "bigint", nullable: false),
                    ProviderOperationCount = table.Column<long>(type: "bigint", nullable: false),
                    SameLanguageCharactersAvoided = table.Column<long>(type: "bigint", nullable: false),
                    TranslationMemoryCharactersAvoided = table.Column<long>(type: "bigint", nullable: false),
                    ContextualCharactersAvoided = table.Column<long>(type: "bigint", nullable: false),
                    QuotaDeniedRequestCount = table.Column<long>(type: "bigint", nullable: false),
                    ProviderFailureCount = table.Column<long>(type: "bigint", nullable: false),
                    GroupUniqueTargetReuseCount = table.Column<long>(type: "bigint", nullable: false),
                    LastTranslationActivityUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendTranslationUsagePeriods", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationEntitlements_UserId_ParticipantType",
                table: "LegendTranslationEntitlements",
                columns: new[] { "UserId", "ParticipantType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationUsageLedgers_PeriodStart_State",
                table: "LegendTranslationUsageLedgers",
                columns: new[] { "PeriodStart", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationUsageLedgers_RequestReference",
                table: "LegendTranslationUsageLedgers",
                column: "RequestReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationUsageLedgers_UserId_ParticipantType_PeriodStart_CreatedUtc",
                table: "LegendTranslationUsageLedgers",
                columns: new[] { "UserId", "ParticipantType", "PeriodStart", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationUsagePeriods_LastTranslationActivityUtc",
                table: "LegendTranslationUsagePeriods",
                column: "LastTranslationActivityUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationUsagePeriods_PeriodStart_ConsumedCharacters",
                table: "LegendTranslationUsagePeriods",
                columns: new[] { "PeriodStart", "ConsumedCharacters" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationUsagePeriods_UserId_ParticipantType_PeriodStart",
                table: "LegendTranslationUsagePeriods",
                columns: new[] { "UserId", "ParticipantType", "PeriodStart" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendTranslationEntitlements");

            migrationBuilder.DropTable(
                name: "LegendTranslationUsageLedgers");

            migrationBuilder.DropTable(
                name: "LegendTranslationUsagePeriods");

            migrationBuilder.DropColumn(
                name: "ContextualCharactersAvoided",
                table: "LegendTranslationSystemUsages");

            migrationBuilder.DropColumn(
                name: "GroupUniqueTargetReuseCount",
                table: "LegendTranslationSystemUsages");

            migrationBuilder.DropColumn(
                name: "ProviderBillableCharacters",
                table: "LegendTranslationSystemUsages");

            migrationBuilder.DropColumn(
                name: "ProviderFailureCount",
                table: "LegendTranslationSystemUsages");

            migrationBuilder.DropColumn(
                name: "ProviderOperationCount",
                table: "LegendTranslationSystemUsages");

            migrationBuilder.DropColumn(
                name: "QuotaDeniedRequestCount",
                table: "LegendTranslationSystemUsages");

            migrationBuilder.DropColumn(
                name: "SameLanguageCharactersAvoided",
                table: "LegendTranslationSystemUsages");

            migrationBuilder.DropColumn(
                name: "TranslationMemoryCharactersAvoided",
                table: "LegendTranslationSystemUsages");
        }
    }
}
