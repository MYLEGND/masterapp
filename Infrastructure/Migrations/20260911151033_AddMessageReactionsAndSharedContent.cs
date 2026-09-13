using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageReactionsAndSharedContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SharedSocialPostId",
                table: "InternalMessages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MessageReactions",
                columns: table => new
                {
                    InternalMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Emoji = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageReactions", x => new { x.InternalMessageId, x.ActorProfileId, x.ParticipantType });
                    table.ForeignKey(
                        name: "FK_MessageReactions_InternalMessages_InternalMessageId",
                        column: x => x.InternalMessageId,
                        principalTable: "InternalMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageReactions");

            migrationBuilder.DropColumn(
                name: "SharedSocialPostId",
                table: "InternalMessages");
        }
    }
}
