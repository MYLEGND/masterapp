using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMessagingReplyAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReplyToMessageId",
                table: "InternalMessages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InternalMessages_ReplyToMessageId",
                table: "InternalMessages",
                column: "ReplyToMessageId");

            migrationBuilder.AddForeignKey(
                name: "FK_InternalMessages_InternalMessages_ReplyToMessageId",
                table: "InternalMessages",
                column: "ReplyToMessageId",
                principalTable: "InternalMessages",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InternalMessages_InternalMessages_ReplyToMessageId",
                table: "InternalMessages");

            migrationBuilder.DropIndex(
                name: "IX_InternalMessages_ReplyToMessageId",
                table: "InternalMessages");

            migrationBuilder.DropColumn(
                name: "ReplyToMessageId",
                table: "InternalMessages");
        }
    }
}
