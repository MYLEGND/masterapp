using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace Infrastructure.Migrations;
public partial class TrainingPromotionMemberLimits : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "UsageLimit", table: "CommerceProductDiscounts", type: "int", nullable: true);
        migrationBuilder.AddColumn<System.Guid>(name: "TrainingDiscountId", table: "ClientSubscriptionOffers", type: "uniqueidentifier", nullable: true);
        migrationBuilder.CreateIndex(name: "IX_ClientSubscriptionOffers_TrainingDiscountId_ClientProfileId", table: "ClientSubscriptionOffers", columns: new[] { "TrainingDiscountId", "ClientProfileId" });
        migrationBuilder.AddForeignKey(name: "FK_ClientSubscriptionOffers_CommerceProductDiscounts_TrainingDiscountId", table: "ClientSubscriptionOffers", column: "TrainingDiscountId", principalTable: "CommerceProductDiscounts", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
    }
    protected override void Down(MigrationBuilder migrationBuilder) => throw new System.NotSupportedException(
        "Accepted training promotion identities must be preserved. Use a reviewed forward migration.");
}
