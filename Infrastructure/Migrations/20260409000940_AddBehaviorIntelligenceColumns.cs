using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBehaviorIntelligenceColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameIndex(
                name: "IX_AnalyticsEvents_SessionId",
                table: "AnalyticsEvents",
                newName: "IX_AnalyticsEvents_SessionId_Behavior");

            migrationBuilder.AlterColumn<string>(
                name: "UpdatedBy",
                table: "ClientFinancialPlans",
                type: "TEXT",
                maxLength: 320,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 320,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Browser",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeviceType",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DwellMilliseconds",
                table: "AnalyticsEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ElementId",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EngagedMilliseconds",
                table: "AnalyticsEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FieldName",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FormId",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsBounceCandidate",
                table: "AnalyticsEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsExitPage",
                table: "AnalyticsEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaAdId",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaAdName",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaAdSetId",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaAdSetName",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaCampaignId",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaCampaignName",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OperatingSystem",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Placement",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferrerHost",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScreenHeight",
                table: "AnalyticsEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScreenWidth",
                table: "AnalyticsEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScrollPercent",
                table: "AnalyticsEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UtmContent",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UtmTerm",
                table: "AnalyticsEvents",
                type: "TEXT",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ViewportHeight",
                table: "AnalyticsEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ViewportWidth",
                table: "AnalyticsEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_DeviceType",
                table: "AnalyticsEvents",
                column: "DeviceType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AnalyticsEvents_DeviceType",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "Browser",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "DeviceType",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "DwellMilliseconds",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "ElementId",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "EngagedMilliseconds",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "FieldName",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "FormId",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "IsBounceCandidate",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "IsExitPage",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "MetaAdId",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "MetaAdName",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "MetaAdSetId",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "MetaAdSetName",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "MetaCampaignId",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "MetaCampaignName",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "OperatingSystem",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "Placement",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "ReferrerHost",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "ScreenHeight",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "ScreenWidth",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "ScrollPercent",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "UtmContent",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "UtmTerm",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "ViewportHeight",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "ViewportWidth",
                table: "AnalyticsEvents");

            migrationBuilder.RenameIndex(
                name: "IX_AnalyticsEvents_SessionId_Behavior",
                table: "AnalyticsEvents",
                newName: "IX_AnalyticsEvents_SessionId");

            migrationBuilder.AlterColumn<string>(
                name: "UpdatedBy",
                table: "ClientFinancialPlans",
                type: "TEXT",
                maxLength: 320,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 320);
        }
    }
}
