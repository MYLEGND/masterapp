using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendConnectModelTrainingLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegendConnectModelTrainingRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunKey = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    ScopeKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Generation = table.Column<int>(type: "int", nullable: false),
                    DatasetIdentity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DatasetEvaluatorVersion = table.Column<int>(type: "int", nullable: false),
                    TrainingProvider = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    BaseModel = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    TrainingFileId = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                    ExternalJobId = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                    ChallengerModelVersion = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                    State = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    EvaluationState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PromotionState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    TrainingExampleCount = table.Column<int>(type: "int", nullable: false),
                    ValidationExampleCount = table.Column<int>(type: "int", nullable: false),
                    HeldOutScore = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    RegressionScore = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LeaseExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailureCode = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    FailureDetail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PromotedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendConnectModelTrainingRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendConnectModelTrainingRuns_Promotion",
                table: "LegendConnectModelTrainingRuns",
                columns: new[] { "ScopeKey", "PromotionState", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendConnectModelTrainingRuns_RunKey",
                table: "LegendConnectModelTrainingRuns",
                column: "RunKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendConnectModelTrainingRuns_ScopeGeneration",
                table: "LegendConnectModelTrainingRuns",
                columns: new[] { "ScopeKey", "Generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendConnectModelTrainingRuns_Work",
                table: "LegendConnectModelTrainingRuns",
                columns: new[] { "State", "LeaseExpiresUtc", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendConnectModelTrainingRuns");
        }
    }
}
