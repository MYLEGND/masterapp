using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260614070000_AddMetaIdentityToWebsiteLeadIntakeLinks")]
    public partial class AddMetaIdentityToWebsiteLeadIntakeLinks : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            string StringType(int length) => migrationBuilder.ActiveProvider.Contains(
                "SqlServer",
                StringComparison.OrdinalIgnoreCase)
                ? $"nvarchar({length})"
                : "TEXT";

            migrationBuilder.AddColumn<string>(
                name: "ClientIpAddress",
                table: "WebsiteLeadIntakeLinks",
                type: StringType(128),
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientUserAgent",
                table: "WebsiteLeadIntakeLinks",
                type: StringType(1024),
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fbc",
                table: "WebsiteLeadIntakeLinks",
                type: StringType(512),
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fbp",
                table: "WebsiteLeadIntakeLinks",
                type: StringType(256),
                maxLength: 256,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientIpAddress",
                table: "WebsiteLeadIntakeLinks");

            migrationBuilder.DropColumn(
                name: "ClientUserAgent",
                table: "WebsiteLeadIntakeLinks");

            migrationBuilder.DropColumn(
                name: "Fbc",
                table: "WebsiteLeadIntakeLinks");

            migrationBuilder.DropColumn(
                name: "Fbp",
                table: "WebsiteLeadIntakeLinks");
        }
    }
}
