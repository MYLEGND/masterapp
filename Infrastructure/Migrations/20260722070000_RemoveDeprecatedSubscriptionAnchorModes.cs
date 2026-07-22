using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

/// <summary>
/// Normalizes the two retired subscription-anchor modes before their enum values
/// are removed from the shared billing contract.
/// </summary>
[DbContext(typeof(MasterAppDbContext))]
[Migration("20260722070000_RemoveDeprecatedSubscriptionAnchorModes")]
public partial class RemoveDeprecatedSubscriptionAnchorModes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE [ClientSubscriptionOffers]
            SET [BillingAnchorSelectionMode] = CASE
                WHEN [BillingAnchorSelectionMode] = 'ClientSelectedIfAllowed'
                     AND [SelectedBillingAnchorDay] = 15 THEN 'FifteenthOfMonth'
                ELSE 'FirstOfMonth'
            END
            WHERE [BillingAnchorSelectionMode] IN ('ClientSelectedIfAllowed', 'ProviderDefault');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The deprecated modes intentionally have no restore path.
    }
}
