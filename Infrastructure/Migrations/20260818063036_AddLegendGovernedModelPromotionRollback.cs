using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendGovernedModelPromotionRollback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegendConnectModelPromotionPairs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModelTrainingRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PairKey = table.Column<string>(type: "nvarchar(72)", maxLength: 72, nullable: false),
                    PreviousActiveModelVersion = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                    PromotedModelVersion = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    PromotedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RolledBackUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendConnectModelPromotionPairs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendConnectModelPromotionPairs_LegendConnectModelTrainingRuns_ModelTrainingRunId",
                        column: x => x.ModelTrainingRunId,
                        principalTable: "LegendConnectModelTrainingRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendConnectModelPromotionPairs_ModelTrainingRunId_PairKey",
                table: "LegendConnectModelPromotionPairs",
                columns: new[] { "ModelTrainingRunId", "PairKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendConnectModelPromotionPairs_PairKey_PromotedUtc",
                table: "LegendConnectModelPromotionPairs",
                columns: new[] { "PairKey", "PromotedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendConnectModelPromotionPairs");
        }
    }
}
