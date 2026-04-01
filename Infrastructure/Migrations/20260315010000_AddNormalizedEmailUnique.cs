using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260315010000_AddNormalizedEmailUnique")]
    public partial class AddNormalizedEmailUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preflight: fail if normalized duplicates exist
            if (migrationBuilder.ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql(@"
IF EXISTS (
    SELECT 1
    FROM ClientProfiles
    WHERE Email IS NOT NULL
    GROUP BY LOWER(LTRIM(RTRIM(Email)))
    HAVING COUNT(*) > 1
)
    THROW 50001, 'Duplicate client emails (normalized) exist; resolve before adding normalized unique index.', 1;
");
            }
            // SQLite will naturally fail on CreateIndex if duplicates exist.

            migrationBuilder.AddColumn<string>(
                name: "NormalizedEmail",
                table: "ClientProfiles",
                type: "nvarchar(320)",
                nullable: true);

            migrationBuilder.Sql(@"
UPDATE ClientProfiles
SET NormalizedEmail = LOWER(LTRIM(RTRIM(Email)))
WHERE Email IS NOT NULL;
");

            if (migrationBuilder.ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_ClientProfiles_NormalizedEmail\";");
            }

            migrationBuilder.CreateIndex(
                name: "IX_ClientProfiles_NormalizedEmail",
                table: "ClientProfiles",
                column: "NormalizedEmail",
                unique: true,
                filter: "[NormalizedEmail] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientProfiles_NormalizedEmail",
                table: "ClientProfiles");

            migrationBuilder.DropColumn(
                name: "NormalizedEmail",
                table: "ClientProfiles");
        }
    }
}
