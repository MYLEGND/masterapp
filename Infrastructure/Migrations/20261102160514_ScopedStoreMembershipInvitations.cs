using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScopedStoreMembershipInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ClientIdentityContinuations_PurposeBinding",
                table: "ClientIdentityContinuations");

            migrationBuilder.AddColumn<Guid>(
                name: "CommerceBusinessMemberId",
                table: "ClientIdentityContinuations",
                type: "uniqueidentifier",
                nullable: true);

            const string bindingCheck = "(([Purpose] IN ('Activation', 'SignIn', 'WebsiteEditor', 'TrainingBilling') AND [ClientProfileId] IS NOT NULL AND [ClientProfileId] <> '00000000-0000-0000-0000-000000000000') OR ([Purpose] = 'BusinessMembership' AND [ClientProfileId] IS NOT NULL AND [CommerceBusinessId] IS NOT NULL AND [CommerceBusinessMemberId] IS NOT NULL AND [ActorUserId] IS NOT NULL) OR ([Purpose] = 'StoreManagement' AND [CommerceBusinessId] IS NOT NULL AND [CommerceBusinessId] <> '00000000-0000-0000-0000-000000000000' AND [ActorUserId] IS NOT NULL AND LTRIM(RTRIM([ActorUserId])) <> '' AND [DestinationOrigin] IS NOT NULL AND LTRIM(RTRIM([DestinationOrigin])) <> '' AND [WebsiteSiteKey] IS NOT NULL AND LTRIM(RTRIM([WebsiteSiteKey])) <> '' AND [WebsiteOwnerKey] IS NOT NULL AND LTRIM(RTRIM([WebsiteOwnerKey])) <> '' AND [BrowserCorrelationHash] IS NOT NULL AND LTRIM(RTRIM([BrowserCorrelationHash])) <> ''))";
            if (ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer")
            {
                // Defer compilation until the additive column exists, including scripted batches.
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
            throw new NotSupportedException("Membership invitation identity and receipts require a reviewed forward recovery.");
        }
    }
}
