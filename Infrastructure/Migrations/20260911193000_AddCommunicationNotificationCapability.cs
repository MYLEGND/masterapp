using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260911193000_AddCommunicationNotificationCapability")]
public sealed partial class AddCommunicationNotificationCapability : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<bool>(
        name: "SupportsCommunicationNotifications", table: "MobilePushDevices", type: "bit", nullable: false, defaultValue: false);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
        name: "SupportsCommunicationNotifications", table: "MobilePushDevices");
}
