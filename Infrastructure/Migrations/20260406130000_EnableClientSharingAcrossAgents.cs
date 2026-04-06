using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260406130000_EnableClientSharingAcrossAgents")]
    public partial class EnableClientSharingAcrossAgents : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentClients_ClientUserId",
                table: "AgentClients");

            migrationBuilder.CreateIndex(
                name: "IX_AgentClients_ClientUserId",
                table: "AgentClients",
                column: "ClientUserId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentClients_ClientUserId",
                table: "AgentClients");

            migrationBuilder.CreateIndex(
                name: "IX_AgentClients_ClientUserId",
                table: "AgentClients",
                column: "ClientUserId",
                unique: true);
        }
    }
}
