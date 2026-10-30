using System;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Infrastructure.Migrations;
public partial class CrmRelationshipHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "AssociationVersion", table: "WebsiteLeadIntakeLinks", type: "uniqueidentifier", nullable: false, defaultValue: Guid.Empty);
        migrationBuilder.AlterColumn<string>(name: "WorkstationLeadId", table: "WebsiteLeadIntakeLinks", type: "nvarchar(64)", maxLength: 64, nullable: true,
            oldClrType: typeof(string), oldType: "nvarchar(64)", oldMaxLength: 64);
        migrationBuilder.AddColumn<Guid>(name: "HistoryClientProfileId", table: "WebsiteLeadIntakeLinks", type: "uniqueidentifier", nullable: true);
        migrationBuilder.CreateIndex(name: "IX_WebsiteLeadIntakeLinks_HistoryClientProfileId", table: "WebsiteLeadIntakeLinks", column: "HistoryClientProfileId");
        migrationBuilder.AddForeignKey(name: "FK_WebsiteLeadIntakeLinks_ClientProfiles_HistoryClientProfileId", table: "WebsiteLeadIntakeLinks", column: "HistoryClientProfileId",
            principalTable: "ClientProfiles", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddCheckConstraint(name: "CK_WebsiteLeadIntakeLinks_HistoryAssociation", table: "WebsiteLeadIntakeLinks", sql: "(([WorkstationLeadId] IS NOT NULL AND [HistoryClientProfileId] IS NULL) OR ([WorkstationLeadId] IS NULL AND [HistoryClientProfileId] IS NOT NULL AND [CommerceBusinessId] IS NULL))");
        migrationBuilder.CreateTable("CrmHistoryEntries", columns: table => new
        {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RelationshipKey = table.Column<string>(type: "nvarchar(100)", nullable: false, maxLength: 100),
                RecordedBy = table.Column<string>(type: "nvarchar(450)", nullable: false, maxLength: 450),
                RecordedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                FiledUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                OccurredDate = table.Column<string>(type: "nvarchar(10)", nullable: true, maxLength: 10),
                TimeZoneId = table.Column<string>(type: "nvarchar(100)", nullable: false, maxLength: 100),
                TimePrecision = table.Column<string>(type: "nvarchar(20)", nullable: false, maxLength: 20),
                Type = table.Column<string>(type: "nvarchar(60)", nullable: false, maxLength: 60),
                Source = table.Column<string>(type: "nvarchar(80)", nullable: false, maxLength: 80),
                SourceId = table.Column<string>(type: "nvarchar(200)", nullable: false, maxLength: 200),
                ContentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                RelatedEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                FormName = table.Column<string>(type: "nvarchar(160)", nullable: true, maxLength: 160),
                FormVersion = table.Column<string>(type: "nvarchar(100)", nullable: true, maxLength: 100),
                Product = table.Column<string>(type: "nvarchar(100)", nullable: true, maxLength: 100),
                CaseReference = table.Column<string>(type: "nvarchar(200)", nullable: true, maxLength: 200),
                AssignedOwner = table.Column<string>(type: "nvarchar(450)", nullable: true, maxLength: 450),
                DueUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                Status = table.Column<string>(type: "nvarchar(40)", nullable: false, maxLength: 40),
                EvidenceStatus = table.Column<string>(type: "nvarchar(80)", nullable: false, maxLength: 80)
        }, constraints: table => table.PrimaryKey("PK_CrmHistoryEntries", x => x.Id));
        migrationBuilder.CreateIndex("IX_CrmHistoryEntries_RelationshipKey_Source_SourceId", "CrmHistoryEntries", new[] { "RelationshipKey", "Source", "SourceId" }, unique: true);
        migrationBuilder.CreateIndex("IX_CrmHistoryEntries_RelationshipKey_OccurredUtc_Id", "CrmHistoryEntries", new[] { "RelationshipKey", "OccurredUtc", "Id" }, unique: false);
        migrationBuilder.CreateIndex("IX_CrmHistoryEntries_RelationshipKey_RecordedUtc_Id", "CrmHistoryEntries", new[] { "RelationshipKey", "RecordedUtc", "Id" }, unique: false);
        migrationBuilder.CreateIndex("IX_CrmHistoryEntries_SubmissionId", "CrmHistoryEntries", new[] { "SubmissionId" }, unique: false);
    }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new InvalidOperationException("Filed CRM history must be preserved during application rollback. Use the reviewed forward-recovery procedure.");
}
