using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFounderAiActionAuthorizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FounderAiActionAuthorizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActionDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ScopeDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    SessionId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AuthorizationVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ToolName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AuthorizationKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CanonicalArgumentsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 32768, nullable: false),
                    ApprovedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ExecutionStartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", maxLength: 32768, nullable: true),
                    Revision = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FounderAiActionAuthorizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FounderAiActionAuthorizations_MessageConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "MessageConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FounderAiActionAuthorizations_ActionDigest",
                table: "FounderAiActionAuthorizations",
                column: "ActionDigest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FounderAiActionAuthorizations_ConversationId",
                table: "FounderAiActionAuthorizations",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_FounderAiActionAuthorizations_ExpiresUtc",
                table: "FounderAiActionAuthorizations",
                column: "ExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FounderAiActionAuthorizations_UserId_RequestId",
                table: "FounderAiActionAuthorizations",
                columns: new[] { "UserId", "RequestId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FounderAiActionAuthorizations");
        }
    }
}
