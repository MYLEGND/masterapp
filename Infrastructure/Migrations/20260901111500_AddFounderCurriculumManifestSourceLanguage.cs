using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

public partial class AddFounderCurriculumManifestSourceLanguage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SourceLanguageCode",
            table: "LegendCurriculumManifestWorkItems",
            type: "nvarchar(32)",
            maxLength: 32,
            nullable: true);

        // Existing durable receipts predate explicit source-language intake
        // and are canonically English. New receipts have no database default:
        // the application authority must provide the normalized language.
        migrationBuilder.Sql(ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase)
            ? "UPDATE [LegendCurriculumManifestWorkItems] SET [SourceLanguageCode] = N'en' WHERE [SourceLanguageCode] IS NULL;"
            : "UPDATE \"LegendCurriculumManifestWorkItems\" SET \"SourceLanguageCode\" = 'en' WHERE \"SourceLanguageCode\" IS NULL;");

        migrationBuilder.AlterColumn<string>(
            name: "SourceLanguageCode",
            table: "LegendCurriculumManifestWorkItems",
            type: "nvarchar(32)",
            maxLength: 32,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "nvarchar(32)",
            oldMaxLength: 32,
            oldNullable: true);

        migrationBuilder.DropIndex(
            name: "IX_LegendCurriculumManifestWorkItems_FounderStatus",
            table: "LegendCurriculumManifestWorkItems");

        migrationBuilder.CreateIndex(
            name: "IX_LegendCurriculumManifestWorkItems_FounderStatus",
            table: "LegendCurriculumManifestWorkItems",
            columns: new[] { "SourceLanguageCode", "CreatedUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_LegendCurriculumManifestWorkItems_FounderStatus",
            table: "LegendCurriculumManifestWorkItems");

        migrationBuilder.DropColumn(
            name: "SourceLanguageCode",
            table: "LegendCurriculumManifestWorkItems");

        migrationBuilder.CreateIndex(
            name: "IX_LegendCurriculumManifestWorkItems_FounderStatus",
            table: "LegendCurriculumManifestWorkItems",
            column: "CreatedUtc");
    }
}
