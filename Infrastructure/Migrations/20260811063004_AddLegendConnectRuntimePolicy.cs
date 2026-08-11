using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendConnectRuntimePolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ContextualInternalServeCount",
                table: "LegendTranslationPairDemands",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ProviderCharactersConsumed",
                table: "LegendCorpusCandidates",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "LegendConnectRuntimePolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScopeKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    MonthlyProviderCapacityCharacters = table.Column<long>(type: "bigint", nullable: false),
                    LiveTranslationReserveCharacters = table.Column<long>(type: "bigint", nullable: false),
                    MaximumSafeCorpusConsumptionCharacters = table.Column<long>(type: "bigint", nullable: false),
                    CorpusAcquisitionEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LearningEnabled = table.Column<bool>(type: "bit", nullable: false),
                    ContextualCompositionMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ContextualMinimumConfidence = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    PriorityMode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PriorityLanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PriorityPairKey = table.Column<string>(type: "nvarchar(72)", maxLength: 72, nullable: true),
                    PriorityLevel = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    LastLearningWorkerHeartbeatUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastAcquisitionWorkerHeartbeatUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendConnectRuntimePolicies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendConnectRuntimePolicies_ScopeKey",
                table: "LegendConnectRuntimePolicies",
                column: "ScopeKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendConnectRuntimePolicies");

            migrationBuilder.DropColumn(
                name: "ContextualInternalServeCount",
                table: "LegendTranslationPairDemands");

            migrationBuilder.DropColumn(
                name: "ProviderCharactersConsumed",
                table: "LegendCorpusCandidates");
        }
    }
}
