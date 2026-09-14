using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMessagingConnectionLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MessagingConnectionLeases",
                columns: table => new
                {
                    ConnectionId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParticipantType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessagingConnectionLeases", x => x.ConnectionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessagingConnectionLeases_ExpiresUtc",
                table: "MessagingConnectionLeases",
                column: "ExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MessagingConnectionLeases_ProfileId_ParticipantType_ExpiresUtc",
                table: "MessagingConnectionLeases",
                columns: new[] { "ProfileId", "ParticipantType", "ExpiresUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessagingConnectionLeases");
        }
    }
}
