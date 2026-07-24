using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260724020000_AddMessagingDirectConversationKey")]
public partial class AddMessagingDirectConversationKey : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DirectConversationKey",
            table: "MessageConversations",
            type: "nvarchar(1000)",
            maxLength: 1000,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_MessageConversations_DirectConversationKey",
            table: "MessageConversations",
            column: "DirectConversationKey",
            unique: true,
            filter: "[DirectConversationKey] IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_MessageConversations_DirectConversationKey",
            table: "MessageConversations");

        migrationBuilder.DropColumn(
            name: "DirectConversationKey",
            table: "MessageConversations");
    }
}
