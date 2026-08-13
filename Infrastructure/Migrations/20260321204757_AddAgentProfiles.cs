using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260321204757_AddAgentProfiles")]
    public partial class AddAgentProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var isSqlServer = migrationBuilder.ActiveProvider.Contains(
                "SqlServer",
                StringComparison.OrdinalIgnoreCase);

            string GuidType() => isSqlServer ? "uniqueidentifier" : "TEXT";
            string DateType() => isSqlServer ? "datetime2" : "TEXT";
            string StringType(int maxLength) =>
                isSqlServer ? $"nvarchar({maxLength})" : "TEXT";

            migrationBuilder.CreateTable(
                name: "AgentProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: GuidType(), nullable: false),
                    AgentUserId = table.Column<string>(type: StringType(450), maxLength: 450, nullable: false),
                    AgentUpn = table.Column<string>(type: StringType(320), maxLength: 320, nullable: false),
                    FullName = table.Column<string>(type: StringType(200), maxLength: 200, nullable: true),
                    Npn = table.Column<string>(type: StringType(64), maxLength: 64, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: DateType(), nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: DateType(), nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentProfiles_AgentUpn",
                table: "AgentProfiles",
                column: "AgentUpn");

            migrationBuilder.CreateIndex(
                name: "IX_AgentProfiles_AgentUserId",
                table: "AgentProfiles",
                column: "AgentUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentProfiles");
        }
    }
}
