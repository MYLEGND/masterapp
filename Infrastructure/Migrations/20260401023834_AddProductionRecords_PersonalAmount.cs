using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductionRecords_PersonalAmount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite: column was added directly via ALTER TABLE (SQLite does not support
            // adding NOT NULL columns without a DEFAULT via migrations easily).
            // SQL Server: add the column with default 0 to match the entity definition.
            if (ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
                return;

            migrationBuilder.AddColumn<decimal>(
                name: "PersonalAmount",
                table: "ProductionRecords",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
                return;

            migrationBuilder.DropColumn(
                name: "PersonalAmount",
                table: "ProductionRecords");
        }
    }
}
