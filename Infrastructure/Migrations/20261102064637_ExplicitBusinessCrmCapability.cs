using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ExplicitBusinessCrmCapability : Migration
    {
        private string PreviousGrant => "[CanManageOrders] = 1 OR " +
            (ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer"
                ? "(LOWER([RoleKey]) COLLATE Latin1_General_100_BIN2 IN (N'owner',N'platform-owner',N'account') AND DATALENGTH([RoleKey]) = DATALENGTH(RTRIM([RoleKey])))"
                : "LOWER([RoleKey]) IN ('owner','platform-owner','account')");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CanManageCrm",
                table: "CommerceBusinessMembers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            var backfill = "UPDATE [CommerceBusinessMembers] SET [CanManageCrm] = CASE WHEN " + PreviousGrant + " THEN 1 ELSE 0 END";
            // Preserve prior grants for active and inactive members; status remains a separate gate.
            migrationBuilder.Sql(ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer"
                ? "EXEC(N'" + backfill.Replace("'", "''") + "');"
                : backfill);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider != "Microsoft.EntityFrameworkCore.SqlServer")
                throw new NotSupportedException("CRM capability rollback requires reviewed forward repair on this provider.");
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [CommerceBusinessMembers] WHERE [CanManageCrm] <> CASE WHEN " + PreviousGrant + " THEN 1 ELSE 0 END) THROW 51000, 'CRM permission rollback would change retained access grants.', 1;");
            migrationBuilder.DropColumn(
                name: "CanManageCrm",
                table: "CommerceBusinessMembers");
        }
    }
}
