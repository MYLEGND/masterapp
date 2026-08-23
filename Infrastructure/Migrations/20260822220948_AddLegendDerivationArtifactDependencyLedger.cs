using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendDerivationArtifactDependencyLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DependencyInventoryWorkItemCount",
                table: "LegendLanguageDerivationConvergences",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresDependencyInventory",
                table: "LegendLanguageDerivationConvergences",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "LegendLanguageDerivationArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactKind = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ResultArtifactIdentity = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    SourceDependencyIdentity = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    SourceDependencySemanticVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DerivationContractIdentity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    SupersededUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguageDerivationArtifacts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageDerivationConvergences_RequiresDependencyInventory_State_UpdatedUtc",
                table: "LegendLanguageDerivationConvergences",
                columns: new[] { "RequiresDependencyInventory", "State", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageDerivationArtifacts_ArtifactKind_ResultArtifactIdentity_SourceDependencyIdentity_DerivationContractIdentity",
                table: "LegendLanguageDerivationArtifacts",
                columns: new[] { "ArtifactKind", "ResultArtifactIdentity", "SourceDependencyIdentity", "DerivationContractIdentity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageDerivationArtifacts_ArtifactKind_State_UpdatedUtc",
                table: "LegendLanguageDerivationArtifacts",
                columns: new[] { "ArtifactKind", "State", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageDerivationArtifacts_DerivationContractIdentity_State",
                table: "LegendLanguageDerivationArtifacts",
                columns: new[] { "DerivationContractIdentity", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendLanguageDerivationArtifacts");

            migrationBuilder.DropIndex(
                name: "IX_LegendLanguageDerivationConvergences_RequiresDependencyInventory_State_UpdatedUtc",
                table: "LegendLanguageDerivationConvergences");

            migrationBuilder.DropColumn(
                name: "DependencyInventoryWorkItemCount",
                table: "LegendLanguageDerivationConvergences");

            migrationBuilder.DropColumn(
                name: "RequiresDependencyInventory",
                table: "LegendLanguageDerivationConvergences");
        }
    }
}
