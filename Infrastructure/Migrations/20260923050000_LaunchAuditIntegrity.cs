using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Migrations;

public partial class LaunchAuditIntegrity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>("NotificationAttemptUtc", "WebsiteLeads", type: "datetime2", nullable: true);
        migrationBuilder.AddColumn<DateTime>("NotificationSentUtc", "WebsiteLeads", type: "datetime2", nullable: true);
        migrationBuilder.AddColumn<string>("OwnershipHistoryJson", "CommerceBusinesses", type: "nvarchar(max)", nullable: true);
        migrationBuilder.AddColumn<decimal>("OwnershipPercentage", "CommerceBusinessMembers", type: "decimal(5,2)", nullable: true);
        if (ActiveProvider?.Contains("SqlServer") == true)
            migrationBuilder.Sql("IF EXISTS (SELECT LeadId FROM WebsiteLeads GROUP BY LeadId HAVING COUNT(*) > 1) THROW 51000, 'Duplicate website lead IDs require review before applying the uniqueness constraint.', 1;");
        migrationBuilder.CreateIndex("IX_WebsiteLeads_LeadId", "WebsiteLeads", "LeadId", unique: true);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("NotificationAttemptUtc", "WebsiteLeads");
        migrationBuilder.DropColumn("NotificationSentUtc", "WebsiteLeads");
        migrationBuilder.DropIndex("IX_WebsiteLeads_LeadId", "WebsiteLeads");
        migrationBuilder.DropColumn("OwnershipPercentage", "CommerceBusinessMembers");
        migrationBuilder.DropColumn("OwnershipHistoryJson", "CommerceBusinesses");
    }
}
