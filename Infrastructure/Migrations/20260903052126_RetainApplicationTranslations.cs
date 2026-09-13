using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RetainApplicationTranslations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LegendTranslationAlignments_PairKey_SourceTextUnitId_TargetTextUnitId",
                table: "LegendTranslationAlignments");

            migrationBuilder.AddColumn<string>(
                name: "PlaceholderContractHash",
                table: "LegendTranslationAlignments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderVersion",
                table: "LegendTranslationAlignments",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetainedTranslationIdentity",
                table: "LegendTranslationAlignments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReuseScope",
                table: "LegendTranslationAlignments",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReuseScopeIdentityHash",
                table: "LegendTranslationAlignments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceContentRevision",
                table: "LegendTranslationAlignments",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StableSourceContentId",
                table: "LegendTranslationAlignments",
                type: "nvarchar(180)",
                maxLength: 180,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TranslationContext",
                table: "LegendTranslationAlignments",
                type: "nvarchar(180)",
                maxLength: 180,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationAlignments_PairKey_SourceTextUnitId_TargetTextUnitId",
                table: "LegendTranslationAlignments",
                columns: new[] { "PairKey", "SourceTextUnitId", "TargetTextUnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationAlignments_RetainedTranslationIdentity",
                table: "LegendTranslationAlignments",
                column: "RetainedTranslationIdentity",
                unique: true,
                filter: "[RetainedTranslationIdentity] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LegendTranslationAlignments_PairKey_SourceTextUnitId_TargetTextUnitId",
                table: "LegendTranslationAlignments");

            migrationBuilder.DropIndex(
                name: "IX_LegendTranslationAlignments_RetainedTranslationIdentity",
                table: "LegendTranslationAlignments");

            migrationBuilder.DropColumn(
                name: "PlaceholderContractHash",
                table: "LegendTranslationAlignments");

            migrationBuilder.DropColumn(
                name: "ProviderVersion",
                table: "LegendTranslationAlignments");

            migrationBuilder.DropColumn(
                name: "RetainedTranslationIdentity",
                table: "LegendTranslationAlignments");

            migrationBuilder.DropColumn(
                name: "ReuseScope",
                table: "LegendTranslationAlignments");

            migrationBuilder.DropColumn(
                name: "ReuseScopeIdentityHash",
                table: "LegendTranslationAlignments");

            migrationBuilder.DropColumn(
                name: "SourceContentRevision",
                table: "LegendTranslationAlignments");

            migrationBuilder.DropColumn(
                name: "StableSourceContentId",
                table: "LegendTranslationAlignments");

            migrationBuilder.DropColumn(
                name: "TranslationContext",
                table: "LegendTranslationAlignments");

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationAlignments_PairKey_SourceTextUnitId_TargetTextUnitId",
                table: "LegendTranslationAlignments",
                columns: new[] { "PairKey", "SourceTextUnitId", "TargetTextUnitId" },
                unique: true);
        }
    }
}
