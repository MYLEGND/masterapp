using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Infrastructure.Data;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260921173000_ExtendClientContinuationForWebsiteEditor")]
public partial class ExtendClientContinuationForWebsiteEditor : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "CommerceBusinessId",
            table: "ClientIdentityContinuations",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ActorUserId",
            table: "ClientIdentityContinuations",
            type: "nvarchar(450)",
            maxLength: 450,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ActorEmail",
            table: "ClientIdentityContinuations",
            type: "nvarchar(320)",
            maxLength: 320,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_ClientIdentityContinuations_CommerceBusinessId_Purpose_ExpiresUtc",
            table: "ClientIdentityContinuations",
            columns: new[] { "CommerceBusinessId", "Purpose", "ExpiresUtc" });

        migrationBuilder.AddForeignKey(
            name: "FK_ClientIdentityContinuations_CommerceBusinesses_CommerceBusinessId",
            table: "ClientIdentityContinuations",
            column: "CommerceBusinessId",
            principalTable: "CommerceBusinesses",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_ClientIdentityContinuations_CommerceBusinesses_CommerceBusinessId",
            table: "ClientIdentityContinuations");

        migrationBuilder.DropIndex(
            name: "IX_ClientIdentityContinuations_CommerceBusinessId_Purpose_ExpiresUtc",
            table: "ClientIdentityContinuations");

        migrationBuilder.DropColumn(name: "CommerceBusinessId", table: "ClientIdentityContinuations");
        migrationBuilder.DropColumn(name: "ActorUserId", table: "ClientIdentityContinuations");
        migrationBuilder.DropColumn(name: "ActorEmail", table: "ClientIdentityContinuations");
    }
}
