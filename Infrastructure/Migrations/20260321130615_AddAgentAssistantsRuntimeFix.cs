using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentAssistantsRuntimeFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var isSqlServer = migrationBuilder.ActiveProvider.Contains(
                "SqlServer",
                StringComparison.OrdinalIgnoreCase);

            string GuidType() => isSqlServer ? "uniqueidentifier" : "TEXT";
            string DateType() => isSqlServer ? "datetime2" : "TEXT";
            string BoolType() => isSqlServer ? "bit" : "INTEGER";
            string StringType(int maxLength) =>
                isSqlServer ? $"nvarchar({maxLength})" : "TEXT";

            migrationBuilder.CreateTable(
                name: "AgentAssistants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: GuidType(), nullable: false),
                    ParentAgentUserId = table.Column<string>(type: StringType(450), maxLength: 450, nullable: false),
                    AssistantUserId = table.Column<string>(type: StringType(450), maxLength: 450, nullable: true),
                    FirstName = table.Column<string>(type: StringType(100), maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: StringType(100), maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: StringType(320), maxLength: 320, nullable: false),
                    IsActive = table.Column<bool>(type: BoolType(), nullable: false),
                    InvitedAt = table.Column<DateTime>(type: DateType(), nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: DateType(), nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentAssistants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentAssistants_AssistantUserId",
                table: "AgentAssistants",
                column: "AssistantUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentAssistants_ParentAgentUserId",
                table: "AgentAssistants",
                column: "ParentAgentUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentAssistants_ParentAgentUserId_Email",
                table: "AgentAssistants",
                columns: new[] { "ParentAgentUserId", "Email" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentAssistants");
        }
    }
}
