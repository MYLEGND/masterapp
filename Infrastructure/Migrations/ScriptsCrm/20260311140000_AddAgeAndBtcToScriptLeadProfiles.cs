using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations.ScriptsCrm
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260311140000_AddAgeAndBtcToScriptLeadProfiles")]
    public partial class AddAgeAndBtcToScriptLeadProfiles : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Age",
                table: "ScriptLeadProfiles",
                type: "nvarchar(12)",
                maxLength: 12,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Btc",
                table: "ScriptLeadProfiles",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Age",
                table: "ScriptLeadProfiles");

            migrationBuilder.DropColumn(
                name: "Btc",
                table: "ScriptLeadProfiles");
        }
    }
}
