using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace Infrastructure.Migrations;

public partial class AddWebsiteSnapshotRetirement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<System.DateTime>(
            name: "SnapshotRetiredUtc", table: "WebsiteContentVersion",
            type: "datetime2", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "SnapshotRetiredUtc", table: "WebsiteContentVersion");
}
