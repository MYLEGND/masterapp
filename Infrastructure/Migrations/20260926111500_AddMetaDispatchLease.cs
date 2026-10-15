using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260926111500_AddMetaDispatchLease")]
public partial class AddMetaDispatchLease : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var sqlite = ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);

        migrationBuilder.AddColumn<string>(
            name: "MetaDispatchClaimToken",
            table: "MetaSignalEvents",
            type: sqlite ? "TEXT" : "nvarchar(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "MetaDispatchClaimedUtc",
            table: "MetaSignalEvents",
            type: sqlite ? "TEXT" : "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "MetaDispatchClaimExpiresUtc",
            table: "MetaSignalEvents",
            type: sqlite ? "TEXT" : "datetime2",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_MetaSignalEvents_MetaServerSent_MetaDispatchClaimExpiresUtc",
            table: "MetaSignalEvents",
            columns: new[] { "MetaServerSent", "MetaDispatchClaimExpiresUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_MetaSignalEvents_MetaServerSent_MetaDispatchClaimExpiresUtc",
            table: "MetaSignalEvents");

        migrationBuilder.DropColumn(name: "MetaDispatchClaimToken", table: "MetaSignalEvents");
        migrationBuilder.DropColumn(name: "MetaDispatchClaimedUtc", table: "MetaSignalEvents");
        migrationBuilder.DropColumn(name: "MetaDispatchClaimExpiresUtc", table: "MetaSignalEvents");
    }
}
