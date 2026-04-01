using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260315180000_AddWorkstationLeadAttemptWindows")]
public partial class AddWorkstationLeadAttemptWindows : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "CallsMonth",
            table: "WorkstationLeadProfiles",
            type: "int",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTime>(
            name: "CallsMonthStartUtc",
            table: "WorkstationLeadProfiles",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "CallsToday",
            table: "WorkstationLeadProfiles",
            type: "int",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTime>(
            name: "CallsTodayDateUtc",
            table: "WorkstationLeadProfiles",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "CallsWeek",
            table: "WorkstationLeadProfiles",
            type: "int",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTime>(
            name: "CallsWeekStartUtc",
            table: "WorkstationLeadProfiles",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "CallsYear",
            table: "WorkstationLeadProfiles",
            type: "int",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTime>(
            name: "CallsYearStartUtc",
            table: "WorkstationLeadProfiles",
            type: "datetime2",
            nullable: true);

        if (ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            migrationBuilder.Sql(
                """
                DECLARE @today date = CAST(SYSUTCDATETIME() AS date);
                DECLARE @weekStart date = DATEADD(day, -(DATEDIFF(day, '18991231', @today) % 7), @today);
                DECLARE @monthStart date = DATEFROMPARTS(YEAR(@today), MONTH(@today), 1);
                DECLARE @yearStart date = DATEFROMPARTS(YEAR(@today), 1, 1);

                UPDATE dbo.WorkstationLeadProfiles
                SET
                    CallsToday = CASE WHEN CAST(UpdatedUtc AS date) = @today THEN CallCount ELSE 0 END,
                    CallsTodayDateUtc = CAST(@today AS datetime2),
                    CallsWeek = CASE WHEN CAST(UpdatedUtc AS date) >= @weekStart THEN CallCount ELSE 0 END,
                    CallsWeekStartUtc = CAST(@weekStart AS datetime2),
                    CallsMonth = CASE WHEN CAST(UpdatedUtc AS date) >= @monthStart THEN CallCount ELSE 0 END,
                    CallsMonthStartUtc = CAST(@monthStart AS datetime2),
                    CallsYear = CASE WHEN CAST(UpdatedUtc AS date) >= @yearStart THEN CallCount ELSE 0 END,
                    CallsYearStartUtc = CAST(@yearStart AS datetime2);
                """);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CallsMonth",
            table: "WorkstationLeadProfiles");

        migrationBuilder.DropColumn(
            name: "CallsMonthStartUtc",
            table: "WorkstationLeadProfiles");

        migrationBuilder.DropColumn(
            name: "CallsToday",
            table: "WorkstationLeadProfiles");

        migrationBuilder.DropColumn(
            name: "CallsTodayDateUtc",
            table: "WorkstationLeadProfiles");

        migrationBuilder.DropColumn(
            name: "CallsWeek",
            table: "WorkstationLeadProfiles");

        migrationBuilder.DropColumn(
            name: "CallsWeekStartUtc",
            table: "WorkstationLeadProfiles");

        migrationBuilder.DropColumn(
            name: "CallsYear",
            table: "WorkstationLeadProfiles");

        migrationBuilder.DropColumn(
            name: "CallsYearStartUtc",
            table: "WorkstationLeadProfiles");
    }
}
