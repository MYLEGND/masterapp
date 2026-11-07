using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScopedStoreProvisioningEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProvisionedStoreBusinessId",
                table: "WebsiteContentState",
                type: "uniqueidentifier",
                nullable: true);

            // Positive legacy evidence only; association and retired/malformed payloads
            // do not establish that a store was ever added. No event time is invented.
            string Enabled(string column)
            {
                if (ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer")
                {
                    var json = "CASE WHEN ISJSON(" + column + ") = 1 THEN " + column + " ELSE N'{}' END";
                    return "(SELECT COUNT(*) FROM OPENJSON(" + json + ") WHERE LOWER([key]) = N'store') = 1 " +
                        "AND EXISTS (SELECT 1 FROM OPENJSON(" + json + ") WHERE [key] COLLATE Latin1_General_100_BIN2 = N'store' AND DATALENGTH([key]) = DATALENGTH(N'store') AND [type] = 5) " +
                        "AND (SELECT COUNT(*) FROM OPENJSON(" + json + ", '$.store') WHERE LOWER([key]) = N'enabled') = 1 " +
                        "AND EXISTS (SELECT 1 FROM OPENJSON(" + json + ", '$.store') WHERE [key] COLLATE Latin1_General_100_BIN2 = N'enabled' AND DATALENGTH([key]) = DATALENGTH(N'enabled') AND [type] = 3 AND [value] = N'true')";
                }
                if (ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
                {
                    var json = "CASE WHEN json_valid(" + column + ") THEN " + column + " ELSE '{}' END";
                    return "json_type(" + json + ") = 'object' AND " +
                        "(SELECT COUNT(*) FROM json_each(" + json + ") WHERE lower(key) = 'store') = 1 " +
                        "AND json_type(" + json + ", '$.store') = 'object' " +
                        "AND (SELECT COUNT(*) FROM json_each(" + json + ", '$.store') WHERE lower(key) = 'enabled') = 1 " +
                        "AND json_type(" + json + ", '$.store.enabled') = 'true'";
                }
                throw new NotSupportedException("Store provisioning evidence requires a reviewed provider migration.");
            }
            var backfill = "UPDATE [WebsiteContentState] SET [ProvisionedStoreBusinessId] = [CommerceBusinessId] " +
                "WHERE [CommerceBusinessId] IS NOT NULL AND (" + Enabled("[DraftJson]") +
                " OR EXISTS (SELECT 1 FROM [WebsiteContentVersion] v WHERE v.[StateId] = [WebsiteContentState].[Id] " +
                "AND v.[SnapshotRetiredUtc] IS NULL AND " + Enabled("v.[DocumentJson]") + "))";
            migrationBuilder.Sql(ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer"
                ? "EXEC(N'" + backfill.Replace("'", "''") + "');" : backfill);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Store provisioning evidence is retained history; use a reviewed forward repair.");
        }
    }
}
