using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimeDiagnosticsAndFounderRepairBatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FounderSoftwareRepairBatches",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    BaseSha = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    HeadSha = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    PullRequestNumber = table.Column<int>(type: "int", nullable: true),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    OperationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Revision = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FounderSoftwareRepairBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeDiagnosticIncidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeduplicationKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AppIdentifier = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Platform = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Route = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ErrorName = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StatusCode = table.Column<int>(type: "int", nullable: true),
                    GitCommitHash = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ReleaseVerified = table.Column<bool>(type: "bit", nullable: false),
                    AppVersion = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    SourceFilePath = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: true),
                    StackTrace = table.Column<string>(type: "nvarchar(2300)", maxLength: 2300, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Disposition = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReviewVersion = table.Column<int>(type: "int", nullable: false),
                    Recurred = table.Column<bool>(type: "bit", nullable: false),
                    ReviewedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Occurrences = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeDiagnosticIncidents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeDiagnosticIncidents_DeduplicationKey",
                table: "RuntimeDiagnosticIncidents",
                column: "DeduplicationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeDiagnosticIncidents_ExpiresUtc",
                table: "RuntimeDiagnosticIncidents",
                column: "ExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeDiagnosticIncidents_LastSeenUtc",
                table: "RuntimeDiagnosticIncidents",
                column: "LastSeenUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FounderSoftwareRepairBatches");

            migrationBuilder.DropTable(
                name: "RuntimeDiagnosticIncidents");
        }
    }
}
