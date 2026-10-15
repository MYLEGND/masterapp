using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Infrastructure.Data;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20261001070000_AddLegendEngineeringControlPlane")]
    public partial class AddLegendEngineeringControlPlane : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegendEngineeringControlLocks",
                columns: table => new
                {
                    LockKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_LegendEngineeringControlLocks", x => x.LockKey));

            // This lock table is deliberately raw SQL state, not an EF entity.
            // Seed it with migration SQL so SQL generation never depends on model mappings.
            migrationBuilder.Sql("""
                INSERT INTO [LegendEngineeringControlLocks] ([LockKey], [Revision])
                VALUES (N'lease-authority', CAST(0 AS bigint)),
                       (N'chatgpt-plan-credential', CAST(0 AS bigint));
                """);

            migrationBuilder.CreateTable(
                name: "LegendEngineeringChatGptPlanCredentials",
                columns: table => new
                {
                    CredentialKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AccessTokenCiphertext = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RefreshTokenCiphertext = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GrantedScopesJson = table.Column<string>(type: "nvarchar(max)", maxLength: 4000, nullable: false),
                    AccessTokenExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    State = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    Revision = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RefreshLeaseIdentity = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    RefreshLeaseUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConnectedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastRefreshedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_LegendEngineeringChatGptPlanCredentials", x => x.CredentialKey));

            migrationBuilder.CreateTable(
                name: "LegendEngineeringWorkItems",
                columns: table => new
                {
                    WorkItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CanonicalAuthorityKey = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    ImpactSetJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    LiveSha = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    EvidenceRevision = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FailureClass = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    RiskClass = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ComplexityScore = table.Column<int>(type: "int", nullable: false),
                    PriorityScore = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    AssignedRole = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LeaseIdentity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LeaseExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", maxLength: 32000, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_LegendEngineeringWorkItems", x => x.WorkItemId));

            migrationBuilder.CreateIndex(
                name: "IX_LegendEngineeringWorkItems_WorkKey",
                table: "LegendEngineeringWorkItems",
                column: "WorkKey",
                unique: true);
            migrationBuilder.CreateIndex(
                name: "IX_LegendEngineeringWorkItems_State_LeaseExpiresUtc",
                table: "LegendEngineeringWorkItems",
                columns: new[] { "State", "LeaseExpiresUtc" });

            migrationBuilder.CreateTable(
                name: "LegendEngineeringContexts",
                columns: table => new
                {
                    EngineeringContextId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractRevision = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PolicyRevision = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    LiveSha = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    EvidenceRevision = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LeaseIdentity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", maxLength: 24000, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendEngineeringContexts", x => x.EngineeringContextId);
                    table.ForeignKey(
                        name: "FK_LegendEngineeringContexts_LegendEngineeringWorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "LegendEngineeringWorkItems",
                        principalColumn: "WorkItemId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendEngineeringContexts_WorkItemId_ExpiresUtc",
                table: "LegendEngineeringContexts",
                columns: new[] { "WorkItemId", "ExpiresUtc" });

            migrationBuilder.CreateTable(
                name: "LegendEngineeringUsage",
                columns: table => new
                {
                    UsageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModelTier = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    SessionId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    InputTokens = table.Column<long>(type: "bigint", nullable: true),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: true),
                    TotalTokens = table.Column<long>(type: "bigint", nullable: true),
                    CostMicrousd = table.Column<long>(type: "bigint", nullable: true),
                    UsageObserved = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_LegendEngineeringUsage", x => x.UsageId));

            migrationBuilder.CreateIndex(
                name: "IX_LegendEngineeringUsage_WorkItemId_CreatedUtc",
                table: "LegendEngineeringUsage",
                columns: new[] { "WorkItemId", "CreatedUtc" });
            migrationBuilder.CreateIndex(
                name: "IX_LegendEngineeringUsage_CreatedUtc",
                table: "LegendEngineeringUsage",
                column: "CreatedUtc");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "LegendEngineeringChatGptPlanCredentials");
            migrationBuilder.DropTable(name: "LegendEngineeringUsage");
            migrationBuilder.DropTable(name: "LegendEngineeringContexts");
            migrationBuilder.DropTable(name: "LegendEngineeringWorkItems");
            migrationBuilder.DropTable(name: "LegendEngineeringControlLocks");
        }
    }
}
