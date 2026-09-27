using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

public partial class SyncCanonicalMarketingModel20260927 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Snapshot-only synchronization. Schema operations are owned by the preceding
        // additive canonical marketing migrations already present in this release.
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Snapshot-only synchronization; no schema rollback is performed here.
    }
}
