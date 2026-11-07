using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace Infrastructure.Migrations;
public partial class ParfaitMemberPageRestrictions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>(
        name: "ParfaitPageGrantsJson", table: "CommerceBusinessMembers", type: "nvarchar(max)", nullable: true);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new System.NotSupportedException(
        "Native page restrictions must be preserved. Use a reviewed forward migration.");
}
