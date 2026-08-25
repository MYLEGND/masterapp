using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFounderRemediationAuthorityAndIntelligenceEvaluation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FounderSoftwareRemediationAuthorityStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScopeKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsRevoked = table.Column<bool>(type: "bit", nullable: false),
                    RevokedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedByUserId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LastVerifiedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProtectedProductionBranchVerified = table.Column<bool>(type: "bit", nullable: false),
                    SecurityCiVerified = table.Column<bool>(type: "bit", nullable: false),
                    RepairPreparationVerified = table.Column<bool>(type: "bit", nullable: false),
                    LastVerificationCode = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    LastVerificationDetail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FounderSoftwareRemediationAuthorityStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegendIntelligenceEvaluationContracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractKey = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ContractIdentity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SupersededUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendIntelligenceEvaluationContracts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegendIntelligenceEvaluationSignals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DomainKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    MetricKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    EvidenceAuthority = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    EvidenceReference = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    State = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    MeasuredUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendIntelligenceEvaluationSignals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendIntelligenceEvaluationSignals_LegendIntelligenceEvaluationContracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "LegendIntelligenceEvaluationContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegendIntelligenceEvaluationSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreviousSnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EvidenceSetIdentity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendIntelligenceEvaluationSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendIntelligenceEvaluationSnapshots_LegendIntelligenceEvaluationContracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "LegendIntelligenceEvaluationContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegendIntelligenceEvaluationDomainSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DomainKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    EvidenceScore = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    LegendSelfAssessment = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    OpenAiExternalAssessment = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    EvidenceVolume = table.Column<long>(type: "bigint", nullable: false),
                    ProductionEligibleEvidenceCount = table.Column<long>(type: "bigint", nullable: false),
                    NativeSuccessRate = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    HeldOutResult = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    TransferResult = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    ContradictionRate = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    EvidenceReferencesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    KnownWeaknessesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OpenGapsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendIntelligenceEvaluationDomainSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendIntelligenceEvaluationDomainSnapshots_LegendIntelligenceEvaluationSnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "LegendIntelligenceEvaluationSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LegendIntelligenceEvaluationPerspectives",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PerspectiveKind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    State = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    AssessmentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EvidenceReferencesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AssessedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendIntelligenceEvaluationPerspectives", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendIntelligenceEvaluationPerspectives_LegendIntelligenceEvaluationSnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "LegendIntelligenceEvaluationSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FounderSoftwareRemediationAuthorityStates_ScopeKey",
                table: "FounderSoftwareRemediationAuthorityStates",
                column: "ScopeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendIntelligenceEvaluationContracts_ContractIdentity",
                table: "LegendIntelligenceEvaluationContracts",
                column: "ContractIdentity",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendIntelligenceEvaluationContracts_ContractKey_State",
                table: "LegendIntelligenceEvaluationContracts",
                columns: new[] { "ContractKey", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendIntelligenceEvaluationDomainSnapshots_SnapshotId_DomainKey",
                table: "LegendIntelligenceEvaluationDomainSnapshots",
                columns: new[] { "SnapshotId", "DomainKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendIntelligenceEvaluationPerspectives_SnapshotId_PerspectiveKind",
                table: "LegendIntelligenceEvaluationPerspectives",
                columns: new[] { "SnapshotId", "PerspectiveKind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendIntelligenceEvaluationSignals_ContractId_DomainKey_MetricKey_State_MeasuredUtc",
                table: "LegendIntelligenceEvaluationSignals",
                columns: new[] { "ContractId", "DomainKey", "MetricKey", "State", "MeasuredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendIntelligenceEvaluationSignals_ContractId_EvidenceAuthority_EvidenceReference",
                table: "LegendIntelligenceEvaluationSignals",
                columns: new[] { "ContractId", "EvidenceAuthority", "EvidenceReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendIntelligenceEvaluationSnapshots_ContractId_CreatedUtc",
                table: "LegendIntelligenceEvaluationSnapshots",
                columns: new[] { "ContractId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendIntelligenceEvaluationSnapshots_ContractId_EvidenceSetIdentity",
                table: "LegendIntelligenceEvaluationSnapshots",
                columns: new[] { "ContractId", "EvidenceSetIdentity" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FounderSoftwareRemediationAuthorityStates");

            migrationBuilder.DropTable(
                name: "LegendIntelligenceEvaluationDomainSnapshots");

            migrationBuilder.DropTable(
                name: "LegendIntelligenceEvaluationPerspectives");

            migrationBuilder.DropTable(
                name: "LegendIntelligenceEvaluationSignals");

            migrationBuilder.DropTable(
                name: "LegendIntelligenceEvaluationSnapshots");

            migrationBuilder.DropTable(
                name: "LegendIntelligenceEvaluationContracts");
        }
    }
}
