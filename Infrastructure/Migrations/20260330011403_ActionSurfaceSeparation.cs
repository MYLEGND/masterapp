using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ActionSurfaceSeparation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ActionItems was never created on SQLite (ExecutionMvp_Regen skipped it).
            // These columns are SQL Server-only; SQLite schema picks them up from the snapshot.
            if (ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
                return;

            migrationBuilder.AddColumn<string>(
                name: "ActionCategory",
                table: "ActionItems",
                maxLength: 60,
                nullable: false,
                defaultValue: "Other");

            migrationBuilder.AddColumn<string>(
                name: "ActionSurface",
                table: "ActionItems",
                maxLength: 40,
                nullable: false,
                defaultValue: "CrmOnly");

            migrationBuilder.AddColumn<bool>(
                name: "IsEscalated",
                table: "ActionItems",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PipelineStage",
                table: "ActionItems",
                maxLength: 120,
                nullable: true);

            migrationBuilder.Sql("UPDATE ActionItems SET ActionSurface = 'CrmOnly', ActionCategory = 'Other', IsEscalated = 0 WHERE ActionSurface IS NULL OR ActionSurface = '' OR ActionCategory IS NULL OR ActionCategory = ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
                return;

            migrationBuilder.DropColumn(
                name: "ActionCategory",
                table: "ActionItems");

            migrationBuilder.DropColumn(
                name: "ActionSurface",
                table: "ActionItems");

            migrationBuilder.DropColumn(
                name: "IsEscalated",
                table: "ActionItems");

            migrationBuilder.DropColumn(
                name: "PipelineStage",
                table: "ActionItems");
        }
    }
}
