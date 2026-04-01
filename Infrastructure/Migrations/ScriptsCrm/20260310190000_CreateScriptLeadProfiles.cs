using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace Infrastructure.Migrations.ScriptsCrm
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260310190000_CreateScriptLeadProfiles")]
    public partial class CreateScriptLeadProfiles : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScriptLeadProfiles",
                columns: table => new
                {
                    LeadId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Bucket = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Phone2 = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    AddressLine = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                    City = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    State = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    County = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    ZipCode = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: true),
                    DOB = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Gender = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    MortgageLender = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    LoanAmount = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    CrmStatus = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    CrmStage = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    CrmOrder = table.Column<long>(type: "bigint", nullable: false),
                    CrmNotes = table.Column<string>(type: "TEXT", nullable: true),
                    CallCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScriptLeadProfiles", x => x.LeadId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScriptLeadProfiles_Bucket",
                table: "ScriptLeadProfiles",
                column: "Bucket");

            migrationBuilder.CreateIndex(
                name: "IX_ScriptLeadProfiles_Email",
                table: "ScriptLeadProfiles",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_ScriptLeadProfiles_Phone",
                table: "ScriptLeadProfiles",
                column: "Phone");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScriptLeadProfiles");
        }
    }
}
