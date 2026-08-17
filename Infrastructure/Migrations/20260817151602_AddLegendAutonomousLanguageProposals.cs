using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendAutonomousLanguageProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TeacherProposalAttemptCount",
                table: "LegendCorpusCandidates",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TeacherProposalFailureCode",
                table: "LegendCorpusCandidates",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TeacherProposalLeaseExpiresUtc",
                table: "LegendCorpusCandidates",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TeacherProposalProcessedUtc",
                table: "LegendCorpusCandidates",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TeacherProposalProcessingState",
                table: "LegendCorpusCandidates",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "NotStarted");

            migrationBuilder.CreateTable(
                name: "LegendLanguageTeacherProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorpusCandidateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposalIdentity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PairKey = table.Column<string>(type: "nvarchar(72)", maxLength: 72, nullable: false),
                    SourceLanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TargetLanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    EvidenceIdentityHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FamilyKey = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    SemanticCategory = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Rationale = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Confidence = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    ProposalPayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CriticApproved = table.Column<bool>(type: "bit", nullable: false),
                    CriticConfidence = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: true),
                    CriticReasonCodesJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    ValidationState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Provenance = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguageTeacherProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendLanguageTeacherProposals_LegendCorpusCandidates_CorpusCandidateId",
                        column: x => x.CorpusCandidateId,
                        principalTable: "LegendCorpusCandidates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendCorpusCandidates_IsApproved_ProcessingState_TeacherProposalProcessingState_TeacherProposalLeaseExpiresUtc_CreatedUtc",
                table: "LegendCorpusCandidates",
                columns: new[] { "IsApproved", "ProcessingState", "TeacherProposalProcessingState", "TeacherProposalLeaseExpiresUtc", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageTeacherProposals_CorpusCandidateId_ValidationState_CreatedUtc",
                table: "LegendLanguageTeacherProposals",
                columns: new[] { "CorpusCandidateId", "ValidationState", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageTeacherProposals_ProposalIdentity",
                table: "LegendLanguageTeacherProposals",
                column: "ProposalIdentity",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendLanguageTeacherProposals");

            migrationBuilder.DropIndex(
                name: "IX_LegendCorpusCandidates_IsApproved_ProcessingState_TeacherProposalProcessingState_TeacherProposalLeaseExpiresUtc_CreatedUtc",
                table: "LegendCorpusCandidates");

            migrationBuilder.DropColumn(
                name: "TeacherProposalAttemptCount",
                table: "LegendCorpusCandidates");

            migrationBuilder.DropColumn(
                name: "TeacherProposalFailureCode",
                table: "LegendCorpusCandidates");

            migrationBuilder.DropColumn(
                name: "TeacherProposalLeaseExpiresUtc",
                table: "LegendCorpusCandidates");

            migrationBuilder.DropColumn(
                name: "TeacherProposalProcessedUtc",
                table: "LegendCorpusCandidates");

            migrationBuilder.DropColumn(
                name: "TeacherProposalProcessingState",
                table: "LegendCorpusCandidates");
        }
    }
}
