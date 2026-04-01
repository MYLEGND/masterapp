using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260315190000_BackfillLegacyWorkstationLeadOriginalType")]
public partial class BackfillLegacyWorkstationLeadOriginalType : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            migrationBuilder.Sql(
                """
                IF OBJECT_ID(N'dbo.WorkstationLeadProfiles', N'U') IS NOT NULL
                BEGIN
                    UPDATE dbo.WorkstationLeadProfiles
                    SET OriginalLeadType = N'MortgageProtection'
                    WHERE OriginalLeadType IS NULL
                       OR LTRIM(RTRIM(OriginalLeadType)) = N''
                       OR LTRIM(RTRIM(OriginalLeadType)) NOT IN (
                            N'MortgageProtection',
                            N'LifeInsurance',
                            N'FinalExpense',
                            N'Medicare',
                            N'DisabilityInsurance'
                       );
                END
                """);
        }
        // SQLite: no-op. Column-order drift across legacy migrations can make
        // this backfill run before OriginalLeadType exists.
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // One-time legacy backfill only; original lead types are intentionally not reverted.
    }
}
