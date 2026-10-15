using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

public partial class AddFounderAssistantRules : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "FounderAssistantRulesJson",
            table: "MobileProfileSettings",
            type: "nvarchar(max)",
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
