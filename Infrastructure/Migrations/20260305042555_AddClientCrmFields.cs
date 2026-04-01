using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientCrmFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CrmLastTouch",
                table: "ClientProfiles",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CrmNextDate",
                table: "ClientProfiles",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CrmNextText",
                table: "ClientProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CrmNotes",
                table: "ClientProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CrmPriority",
                table: "ClientProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CrmStatus",
                table: "ClientProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CrmTags",
                table: "ClientProfiles",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CrmLastTouch",
                table: "ClientProfiles");

            migrationBuilder.DropColumn(
                name: "CrmNextDate",
                table: "ClientProfiles");

            migrationBuilder.DropColumn(
                name: "CrmNextText",
                table: "ClientProfiles");

            migrationBuilder.DropColumn(
                name: "CrmNotes",
                table: "ClientProfiles");

            migrationBuilder.DropColumn(
                name: "CrmPriority",
                table: "ClientProfiles");

            migrationBuilder.DropColumn(
                name: "CrmStatus",
                table: "ClientProfiles");

            migrationBuilder.DropColumn(
                name: "CrmTags",
                table: "ClientProfiles");
        }
    }
}
