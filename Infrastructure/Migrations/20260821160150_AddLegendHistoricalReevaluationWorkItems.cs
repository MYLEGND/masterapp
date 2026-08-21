using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendHistoricalReevaluationWorkItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CursorReplayCompatibilityEvaluatorVersion",
                table: "LegendConnectRuntimePolicies",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Capture the cursor contract that existed at migration time.
            // Earlier in-flight phases retain their bounded cursor. When an
            // in-flight pass reaches ProviderObservations, the new worker
            // atomically records the exact cursor boundary and adopts only
            // the remaining ordered suffix into durable work; no replay is
            // reset and no canonical knowledge is copied.
            migrationBuilder.Sql("""
                UPDATE [LegendConnectRuntimePolicies]
                SET [CursorReplayCompatibilityEvaluatorVersion] = CASE
                    WHEN [TargetLanguageIntelligenceEvaluatorVersion] > [CompletedLanguageIntelligenceEvaluatorVersion]
                        THEN [TargetLanguageIntelligenceEvaluatorVersion]
                    WHEN [CompletedLanguageIntelligenceEvaluatorVersion] > 0
                        THEN [CompletedLanguageIntelligenceEvaluatorVersion]
                    ELSE 0
                END
                WHERE [CursorReplayCompatibilityEvaluatorVersion] = 0;
                """);

            migrationBuilder.CreateTable(
                name: "LegendHistoricalReevaluationWorkItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvaluatorVersion = table.Column<int>(type: "int", nullable: false),
                    Phase = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    WorkKind = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    WorkIdentity = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SubjectScope = table.Column<string>(type: "nvarchar(96)", maxLength: 96, nullable: false),
                    DependencyIdentity = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    ProcessingState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LastErrorCode = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    LastErrorMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendHistoricalReevaluationWorkItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendHistoricalReevaluationWorkItems_ActiveDependency",
                table: "LegendHistoricalReevaluationWorkItems",
                columns: new[] { "EvaluatorVersion", "Phase", "DependencyIdentity" },
                unique: true,
                filter: "[ProcessingState] = 'Processing'");

            migrationBuilder.CreateIndex(
                name: "IX_LegendHistoricalReevaluationWorkItems_Claim",
                table: "LegendHistoricalReevaluationWorkItems",
                columns: new[] { "EvaluatorVersion", "Phase", "ProcessingState", "LeaseExpiresUtc", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendHistoricalReevaluationWorkItems_Identity",
                table: "LegendHistoricalReevaluationWorkItems",
                columns: new[] { "EvaluatorVersion", "Phase", "WorkKind", "WorkIdentity" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendHistoricalReevaluationWorkItems");

            migrationBuilder.DropColumn(
                name: "CursorReplayCompatibilityEvaluatorVersion",
                table: "LegendConnectRuntimePolicies");
        }
    }
}
