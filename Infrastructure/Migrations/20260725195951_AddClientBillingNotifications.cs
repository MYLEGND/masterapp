using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientBillingNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var isSqlServer = ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase);

            migrationBuilder.CreateTable(
                name: "ClientBillingNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: isSqlServer ? "uniqueidentifier" : "TEXT", nullable: false),
                    ClientProfileId = table.Column<Guid>(type: isSqlServer ? "uniqueidentifier" : "TEXT", nullable: false),
                    ClientSubscriptionId = table.Column<Guid>(type: isSqlServer ? "uniqueidentifier" : "TEXT", nullable: false),
                    Kind = table.Column<string>(type: isSqlServer ? "nvarchar(48)" : "TEXT", maxLength: 48, nullable: false),
                    EventKey = table.Column<string>(type: isSqlServer ? "nvarchar(220)" : "TEXT", maxLength: 220, nullable: false),
                    Subject = table.Column<string>(type: isSqlServer ? "nvarchar(240)" : "TEXT", maxLength: 240, nullable: false),
                    PlainTextBody = table.Column<string>(type: isSqlServer ? "nvarchar(4000)" : "TEXT", maxLength: 4000, nullable: false),
                    NotBeforeUtc = table.Column<DateTime>(type: isSqlServer ? "datetime2" : "TEXT", nullable: false),
                    SentUtc = table.Column<DateTime>(type: isSqlServer ? "datetime2" : "TEXT", nullable: true),
                    AttemptCount = table.Column<int>(type: isSqlServer ? "int" : "INTEGER", nullable: false),
                    LastAttemptUtc = table.Column<DateTime>(type: isSqlServer ? "datetime2" : "TEXT", nullable: true),
                    NextAttemptUtc = table.Column<DateTime>(type: isSqlServer ? "datetime2" : "TEXT", nullable: true),
                    SafeFailureCode = table.Column<string>(type: isSqlServer ? "nvarchar(120)" : "TEXT", maxLength: 120, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: isSqlServer ? "datetime2" : "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: isSqlServer ? "datetime2" : "TEXT", nullable: false),
                    RowVersion = table.Column<byte[]>(
                        type: isSqlServer ? "rowversion" : "BLOB",
                        rowVersion: isSqlServer,
                        nullable: false,
                        defaultValueSql: isSqlServer ? null : "X''")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientBillingNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientBillingNotifications_ClientProfiles_ClientProfileId",
                        column: x => x.ClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientBillingNotifications_ClientSubscriptions_ClientSubscriptionId",
                        column: x => x.ClientSubscriptionId,
                        principalTable: "ClientSubscriptions",
                        principalColumn: "Id",
                        onDelete: isSqlServer ? ReferentialAction.NoAction : ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientBillingNotifications_ClientProfileId",
                table: "ClientBillingNotifications",
                column: "ClientProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientBillingNotifications_ClientSubscriptionId_Kind",
                table: "ClientBillingNotifications",
                columns: new[] { "ClientSubscriptionId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientBillingNotifications_EventKey",
                table: "ClientBillingNotifications",
                column: "EventKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientBillingNotifications_SentUtc_NotBeforeUtc_NextAttemptUtc",
                table: "ClientBillingNotifications",
                columns: new[] { "SentUtc", "NotBeforeUtc", "NextAttemptUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientBillingNotifications");
        }
    }
}
