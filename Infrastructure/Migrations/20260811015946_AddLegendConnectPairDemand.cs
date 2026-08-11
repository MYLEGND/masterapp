using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendConnectPairDemand : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegendTranslationPairDemands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PairKey = table.Column<string>(type: "nvarchar(72)", maxLength: 72, nullable: false),
                    TranslationRequestCount = table.Column<long>(type: "bigint", nullable: false),
                    ProviderCharacterCount = table.Column<long>(type: "bigint", nullable: false),
                    TranslationMemoryHitCount = table.Column<long>(type: "bigint", nullable: false),
                    LastRequestedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendTranslationPairDemands", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationPairDemands_LastRequestedUtc",
                table: "LegendTranslationPairDemands",
                column: "LastRequestedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationPairDemands_PairKey",
                table: "LegendTranslationPairDemands",
                column: "PairKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendTranslationPairDemands");
        }
    }
}
