using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendFounderDiscourseState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegendFounderAiDiscourseConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FounderAgentUserId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NextTurnSequence = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendFounderAiDiscourseConversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegendFounderAiDiscourseTurns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DiscourseConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SequenceNumber = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    MeaningGraphJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AnalysisReasonCode = table.Column<string>(type: "nvarchar(96)", maxLength: 96, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendFounderAiDiscourseTurns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendFounderAiDiscourseTurns_LegendFounderAiDiscourseConversations_DiscourseConversationId",
                        column: x => x.DiscourseConversationId,
                        principalTable: "LegendFounderAiDiscourseConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendFounderAiDiscourseConversations_FounderAgentUserId_ConversationId",
                table: "LegendFounderAiDiscourseConversations",
                columns: new[] { "FounderAgentUserId", "ConversationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendFounderAiDiscourseConversations_UpdatedUtc",
                table: "LegendFounderAiDiscourseConversations",
                column: "UpdatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LegendFounderAiDiscourseTurns_DiscourseConversationId_SequenceNumber",
                table: "LegendFounderAiDiscourseTurns",
                columns: new[] { "DiscourseConversationId", "SequenceNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendFounderAiDiscourseTurns");

            migrationBuilder.DropTable(
                name: "LegendFounderAiDiscourseConversations");
        }
    }
}
