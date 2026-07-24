using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSecureInternalMessaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientAgentMessagingGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    AgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    GrantedByAgentUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    GrantedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientAgentMessagingGrants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MessageConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 240, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastMessageUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsClosed = table.Column<bool>(type: "INTEGER", nullable: false),
                    ClosedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageConversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MessagingAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    InternalMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessagingAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InternalMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SenderUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    SenderType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 10000, nullable: false),
                    SentUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EditedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    ClientMessageId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InternalMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InternalMessages_MessageConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "MessageConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MessageConversationParticipants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    ParticipantType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    JoinedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LeftUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastReadUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastReadMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsMuted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageConversationParticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageConversationParticipants_MessageConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "MessageConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MessageAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InternalMessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginalFileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    StoredFileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    StoragePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    ScanStatus = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageAttachments_InternalMessages_InternalMessageId",
                        column: x => x.InternalMessageId,
                        principalTable: "InternalMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientAgentMessagingGrants_AgentUserId_IsActive",
                table: "ClientAgentMessagingGrants",
                columns: new[] { "AgentUserId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientAgentMessagingGrants_ClientUserId_AgentUserId",
                table: "ClientAgentMessagingGrants",
                columns: new[] { "ClientUserId", "AgentUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientAgentMessagingGrants_ClientUserId_IsActive",
                table: "ClientAgentMessagingGrants",
                columns: new[] { "ClientUserId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_InternalMessages_ClientMessageId",
                table: "InternalMessages",
                column: "ClientMessageId",
                unique: true,
                filter: "\"ClientMessageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InternalMessages_ConversationId_SentUtc",
                table: "InternalMessages",
                columns: new[] { "ConversationId", "SentUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageAttachments_InternalMessageId",
                table: "MessageAttachments",
                column: "InternalMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageConversationParticipants_ConversationId_UserId",
                table: "MessageConversationParticipants",
                columns: new[] { "ConversationId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageConversationParticipants_UserId_IsActive",
                table: "MessageConversationParticipants",
                columns: new[] { "UserId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageConversations_IsClosed",
                table: "MessageConversations",
                column: "IsClosed");

            migrationBuilder.CreateIndex(
                name: "IX_MessageConversations_LastMessageUtc",
                table: "MessageConversations",
                column: "LastMessageUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MessagingAuditEntries_ActorUserId",
                table: "MessagingAuditEntries",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MessagingAuditEntries_ConversationId",
                table: "MessagingAuditEntries",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_MessagingAuditEntries_CreatedUtc",
                table: "MessagingAuditEntries",
                column: "CreatedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientAgentMessagingGrants");

            migrationBuilder.DropTable(
                name: "MessageAttachments");

            migrationBuilder.DropTable(
                name: "MessageConversationParticipants");

            migrationBuilder.DropTable(
                name: "MessagingAuditEntries");

            migrationBuilder.DropTable(
                name: "InternalMessages");

            migrationBuilder.DropTable(
                name: "MessageConversations");
        }
    }
}
