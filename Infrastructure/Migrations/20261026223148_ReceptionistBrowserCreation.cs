using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReceptionistBrowserCreation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReceptionistCalls_Provider_ProviderProjectKey_ProviderSessionId",
                table: "ReceptionistCalls");

            migrationBuilder.AlterColumn<string>(
                name: "ProviderSessionId",
                table: "ReceptionistCalls",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<Guid>(
                name: "BrowserCreationId",
                table: "ReceptionistCalls",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BrowserOfferSha256",
                table: "ReceptionistCalls",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCalls_BrowserCreationId",
                table: "ReceptionistCalls",
                column: "BrowserCreationId",
                unique: true,
                filter: "[BrowserCreationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCalls_Provider_ProviderProjectKey_ProviderSessionId",
                table: "ReceptionistCalls",
                columns: new[] { "Provider", "ProviderProjectKey", "ProviderSessionId" },
                unique: true,
                filter: "[ProviderSessionId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReceptionistCall_BrowserCreation",
                table: "ReceptionistCalls",
                sql: "([BrowserCreationId] IS NULL AND [BrowserOfferSha256] IS NULL AND [ProviderSessionId] IS NOT NULL) OR ([BrowserCreationId] IS NOT NULL AND [BrowserOfferSha256] IS NOT NULL AND LEN([BrowserOfferSha256]) = 64 AND [ProviderAcceptAttemptId] IS NOT NULL AND ([ProviderSessionId] IS NOT NULL OR ([State] = 'uncertain' AND [ProviderClosedUtc] IS NULL AND [SidebandLeaseToken] IS NULL)))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Browser attempt identity is reconciliation evidence. Rolling application
            // code back must not erase it or manufacture provider IDs for pending calls.
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [ReceptionistCalls] WHERE [BrowserCreationId] IS NOT NULL OR [ProviderSessionId] IS NULL) THROW 51000, 'Browser creation evidence must be preserved; use a reviewed forward recovery.', 1;");
            migrationBuilder.DropIndex(
                name: "IX_ReceptionistCalls_BrowserCreationId",
                table: "ReceptionistCalls");

            migrationBuilder.DropIndex(
                name: "IX_ReceptionistCalls_Provider_ProviderProjectKey_ProviderSessionId",
                table: "ReceptionistCalls");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ReceptionistCall_BrowserCreation",
                table: "ReceptionistCalls");

            migrationBuilder.DropColumn(
                name: "BrowserCreationId",
                table: "ReceptionistCalls");

            migrationBuilder.DropColumn(
                name: "BrowserOfferSha256",
                table: "ReceptionistCalls");

            migrationBuilder.AlterColumn<string>(
                name: "ProviderSessionId",
                table: "ReceptionistCalls",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200,
                oldNullable: true,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCalls_Provider_ProviderProjectKey_ProviderSessionId",
                table: "ReceptionistCalls",
                columns: new[] { "Provider", "ProviderProjectKey", "ProviderSessionId" },
                unique: true);
        }
    }
}
