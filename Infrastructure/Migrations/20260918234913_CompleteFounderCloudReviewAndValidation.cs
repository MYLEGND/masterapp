using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompleteFounderCloudReviewAndValidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CandidateValidationEvidenceJson",
                table: "FounderSoftwareRepairBatches",
                type: "nvarchar(max)",
                maxLength: 16000,
                nullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "ApprovedUtc",
                table: "FounderAiActionAuthorizations",
                type: "datetime2",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedUtc",
                table: "FounderAiActionAuthorizations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentProposalId",
                table: "FounderAiActionAuthorizations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewBindingJson",
                table: "FounderAiActionAuthorizations",
                type: "nvarchar(max)",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_FounderAiActionAuthorizations_ParentProposalId",
                table: "FounderAiActionAuthorizations",
                column: "ParentProposalId",
                unique: true,
                filter: "[ParentProposalId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FounderAiActionAuthorizations_ParentProposalId",
                table: "FounderAiActionAuthorizations");

            migrationBuilder.DropColumn(
                name: "CandidateValidationEvidenceJson",
                table: "FounderSoftwareRepairBatches");

            migrationBuilder.DropColumn(
                name: "CreatedUtc",
                table: "FounderAiActionAuthorizations");

            migrationBuilder.DropColumn(
                name: "ParentProposalId",
                table: "FounderAiActionAuthorizations");

            migrationBuilder.DropColumn(
                name: "ReviewBindingJson",
                table: "FounderAiActionAuthorizations");

            migrationBuilder.AlterColumn<DateTime>(
                name: "ApprovedUtc",
                table: "FounderAiActionAuthorizations",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldNullable: true);
        }
    }
}
