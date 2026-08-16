using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendCurriculumManifestWorkQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegendCurriculumManifestWorkItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FounderUserId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ManifestHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FamilyCount = table.Column<int>(type: "int", nullable: false),
                    ExampleCount = table.Column<int>(type: "int", nullable: false),
                    NextFamilyIndex = table.Column<int>(type: "int", nullable: false),
                    ProcessingState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LastErrorCode = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    LastErrorMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LeaseExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendCurriculumManifestWorkItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendCurriculumManifestWorkItems_Identity",
                table: "LegendCurriculumManifestWorkItems",
                columns: new[] { "FounderUserId", "ManifestHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegendCurriculumManifestWorkItems_Processing",
                table: "LegendCurriculumManifestWorkItems",
                columns: new[] { "ProcessingState", "LeaseExpiresUtc", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendCurriculumManifestWorkItems");
        }
    }
}
