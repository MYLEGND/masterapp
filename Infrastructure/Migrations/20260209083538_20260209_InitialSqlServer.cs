using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class _20260209_InitialSqlServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentClients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", maxLength: 450, nullable: false),
                    AgentUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ClientUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    AgentUpn = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentClients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", maxLength: 450, nullable: false),
                    ClientUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    FirstName = table.Column<string>(type: "TEXT", nullable: false),
                    LastName = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    Phone = table.Column<string>(type: "TEXT", nullable: false),
                    DOB = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MaritalStatus = table.Column<string>(type: "TEXT", nullable: false),
                    SignificantOtherFirstName = table.Column<string>(type: "TEXT", nullable: true),
                    SignificantOtherLastName = table.Column<string>(type: "TEXT", nullable: true),
                    SignificantOtherDOB = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SignificantOtherEmail = table.Column<string>(type: "TEXT", nullable: true),
                    SignificantOtherPhone = table.Column<string>(type: "TEXT", nullable: true),
                    AgentNotes = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HouseholdMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", maxLength: 450, nullable: false),
                    ClientUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RelationshipType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FirstName = table.Column<string>(type: "TEXT", nullable: false),
                    LastName = table.Column<string>(type: "TEXT", nullable: false),
                    DOB = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    Phone = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HouseholdMembers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentClients_AgentUserId_ClientUserId",
                table: "AgentClients",
                columns: new[] { "AgentUserId", "ClientUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientProfiles_ClientUserId",
                table: "ClientProfiles",
                column: "ClientUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdMembers_ClientUserId",
                table: "HouseholdMembers",
                column: "ClientUserId");

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdMembers_ClientUserId_RelationshipType",
                table: "HouseholdMembers",
                columns: new[] { "ClientUserId", "RelationshipType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentClients");

            migrationBuilder.DropTable(
                name: "ClientProfiles");

            migrationBuilder.DropTable(
                name: "HouseholdMembers");
        }
    }
}
