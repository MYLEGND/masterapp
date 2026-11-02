using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScopedStoreContinuations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ClientProfileId",
                table: "ClientIdentityContinuations",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<string>(
                name: "BrowserCorrelationHash",
                table: "ClientIdentityContinuations",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DestinationOrigin",
                table: "ClientIdentityContinuations",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebsiteOwnerKey",
                table: "ClientIdentityContinuations",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebsiteSiteKey",
                table: "ClientIdentityContinuations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            const string bindingCheck = "(([Purpose] IN ('Activation', 'SignIn', 'WebsiteEditor', 'TrainingBilling') AND [ClientProfileId] IS NOT NULL AND [ClientProfileId] <> '00000000-0000-0000-0000-000000000000') OR ([Purpose] = 'StoreManagement' AND [CommerceBusinessId] IS NOT NULL AND [CommerceBusinessId] <> '00000000-0000-0000-0000-000000000000' AND [ActorUserId] IS NOT NULL AND LTRIM(RTRIM([ActorUserId])) <> '' AND [DestinationOrigin] IS NOT NULL AND LTRIM(RTRIM([DestinationOrigin])) <> '' AND [WebsiteSiteKey] IS NOT NULL AND LTRIM(RTRIM([WebsiteSiteKey])) <> '' AND [WebsiteOwnerKey] IS NOT NULL AND LTRIM(RTRIM([WebsiteOwnerKey])) <> '' AND [BrowserCorrelationHash] IS NOT NULL AND LTRIM(RTRIM([BrowserCorrelationHash])) <> ''))";
            if (ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer")
            {
                // Compile after the additive columns exist, including generated SQL scripts.
                migrationBuilder.Sql("EXEC(N'ALTER TABLE [ClientIdentityContinuations] ADD CONSTRAINT " +
                    "[CK_ClientIdentityContinuations_PurposeBinding] CHECK (" + bindingCheck.Replace("'", "''") + ")');");
            }
            else
            {
                migrationBuilder.AddCheckConstraint(
                    name: "CK_ClientIdentityContinuations_PurposeBinding",
                    table: "ClientIdentityContinuations",
                    sql: bindingCheck);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never invent a client identity or discard retained store handoffs on rollback.
            if (ActiveProvider != "Microsoft.EntityFrameworkCore.SqlServer")
                throw new NotSupportedException("Store continuation rollback requires a reviewed forward repair on this provider.");
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [ClientIdentityContinuations] WHERE [ClientProfileId] IS NULL OR [Purpose] = 'StoreManagement') THROW 51000, 'Store continuations require reviewed retention before schema rollback.', 1;");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ClientIdentityContinuations_PurposeBinding",
                table: "ClientIdentityContinuations");

            migrationBuilder.DropColumn(
                name: "BrowserCorrelationHash",
                table: "ClientIdentityContinuations");

            migrationBuilder.DropColumn(
                name: "DestinationOrigin",
                table: "ClientIdentityContinuations");

            migrationBuilder.DropColumn(
                name: "WebsiteOwnerKey",
                table: "ClientIdentityContinuations");

            migrationBuilder.DropColumn(
                name: "WebsiteSiteKey",
                table: "ClientIdentityContinuations");

            migrationBuilder.AlterColumn<Guid>(
                name: "ClientProfileId",
                table: "ClientIdentityContinuations",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
