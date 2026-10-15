using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <summary>
    /// Collapses the retired business-only finance tool identities into the
    /// canonical ExpenseLens and SavingsAccelerator authorities. Business
    /// behavior is selected by client context/templates, not a parallel tool id.
    /// </summary>
    public partial class CanonicalizeBusinessFinanceToolState : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE canonical
                FROM [FinanceToolStates] canonical
                INNER JOIN [FinanceToolStates] business
                    ON business.[HouseholdAccountId] = canonical.[HouseholdAccountId]
                WHERE canonical.[ToolId] = N'ExpenseLens'
                  AND business.[ToolId] = N'BusinessExpenseLens';

                UPDATE [FinanceToolStates]
                SET [ToolId] = N'ExpenseLens'
                WHERE [ToolId] = N'BusinessExpenseLens';

                DELETE canonical
                FROM [FinanceToolStates] canonical
                INNER JOIN [FinanceToolStates] business
                    ON business.[HouseholdAccountId] = canonical.[HouseholdAccountId]
                WHERE canonical.[ToolId] = N'SavingsAccelerator'
                  AND business.[ToolId] = N'BusinessSavingsAccelerator';

                UPDATE [FinanceToolStates]
                SET [ToolId] = N'SavingsAccelerator'
                WHERE [ToolId] = N'BusinessSavingsAccelerator';

                DELETE canonical
                FROM [AgentFinanceToolStates] canonical
                INNER JOIN [AgentFinanceToolStates] business
                    ON business.[AgentUserId] = canonical.[AgentUserId]
                WHERE canonical.[ToolId] = N'ExpenseLens'
                  AND business.[ToolId] = N'BusinessExpenseLens';

                UPDATE [AgentFinanceToolStates]
                SET [ToolId] = N'ExpenseLens'
                WHERE [ToolId] = N'BusinessExpenseLens';

                DELETE canonical
                FROM [AgentFinanceToolStates] canonical
                INNER JOIN [AgentFinanceToolStates] business
                    ON business.[AgentUserId] = canonical.[AgentUserId]
                WHERE canonical.[ToolId] = N'SavingsAccelerator'
                  AND business.[ToolId] = N'BusinessSavingsAccelerator';

                UPDATE [AgentFinanceToolStates]
                SET [ToolId] = N'SavingsAccelerator'
                WHERE [ToolId] = N'BusinessSavingsAccelerator';

                UPDATE [ExpenseLensStreamLinks]
                SET [ExpenseLensToolId] = N'ExpenseLens'
                WHERE [ExpenseLensToolId] = N'BusinessExpenseLens';
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE stateRow
                SET [ToolId] = N'BusinessExpenseLens'
                FROM [FinanceToolStates] stateRow
                INNER JOIN [ClientProfiles] profile
                    ON profile.[Id] = stateRow.[ClientProfileId]
                WHERE stateRow.[ToolId] = N'ExpenseLens'
                  AND JSON_VALUE(profile.[CrmNotes], '$.recordType') IN (N'BusinessClient', N'Business Client');

                UPDATE stateRow
                SET [ToolId] = N'BusinessSavingsAccelerator'
                FROM [FinanceToolStates] stateRow
                INNER JOIN [ClientProfiles] profile
                    ON profile.[Id] = stateRow.[ClientProfileId]
                WHERE stateRow.[ToolId] = N'SavingsAccelerator'
                  AND JSON_VALUE(profile.[CrmNotes], '$.recordType') IN (N'BusinessClient', N'Business Client');

                UPDATE linkRow
                SET [ExpenseLensToolId] = N'BusinessExpenseLens'
                FROM [ExpenseLensStreamLinks] linkRow
                INNER JOIN [ClientProfiles] profile
                    ON profile.[Id] = linkRow.[ClientProfileId]
                WHERE linkRow.[ExpenseLensToolId] = N'ExpenseLens'
                  AND JSON_VALUE(profile.[CrmNotes], '$.recordType') IN (N'BusinessClient', N'Business Client');
                """);
        }
    }
}
