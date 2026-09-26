using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260925163000_AddWebsiteCommerceScope")]
public partial class AddWebsiteCommerceScope : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "CommerceBusinessId",
            table: "WebsiteContentState",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_WebsiteContentState_CommerceBusinessId",
            table: "WebsiteContentState",
            column: "CommerceBusinessId");

        migrationBuilder.AddForeignKey(
            name: "FK_WebsiteContentState_CommerceBusinesses_CommerceBusinessId",
            table: "WebsiteContentState",
            column: "CommerceBusinessId",
            principalTable: "CommerceBusinesses",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_WebsiteContentState_CommerceBusinesses_CommerceBusinessId",
            table: "WebsiteContentState");

        migrationBuilder.DropIndex(
            name: "IX_WebsiteContentState_CommerceBusinessId",
            table: "WebsiteContentState");

        migrationBuilder.DropColumn(
            name: "CommerceBusinessId",
            table: "WebsiteContentState");
    }
}
