using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendDependencyDrivenDerivationConvergence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegendLanguageDerivationContracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DerivationKind = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ContractVersion = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ContractIdentity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EarliestPhase = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    RequiresHistoricalWork = table.Column<bool>(type: "bit", nullable: false),
                    IntroducedEvaluatorVersion = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    SupersededUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguageDerivationContracts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegendLanguageDerivationConvergences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetEvaluatorVersion = table.Column<int>(type: "int", nullable: false),
                    BaselineEvaluatorVersion = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    EarliestAffectedPhase = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ChangedContractCount = table.Column<int>(type: "int", nullable: false),
                    ReusedContractCount = table.Column<int>(type: "int", nullable: false),
                    ExistingCanonicalArtifactCount = table.Column<long>(type: "bigint", nullable: false),
                    ReusedCanonicalArtifactCount = table.Column<long>(type: "bigint", nullable: false),
                    AffectedCanonicalArtifactCount = table.Column<long>(type: "bigint", nullable: false),
                    PlannedWorkItemCount = table.Column<long>(type: "bigint", nullable: false),
                    BlockingDependencyIdentity = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguageDerivationConvergences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegendLanguageDerivationContractDependencies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DependentContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DependencyDerivationKind = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    DependencyContractIdentity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendLanguageDerivationContractDependencies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendLanguageDerivationContractDependencies_LegendLanguageDerivationContracts_DependentContractId",
                        column: x => x.DependentContractId,
                        principalTable: "LegendLanguageDerivationContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageDerivationContractDependencies_DependencyContractIdentity",
                table: "LegendLanguageDerivationContractDependencies",
                column: "DependencyContractIdentity");

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageDerivationContractDependencies_DependentContractId_DependencyDerivationKind_DependencyContractIdentity",
                table: "LegendLanguageDerivationContractDependencies",
                columns: new[] { "DependentContractId", "DependencyDerivationKind", "DependencyContractIdentity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageDerivationContracts_ContractIdentity",
                table: "LegendLanguageDerivationContracts",
                column: "ContractIdentity",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageDerivationContracts_DerivationKind_SupersededUtc",
                table: "LegendLanguageDerivationContracts",
                columns: new[] { "DerivationKind", "SupersededUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageDerivationContracts_State_IntroducedEvaluatorVersion",
                table: "LegendLanguageDerivationContracts",
                columns: new[] { "State", "IntroducedEvaluatorVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageDerivationConvergences_State_UpdatedUtc",
                table: "LegendLanguageDerivationConvergences",
                columns: new[] { "State", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendLanguageDerivationConvergences_TargetEvaluatorVersion",
                table: "LegendLanguageDerivationConvergences",
                column: "TargetEvaluatorVersion",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendLanguageDerivationContractDependencies");

            migrationBuilder.DropTable(
                name: "LegendLanguageDerivationConvergences");

            migrationBuilder.DropTable(
                name: "LegendLanguageDerivationContracts");
        }
    }
}
