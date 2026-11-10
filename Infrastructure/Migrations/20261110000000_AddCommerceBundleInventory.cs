using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

public partial class AddCommerceBundleInventory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("BundleComponentsJson", "CommerceProducts", type: "nvarchar(max)", nullable: true);
        migrationBuilder.AddColumn<string>("InventoryRequirementsJson", "CommerceOrderLines", type: "nvarchar(max)", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("BundleComponentsJson", "CommerceProducts");
        migrationBuilder.DropColumn("InventoryRequirementsJson", "CommerceOrderLines");
    }
}
