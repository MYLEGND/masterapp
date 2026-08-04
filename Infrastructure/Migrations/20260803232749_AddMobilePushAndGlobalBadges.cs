using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMobilePushAndGlobalBadges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClearedUtc",
                table: "MobileActivityNotifications",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId",
                table: "MobileActivityNotifications",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCleared",
                table: "MobileActivityNotifications",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsRead",
                table: "MobileActivityNotifications",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReadUtc",
                table: "MobileActivityNotifications",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "MobileActivityNotifications",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceMessageId",
                table: "MobileActivityNotifications",
                type: "uniqueidentifier",
                nullable: true);

            // Entries that predate the ledger did not have a read lifecycle.
            // Treat them as historical activity instead of unexpectedly adding
            // every legacy decision to a member's first centralized badge.
            migrationBuilder.Sql("""
                UPDATE [MobileActivityNotifications]
                SET [IsRead] = CAST(1 AS bit),
                    [ReadUtc] = [OccurredUtc]
                WHERE [SourceMessageId] IS NULL;
                """);

            migrationBuilder.CreateTable(
                name: "MobilePushDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MobilePushDeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AbandonedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MobilePushDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MobilePushDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    DeviceToken = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    InvalidatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MobilePushDevices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserGlobalBadges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ParticipantType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    UnreadCount = table.Column<int>(type: "int", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserGlobalBadges", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MobileActivityNotifications_RecipientUserId_RecipientParticipantType_ConversationId_IsRead_IsCleared",
                table: "MobileActivityNotifications",
                columns: new[] { "RecipientUserId", "RecipientParticipantType", "ConversationId", "IsRead", "IsCleared" });

            migrationBuilder.CreateIndex(
                name: "IX_MobileActivityNotifications_RecipientUserId_RecipientParticipantType_IsRead_IsCleared_OccurredUtc",
                table: "MobileActivityNotifications",
                columns: new[] { "RecipientUserId", "RecipientParticipantType", "IsRead", "IsCleared", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MobileActivityNotifications_SourceMessageId_RecipientUserId_RecipientParticipantType",
                table: "MobileActivityNotifications",
                columns: new[] { "SourceMessageId", "RecipientUserId", "RecipientParticipantType" },
                unique: true,
                filter: "[SourceMessageId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MobilePushDeliveries_NotificationId_MobilePushDeviceId",
                table: "MobilePushDeliveries",
                columns: new[] { "NotificationId", "MobilePushDeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MobilePushDeliveries_SentUtc_AbandonedUtc_NextAttemptUtc",
                table: "MobilePushDeliveries",
                columns: new[] { "SentUtc", "AbandonedUtc", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MobilePushDevices_TokenHash",
                table: "MobilePushDevices",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MobilePushDevices_UserId_ParticipantType_IsActive",
                table: "MobilePushDevices",
                columns: new[] { "UserId", "ParticipantType", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_UserGlobalBadges_UserId_ParticipantType",
                table: "UserGlobalBadges",
                columns: new[] { "UserId", "ParticipantType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MobilePushDeliveries");

            migrationBuilder.DropTable(
                name: "MobilePushDevices");

            migrationBuilder.DropTable(
                name: "UserGlobalBadges");

            migrationBuilder.DropIndex(
                name: "IX_MobileActivityNotifications_RecipientUserId_RecipientParticipantType_ConversationId_IsRead_IsCleared",
                table: "MobileActivityNotifications");

            migrationBuilder.DropIndex(
                name: "IX_MobileActivityNotifications_RecipientUserId_RecipientParticipantType_IsRead_IsCleared_OccurredUtc",
                table: "MobileActivityNotifications");

            migrationBuilder.DropIndex(
                name: "IX_MobileActivityNotifications_SourceMessageId_RecipientUserId_RecipientParticipantType",
                table: "MobileActivityNotifications");

            migrationBuilder.DropColumn(
                name: "ClearedUtc",
                table: "MobileActivityNotifications");

            migrationBuilder.DropColumn(
                name: "ConversationId",
                table: "MobileActivityNotifications");

            migrationBuilder.DropColumn(
                name: "IsCleared",
                table: "MobileActivityNotifications");

            migrationBuilder.DropColumn(
                name: "IsRead",
                table: "MobileActivityNotifications");

            migrationBuilder.DropColumn(
                name: "ReadUtc",
                table: "MobileActivityNotifications");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "MobileActivityNotifications");

            migrationBuilder.DropColumn(
                name: "SourceMessageId",
                table: "MobileActivityNotifications");
        }
    }
}
