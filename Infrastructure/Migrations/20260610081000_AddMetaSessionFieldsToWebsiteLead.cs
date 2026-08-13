using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260610081000_AddMetaSessionFieldsToWebsiteLead")]
    public partial class AddMetaSessionFieldsToWebsiteLead : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var textType = migrationBuilder.ActiveProvider.Contains(
                "SqlServer",
                StringComparison.OrdinalIgnoreCase)
                ? "nvarchar(max)"
                : "TEXT";

            migrationBuilder.AddColumn<string>(
                name: "ClientIpAddress",
                table: "WebsiteLeads",
                type: textType,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientUserAgent",
                table: "WebsiteLeads",
                type: textType,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fbp",
                table: "WebsiteLeads",
                type: textType,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fbc",
                table: "WebsiteLeads",
                type: textType,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientIpAddress",
                table: "WebsiteLeads");

            migrationBuilder.DropColumn(
                name: "ClientUserAgent",
                table: "WebsiteLeads");

            migrationBuilder.DropColumn(
                name: "Fbp",
                table: "WebsiteLeads");

            migrationBuilder.DropColumn(
                name: "Fbc",
                table: "WebsiteLeads");
        }
    }
}
