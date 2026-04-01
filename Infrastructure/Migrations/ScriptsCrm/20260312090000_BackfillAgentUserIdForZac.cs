using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations.ScriptsCrm
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260312090000_BackfillAgentUserIdForZac")]
    public partial class BackfillAgentUserIdForZac : Migration
    {
        // Zac Owen (zac.owen@mylegnd.com) canonical agent object id
        private const string ZacAgentId = "9a578496-dafd-4c10-aba5-a8386da25e53";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
UPDATE ScriptLeadProfiles
SET AgentUserId = '{ZacAgentId}'
WHERE AgentUserId IS NULL
   OR LTRIM(RTRIM(AgentUserId)) = '';");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
UPDATE ScriptLeadProfiles
SET AgentUserId = ''
WHERE AgentUserId = '{ZacAgentId}';");
        }
    }
}
