using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    [Migration("20260329093000_ExecutionMvp")]
    public partial class ExecutionMvp : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Blockers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RelatedEntityType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    RelatedEntityId = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    BlockerType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    BlockerReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    BlockerOwnerType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    BlockerOwnerId = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    UnblockDueDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(800)", maxLength: 800, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Blockers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DecisionRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RelatedEntityType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    RelatedEntityId = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    Rationale = table.Column<string>(type: "TEXT", nullable: false),
                    RecommendationType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DecisionRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlaybookExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutionKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybookExecutions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ActionItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RelatedEntityType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    RelatedEntityId = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    OwnerId = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    EffectiveAgentOid = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    DueDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Priority = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    DecisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BlockerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    SourceRef = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DismissedReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActionItems_Blockers_BlockerId",
                        column: x => x.BlockerId,
                        principalTable: "Blockers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ActionItems_DecisionRecords_DecisionId",
                        column: x => x.DecisionId,
                        principalTable: "DecisionRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActionLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    Verb = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActionLogs_ActionItems_ActionId",
                        column: x => x.ActionId,
                        principalTable: "ActionItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActionItems_BlockerId",
                table: "ActionItems",
                column: "BlockerId");

            migrationBuilder.CreateIndex(
                name: "IX_ActionItems_DecisionId",
                table: "ActionItems",
                column: "DecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_ActionItems_EffectiveAgentOid_Status_DueDateUtc",
                table: "ActionItems",
                columns: new[] { "EffectiveAgentOid", "Status", "DueDateUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionItems_OwnerId_Status_DueDateUtc",
                table: "ActionItems",
                columns: new[] { "OwnerId", "Status", "DueDateUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionItems_RelatedEntityType_RelatedEntityId",
                table: "ActionItems",
                columns: new[] { "RelatedEntityType", "RelatedEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionItems_Source_SourceRef",
                table: "ActionItems",
                columns: new[] { "Source", "SourceRef" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionItems_Status_DueDateUtc",
                table: "ActionItems",
                columns: new[] { "Status", "DueDateUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionLogs_ActionId_OccurredUtc",
                table: "ActionLogs",
                columns: new[] { "ActionId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Blockers_RelatedEntityType_RelatedEntityId_Status",
                table: "Blockers",
                columns: new[] { "RelatedEntityType", "RelatedEntityId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Blockers_UnblockDueDateUtc",
                table: "Blockers",
                column: "UnblockDueDateUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DecisionRecords_RelatedEntityType_RelatedEntityId_CreatedUtc",
                table: "DecisionRecords",
                columns: new[] { "RelatedEntityType", "RelatedEntityId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PlaybookExecutions_ExecutionKey",
                table: "PlaybookExecutions",
                column: "ExecutionKey",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActionLogs");

            migrationBuilder.DropTable(
                name: "PlaybookExecutions");

            migrationBuilder.DropTable(
                name: "ActionItems");

            migrationBuilder.DropTable(
                name: "Blockers");

            migrationBuilder.DropTable(
                name: "DecisionRecords");
        }
    }
}
