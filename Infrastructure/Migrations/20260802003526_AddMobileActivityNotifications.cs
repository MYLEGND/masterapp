using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMobileActivityNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MobileActivityNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipientUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RecipientParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ControlledResourceRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MobileActivityNotifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MobileActivityNotifications_ControlledResourceRequestId",
                table: "MobileActivityNotifications",
                column: "ControlledResourceRequestId",
                unique: true,
                filter: "[ControlledResourceRequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MobileActivityNotifications_RecipientUserId_RecipientParticipantType_OccurredUtc",
                table: "MobileActivityNotifications",
                columns: new[] { "RecipientUserId", "RecipientParticipantType", "OccurredUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MobileActivityNotifications");
        }
    }
}
