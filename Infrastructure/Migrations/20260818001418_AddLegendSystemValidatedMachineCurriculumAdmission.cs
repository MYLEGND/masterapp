using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendSystemValidatedMachineCurriculumAdmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CurriculumAdmissionAttemptCount",
                table: "LegendLanguageTeacherProposals",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CurriculumAdmissionFailureCode",
                table: "LegendLanguageTeacherProposals",
                type: "nvarchar(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CurriculumAdmissionLeaseExpiresUtc",
                table: "LegendLanguageTeacherProposals",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CurriculumAdmittedUtc",
                table: "LegendLanguageTeacherProposals",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageTeacherProposals_ValidationState_CurriculumAdmissionLeaseExpiresUtc_CreatedUtc",
                table: "LegendLanguageTeacherProposals",
                columns: new[] { "ValidationState", "CurriculumAdmissionLeaseExpiresUtc", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageTeacherProposals_ValidationState_CurriculumAdmissionLeaseExpiresUtc_CreatedUtc",
                table: "LegendLanguageTeacherProposals");

            migrationBuilder.DropColumn(
                name: "CurriculumAdmissionAttemptCount",
                table: "LegendLanguageTeacherProposals");

            migrationBuilder.DropColumn(
                name: "CurriculumAdmissionFailureCode",
                table: "LegendLanguageTeacherProposals");

            migrationBuilder.DropColumn(
                name: "CurriculumAdmissionLeaseExpiresUtc",
                table: "LegendLanguageTeacherProposals");

            migrationBuilder.DropColumn(
                name: "CurriculumAdmittedUtc",
                table: "LegendLanguageTeacherProposals");
        }
    }
}
