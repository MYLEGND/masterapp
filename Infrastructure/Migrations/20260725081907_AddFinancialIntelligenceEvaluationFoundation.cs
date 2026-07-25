using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <summary>
/// Additive, provider-neutral Financial Intelligence evaluation storage.
/// Journey Circles is intentionally absent because it is already created by
/// the existing 20260724030000_AddJourneyCircles migration.
/// </summary>
public partial class AddFinancialIntelligenceEvaluationFoundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ClientFinancialIntelligenceProfiles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ClientProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                DataCompletenessScore = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                BehavioralBaselineStatus = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                PersonalizationMaturity = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                RecommendationResponseSummary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                CurrentRiskSummary = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: false),
                CurrentOpportunitySummary = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: false),
                CurrentLeakageSummary = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: false),
                EvaluationSequence = table.Column<int>(type: "int", nullable: false),
                LastEvaluatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
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
            name: "FinancialFindings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ClientProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                FindingKey = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                RuleIdentifier = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                RuleVersion = table.Column<int>(type: "int", nullable: false),
                Category = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                FindingType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Title = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                Explanation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                EstimatedImpact = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                ImpactUnit = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                Confidence = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                PriorityScore = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: false),
                Urgency = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                Difficulty = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                EvidenceSummary = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                ClientFacingSummary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                AgentFacingSummary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                Disclaimer = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                RequiresAgentReview = table.Column<bool>(type: "bit", nullable: false),
                AgentReviewedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                AgentReviewedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                FirstDetectedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                LastDetectedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                ResolvedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
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
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ClientProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ObservationKey = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                RuleIdentifier = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                RuleVersion = table.Column<int>(type: "int", nullable: false),
                ObservationType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                SourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                SourceReference = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                PeriodStartUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                PeriodEndUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                NumericValue = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                PreviousValue = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                Unit = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                Confidence = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                EvidenceSummary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                SupersededUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
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
            name: "FinancialFindingFeedback",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                FinancialFindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ClientProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ActorType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                FeedbackType = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                ReasonCode = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
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
                FinancialFindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                FinancialObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
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
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "FinancialFindingObservations");
        migrationBuilder.DropTable(name: "FinancialFindingFeedback");
        migrationBuilder.DropTable(name: "FinancialFindings");
        migrationBuilder.DropTable(name: "FinancialObservations");
        migrationBuilder.DropTable(name: "ClientFinancialIntelligenceProfiles");
    }
}
