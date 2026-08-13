using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendConnectLanguageIntelligenceReevaluationVersioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CompletedLanguageIntelligenceEvaluatorVersion",
                table: "LegendConnectRuntimePolicies",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LanguageIntelligenceReevaluationCompletedUtc",
                table: "LegendConnectRuntimePolicies",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LanguageIntelligenceReevaluationCursor",
                table: "LegendConnectRuntimePolicies",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LanguageIntelligenceReevaluationPhase",
                table: "LegendConnectRuntimePolicies",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "LanguageIntelligenceReevaluationStartedUtc",
                table: "LegendConnectRuntimePolicies",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetLanguageIntelligenceEvaluatorVersion",
                table: "LegendConnectRuntimePolicies",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletedLanguageIntelligenceEvaluatorVersion",
                table: "LegendConnectRuntimePolicies");

            migrationBuilder.DropColumn(
                name: "LanguageIntelligenceReevaluationCompletedUtc",
                table: "LegendConnectRuntimePolicies");

            migrationBuilder.DropColumn(
                name: "LanguageIntelligenceReevaluationCursor",
                table: "LegendConnectRuntimePolicies");

            migrationBuilder.DropColumn(
                name: "LanguageIntelligenceReevaluationPhase",
                table: "LegendConnectRuntimePolicies");

            migrationBuilder.DropColumn(
                name: "LanguageIntelligenceReevaluationStartedUtc",
                table: "LegendConnectRuntimePolicies");

            migrationBuilder.DropColumn(
                name: "TargetLanguageIntelligenceEvaluatorVersion",
                table: "LegendConnectRuntimePolicies");
        }
    }
}
