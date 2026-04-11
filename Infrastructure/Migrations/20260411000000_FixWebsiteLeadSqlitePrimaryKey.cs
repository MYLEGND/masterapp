using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <summary>
    /// The WebsiteLeads table was originally created with "bigint NOT NULL PRIMARY KEY" which
    /// does NOT auto-increment in SQLite (only INTEGER PRIMARY KEY does). This migration
    /// recreates the table with a proper INTEGER PRIMARY KEY AUTOINCREMENT so that EF Core's
    /// SQLite provider can omit the Id column on INSERT and have SQLite generate it.
    ///
    /// On SQL Server the bigint IDENTITY column works correctly, so no change is needed there.
    /// </summary>
    public partial class FixWebsiteLeadSqlitePrimaryKey : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
                return;

            // SQLite does not support ALTER COLUMN, so we must recreate the table.
            // The table is expected to be empty in dev; data is preserved via INSERT INTO ... SELECT.
            migrationBuilder.Sql(@"
CREATE TABLE ""WebsiteLeads_v2"" (
    ""Id""                     INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    ""LeadId""                 TEXT    NOT NULL,
    ""FirstName""              TEXT    NOT NULL,
    ""LastName""               TEXT    NULL,
    ""Email""                  TEXT    NOT NULL,
    ""Phone""                  TEXT    NULL,
    ""PreferredContactMethod"" TEXT    NULL,
    ""InterestType""           TEXT    NULL,
    ""Notes""                  TEXT    NULL,
    ""SourcePageKey""          TEXT    NULL,
    ""SourceCtaKey""           TEXT    NULL,
    ""UtmSource""              TEXT    NULL,
    ""UtmMedium""              TEXT    NULL,
    ""UtmCampaign""            TEXT    NULL,
    ""SessionId""              TEXT    NULL,
    ""VisitorId""              TEXT    NULL,
    ""MarketingEmailConsent""  INTEGER NOT NULL,
    ""CallTextConsent""        INTEGER NOT NULL,
    ""TermsAccepted""          INTEGER NOT NULL,
    ""IsInternal""             INTEGER NOT NULL,
    ""Environment""            TEXT    NULL,
    ""Host""                   TEXT    NULL,
    ""CreatedUtc""             TEXT    NOT NULL,
    ""Status""                 TEXT    NOT NULL,
    ""MetadataJson""           TEXT    NULL,
    ""AgentTrackingProfileId"" TEXT    NULL,
    ""AgentSlug""              TEXT    NULL
);

INSERT INTO ""WebsiteLeads_v2""
SELECT
    ""Id"", ""LeadId"", ""FirstName"", ""LastName"", ""Email"", ""Phone"",
    ""PreferredContactMethod"", ""InterestType"", ""Notes"", ""SourcePageKey"",
    ""SourceCtaKey"", ""UtmSource"", ""UtmMedium"", ""UtmCampaign"", ""SessionId"",
    ""VisitorId"", ""MarketingEmailConsent"", ""CallTextConsent"", ""TermsAccepted"",
    ""IsInternal"", ""Environment"", ""Host"", ""CreatedUtc"", ""Status"",
    ""MetadataJson"", ""AgentTrackingProfileId"", ""AgentSlug""
FROM ""WebsiteLeads"";

DROP TABLE ""WebsiteLeads"";
ALTER TABLE ""WebsiteLeads_v2"" RENAME TO ""WebsiteLeads"";
");

            // Recreate all indices
            migrationBuilder.Sql(@"
CREATE INDEX ""IX_WebsiteLeads_AgentSlug""              ON ""WebsiteLeads"" (""AgentSlug"");
CREATE INDEX ""IX_WebsiteLeads_AgentTrackingProfileId"" ON ""WebsiteLeads"" (""AgentTrackingProfileId"");
CREATE INDEX ""IX_WebsiteLeads_CreatedUtc""             ON ""WebsiteLeads"" (""CreatedUtc"");
CREATE INDEX ""IX_WebsiteLeads_Email""                  ON ""WebsiteLeads"" (""Email"");
CREATE INDEX ""IX_WebsiteLeads_InterestType""           ON ""WebsiteLeads"" (""InterestType"");
CREATE INDEX ""IX_WebsiteLeads_SourceCtaKey""           ON ""WebsiteLeads"" (""SourceCtaKey"");
CREATE INDEX ""IX_WebsiteLeads_SourcePageKey""          ON ""WebsiteLeads"" (""SourcePageKey"");
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down is intentionally left as no-op: reverting to bigint PRIMARY KEY would
            // re-introduce the SQLite auto-increment bug.
        }
    }
}
