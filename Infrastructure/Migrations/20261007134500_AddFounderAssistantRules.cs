using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20261007134500_AddFounderAssistantRules")]
public partial class AddFounderAssistantRules : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "FounderAssistantRulesJson",
            table: "MobileProfileSettings",
            type: "nvarchar(16000)",
            maxLength: 16000,
            nullable: false,
            defaultValue: "[]");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "FounderAssistantRulesJson",
            table: "MobileProfileSettings");
    }
}
