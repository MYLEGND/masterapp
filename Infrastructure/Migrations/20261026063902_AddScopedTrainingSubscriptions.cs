using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddScopedTrainingSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientSubscriptions_ClientProfileId",
                table: "ClientSubscriptions");

            migrationBuilder.AddColumn<string>(
                name: "BillingKind",
                table: "CommerceProducts",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "OneTime");

            migrationBuilder.AddColumn<Guid>(
                name: "CommerceBusinessId",
                table: "ClientSubscriptions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CommerceProductId",
                table: "ClientSubscriptions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubscriptionScope",
                table: "ClientSubscriptions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "LegendMembership");

            migrationBuilder.AddColumn<Guid>(
                name: "CommerceBusinessId",
                table: "ClientSubscriptionOffers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CommerceProductId",
                table: "ClientSubscriptionOffers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubscriptionScope",
                table: "ClientSubscriptionOffers",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "LegendMembership");

            migrationBuilder.AddColumn<string>(
                name: "TrainingBenefits",
                table: "ClientSubscriptionOffers",
                type: "nvarchar(max)",
                maxLength: 12000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrainingPlanName",
                table: "ClientSubscriptionOffers",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrainingPromotionCode",
                table: "ClientSubscriptionOffers",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientSubscriptions_ClientProfileId_SubscriptionScope",
                table: "ClientSubscriptions",
                columns: new[] { "ClientProfileId", "SubscriptionScope" },
                unique: true,
                filter: "[Status] <> 'Canceled' AND [Status] <> 'ActivationFailed'");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSubscriptions_CommerceBusinessId",
                table: "ClientSubscriptions",
                column: "CommerceBusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSubscriptions_CommerceProductId",
                table: "ClientSubscriptions",
                column: "CommerceProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSubscriptionOffers_CommerceBusinessId",
                table: "ClientSubscriptionOffers",
                column: "CommerceBusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSubscriptionOffers_CommerceProductId",
                table: "ClientSubscriptionOffers",
                column: "CommerceProductId");

            migrationBuilder.AddForeignKey(
                name: "FK_ClientSubscriptionOffers_CommerceBusinesses_CommerceBusinessId",
                table: "ClientSubscriptionOffers",
                column: "CommerceBusinessId",
                principalTable: "CommerceBusinesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientSubscriptionOffers_CommerceProducts_CommerceProductId",
                table: "ClientSubscriptionOffers",
                column: "CommerceProductId",
                principalTable: "CommerceProducts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientSubscriptions_CommerceBusinesses_CommerceBusinessId",
                table: "ClientSubscriptions",
                column: "CommerceBusinessId",
                principalTable: "CommerceBusinesses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientSubscriptions_CommerceProducts_CommerceProductId",
                table: "ClientSubscriptions",
                column: "CommerceProductId",
                principalTable: "CommerceProducts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClientSubscriptionOffers_CommerceBusinesses_CommerceBusinessId",
                table: "ClientSubscriptionOffers");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientSubscriptionOffers_CommerceProducts_CommerceProductId",
                table: "ClientSubscriptionOffers");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientSubscriptions_CommerceBusinesses_CommerceBusinessId",
                table: "ClientSubscriptions");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientSubscriptions_CommerceProducts_CommerceProductId",
                table: "ClientSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_ClientSubscriptions_ClientProfileId_SubscriptionScope",
                table: "ClientSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_ClientSubscriptions_CommerceBusinessId",
                table: "ClientSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_ClientSubscriptions_CommerceProductId",
                table: "ClientSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_ClientSubscriptionOffers_CommerceBusinessId",
                table: "ClientSubscriptionOffers");

            migrationBuilder.DropIndex(
                name: "IX_ClientSubscriptionOffers_CommerceProductId",
                table: "ClientSubscriptionOffers");

            migrationBuilder.DropColumn(
                name: "BillingKind",
                table: "CommerceProducts");

            migrationBuilder.DropColumn(
                name: "CommerceBusinessId",
                table: "ClientSubscriptions");

            migrationBuilder.DropColumn(
                name: "CommerceProductId",
                table: "ClientSubscriptions");

            migrationBuilder.DropColumn(
                name: "SubscriptionScope",
                table: "ClientSubscriptions");

            migrationBuilder.DropColumn(
                name: "CommerceBusinessId",
                table: "ClientSubscriptionOffers");

            migrationBuilder.DropColumn(
                name: "CommerceProductId",
                table: "ClientSubscriptionOffers");

            migrationBuilder.DropColumn(
                name: "SubscriptionScope",
                table: "ClientSubscriptionOffers");

            migrationBuilder.DropColumn(
                name: "TrainingBenefits",
                table: "ClientSubscriptionOffers");

            migrationBuilder.DropColumn(
                name: "TrainingPlanName",
                table: "ClientSubscriptionOffers");

            migrationBuilder.DropColumn(
                name: "TrainingPromotionCode",
                table: "ClientSubscriptionOffers");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSubscriptions_ClientProfileId",
                table: "ClientSubscriptions",
                column: "ClientProfileId",
                unique: true,
                filter: "[Status] <> 'Canceled' AND [Status] <> 'ActivationFailed'");
        }
    }
}
