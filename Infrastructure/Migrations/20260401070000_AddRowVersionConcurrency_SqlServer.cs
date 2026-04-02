using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260401070000_AddRowVersionConcurrency_SqlServer")]
    public partial class AddRowVersionConcurrency_SqlServer : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite already handled this in AddRowVersionConcurrency (20260401061601).
            if (!migrationBuilder.ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
                return;

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "WorkstationLeadProfiles",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "ProductionRecords",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "ClientProfiles",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (!migrationBuilder.ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
                return;

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "WorkstationLeadProfiles");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "ProductionRecords");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "ClientProfiles");
        }
    }
}
