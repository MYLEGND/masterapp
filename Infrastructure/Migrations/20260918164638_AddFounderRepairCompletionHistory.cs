using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFounderRepairCompletionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CompletionVerifiedUtc",
                table: "FounderSoftwareRepairBatches",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeployedTreeSha",
                table: "FounderSoftwareRepairBatches",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeploymentEvidenceJson",
                table: "FounderSoftwareRepairBatches",
                type: "nvarchar(max)",
                maxLength: 16000,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DeploymentRunId",
                table: "FounderSoftwareRepairBatches",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergedSha",
                table: "FounderSoftwareRepairBatches",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviewBranch",
                table: "FounderSoftwareRepairBatches",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "hotfix/staging-batch");

            migrationBuilder.AddColumn<string>(
                name: "ReviewedHeadSha",
                table: "FounderSoftwareRepairBatches",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_FounderSoftwareRepairBatches_CompletionVerifiedUtc",
                table: "FounderSoftwareRepairBatches",
                column: "CompletionVerifiedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FounderSoftwareRepairBatches_CompletionVerifiedUtc",
                table: "FounderSoftwareRepairBatches");

            migrationBuilder.DropColumn(
                name: "CompletionVerifiedUtc",
                table: "FounderSoftwareRepairBatches");

            migrationBuilder.DropColumn(
                name: "DeployedTreeSha",
                table: "FounderSoftwareRepairBatches");

            migrationBuilder.DropColumn(
                name: "DeploymentEvidenceJson",
                table: "FounderSoftwareRepairBatches");

            migrationBuilder.DropColumn(
                name: "DeploymentRunId",
                table: "FounderSoftwareRepairBatches");

            migrationBuilder.DropColumn(
                name: "MergedSha",
                table: "FounderSoftwareRepairBatches");

            migrationBuilder.DropColumn(
                name: "PreviewBranch",
                table: "FounderSoftwareRepairBatches");

            migrationBuilder.DropColumn(
                name: "ReviewedHeadSha",
                table: "FounderSoftwareRepairBatches");
        }
    }
}
