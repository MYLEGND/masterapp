using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWebsiteLeadSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeleteReason",
                table: "WebsiteLeads",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAtUtc",
                table: "WebsiteLeads",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedByUserId",
                table: "WebsiteLeads",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "WebsiteLeads",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeleteReason",
                table: "WebsiteLeads");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "WebsiteLeads");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                table: "WebsiteLeads");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "WebsiteLeads");
        }
    }
}
