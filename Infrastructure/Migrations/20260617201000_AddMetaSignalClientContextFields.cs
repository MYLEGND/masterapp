using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260617201000_AddMetaSignalClientContextFields")]
    public partial class AddMetaSignalClientContextFields : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var isSqlServer = migrationBuilder.ActiveProvider.Contains(
                "SqlServer",
                StringComparison.OrdinalIgnoreCase);
            var textType = isSqlServer ? "nvarchar(max)" : "TEXT";
            var integerType = isSqlServer ? "int" : "INTEGER";
            var boolType = isSqlServer ? "bit" : "INTEGER";

            migrationBuilder.AddColumn<string>(
                name: "DeviceType",
                table: "MetaSignalEvents",
                type: textType,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Browser",
                table: "MetaSignalEvents",
                type: textType,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OperatingSystem",
                table: "MetaSignalEvents",
                type: textType,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserAgent",
                table: "MetaSignalEvents",
                type: textType,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ViewportWidth",
                table: "MetaSignalEvents",
                type: integerType,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ViewportHeight",
                table: "MetaSignalEvents",
                type: integerType,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScreenWidth",
                table: "MetaSignalEvents",
                type: integerType,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScreenHeight",
                table: "MetaSignalEvents",
                type: integerType,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WebDriver",
                table: "MetaSignalEvents",
                type: boolType,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsHeadless",
                table: "MetaSignalEvents",
                type: boolType,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MouseMoveCount",
                table: "MetaSignalEvents",
                type: integerType,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HumanInteractionCount",
                table: "MetaSignalEvents",
                type: integerType,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VisibilityChangeCount",
                table: "MetaSignalEvents",
                type: integerType,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "MetaSignalEvents",
                type: textType,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                table: "MetaSignalEvents",
                type: textType,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "DeviceType", table: "MetaSignalEvents");
            migrationBuilder.DropColumn(name: "Browser", table: "MetaSignalEvents");
            migrationBuilder.DropColumn(name: "OperatingSystem", table: "MetaSignalEvents");
            migrationBuilder.DropColumn(name: "UserAgent", table: "MetaSignalEvents");
            migrationBuilder.DropColumn(name: "ViewportWidth", table: "MetaSignalEvents");
            migrationBuilder.DropColumn(name: "ViewportHeight", table: "MetaSignalEvents");
            migrationBuilder.DropColumn(name: "ScreenWidth", table: "MetaSignalEvents");
            migrationBuilder.DropColumn(name: "ScreenHeight", table: "MetaSignalEvents");
            migrationBuilder.DropColumn(name: "WebDriver", table: "MetaSignalEvents");
            migrationBuilder.DropColumn(name: "IsHeadless", table: "MetaSignalEvents");
            migrationBuilder.DropColumn(name: "MouseMoveCount", table: "MetaSignalEvents");
            migrationBuilder.DropColumn(name: "HumanInteractionCount", table: "MetaSignalEvents");
            migrationBuilder.DropColumn(name: "VisibilityChangeCount", table: "MetaSignalEvents");
            migrationBuilder.DropColumn(name: "Language", table: "MetaSignalEvents");
            migrationBuilder.DropColumn(name: "TimeZone", table: "MetaSignalEvents");
        }
    }
}
