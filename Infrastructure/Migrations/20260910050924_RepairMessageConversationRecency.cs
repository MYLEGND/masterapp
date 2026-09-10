using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RepairMessageConversationRecency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Repair the existing indexed inbox projection from its canonical
            // messages. The send path now tracks this row so future sends keep
            // the projection current. Empty drafts have no activity to repair.
            migrationBuilder.Sql("""
                UPDATE [MessageConversations]
                SET [LastMessageUtc] = (
                    SELECT MAX([SentUtc]) FROM [InternalMessages]
                    WHERE [ConversationId] = [MessageConversations].[Id])
                WHERE EXISTS (
                    SELECT 1 FROM [InternalMessages]
                    WHERE [ConversationId] = [MessageConversations].[Id])
                  AND ([LastMessageUtc] IS NULL OR [LastMessageUtc] <> (
                    SELECT MAX([SentUtc]) FROM [InternalMessages]
                    WHERE [ConversationId] = [MessageConversations].[Id]));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data reconciliation has no inverse: never restore stale activity.
        }
    }
}
