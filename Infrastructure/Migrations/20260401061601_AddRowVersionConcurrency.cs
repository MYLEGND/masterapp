using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRowVersionConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // RowVersion DDL differs between SQLite (BLOB + trigger) and SQL Server (rowversion).
            // SQL Server: apply this as a separate targeted migration via dotnet ef database update.
            // SQLite dev: auto-applied on startup.
            if (migrationBuilder.ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
                return;

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "WorkstationLeadProfiles",
                type: "BLOB",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "ProductionRecords",
                type: "BLOB",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "ClientProfiles",
                type: "BLOB",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
                return;

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "WorkstationLeadProfiles");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "ProductionRecords");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "ClientProfiles");
        }
    }
}
