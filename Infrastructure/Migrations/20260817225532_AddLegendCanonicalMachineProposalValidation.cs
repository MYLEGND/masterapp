using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendCanonicalMachineProposalValidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CanonicalValidatedUtc",
                table: "LegendLanguageTeacherProposals",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CanonicalValidationAttemptCount",
                table: "LegendLanguageTeacherProposals",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CanonicalValidationFailureCode",
                table: "LegendLanguageTeacherProposals",
                type: "nvarchar(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CanonicalValidationLeaseExpiresUtc",
                table: "LegendLanguageTeacherProposals",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageTeacherProposals_ValidationState_CanonicalValidationLeaseExpiresUtc_CreatedUtc",
                table: "LegendLanguageTeacherProposals",
                columns: new[] { "ValidationState", "CanonicalValidationLeaseExpiresUtc", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageTeacherProposals_ValidationState_CanonicalValidationLeaseExpiresUtc_CreatedUtc",
                table: "LegendLanguageTeacherProposals");

            migrationBuilder.DropColumn(
                name: "CanonicalValidatedUtc",
                table: "LegendLanguageTeacherProposals");

            migrationBuilder.DropColumn(
                name: "CanonicalValidationAttemptCount",
                table: "LegendLanguageTeacherProposals");

            migrationBuilder.DropColumn(
                name: "CanonicalValidationFailureCode",
                table: "LegendLanguageTeacherProposals");

            migrationBuilder.DropColumn(
                name: "CanonicalValidationLeaseExpiresUtc",
                table: "LegendLanguageTeacherProposals");
        }
    }
}
