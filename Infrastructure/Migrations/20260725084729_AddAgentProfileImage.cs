using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentProfileImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var isSqlServer = migrationBuilder.ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase);

            migrationBuilder.AddColumn<byte[]>(
                name: "ProfileImageContent",
                table: "AgentProfiles",
                type: isSqlServer ? "varbinary(max)" : "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProfileImageContentType",
                table: "AgentProfiles",
                type: isSqlServer ? "nvarchar(127)" : "TEXT",
                maxLength: 127,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProfileImageContent",
                table: "AgentProfiles");

            migrationBuilder.DropColumn(
                name: "ProfileImageContentType",
                table: "AgentProfiles");
        }
    }
}
