using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260315000000_AddUniqueIndex_ClientProfile_Email")]
    public partial class AddUniqueIndex_ClientProfile_Email : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fail fast if duplicates exist so we don’t create a broken unique index.
            if (migrationBuilder.ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql(@"
IF EXISTS (
    SELECT 1
    FROM ClientProfiles
    WHERE Email IS NOT NULL
    GROUP BY Email
    HAVING COUNT(*) > 1
)
    THROW 50000, 'Duplicate client emails exist; resolve before adding unique index.', 1;");
            }
            // SQLite will naturally fail on CreateIndex if duplicates exist.

            if (migrationBuilder.ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_ClientProfiles_Email\";");
            }

            migrationBuilder.CreateIndex(
                name: "IX_ClientProfiles_Email",
                table: "ClientProfiles",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientProfiles_Email",
                table: "ClientProfiles");
        }
    }
}
