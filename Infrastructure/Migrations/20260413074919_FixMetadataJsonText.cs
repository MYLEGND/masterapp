using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixMetadataJsonText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var jsonType = migrationBuilder.ActiveProvider.Contains(
                "SqlServer",
                StringComparison.OrdinalIgnoreCase)
                ? "nvarchar(max)"
                : "TEXT";

            migrationBuilder.AlterColumn<string>(
                name: "MetadataJson",
                table: "WebsiteLeads",
                type: jsonType,
                nullable: true,
                oldClrType: typeof(string),
                oldType: jsonType,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "MetadataJson",
                table: "AnalyticsEvents",
                type: jsonType,
                nullable: true,
                oldClrType: typeof(string),
                oldType: jsonType,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var jsonType = migrationBuilder.ActiveProvider.Contains(
                "SqlServer",
                StringComparison.OrdinalIgnoreCase)
                ? "nvarchar(max)"
                : "TEXT";

            migrationBuilder.AlterColumn<string>(
                name: "MetadataJson",
                table: "WebsiteLeads",
                type: jsonType,
                nullable: true,
                oldClrType: typeof(string),
                oldType: jsonType,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "MetadataJson",
                table: "AnalyticsEvents",
                type: jsonType,
                nullable: true,
                oldClrType: typeof(string),
                oldType: jsonType,
                oldNullable: true);
        }
    }
}
