using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260725050000_HardenMessagingParticipantIdentity")]
public partial class HardenMessagingParticipantIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_MessageConversationParticipants_ConversationId_UserId",
            table: "MessageConversationParticipants");

        migrationBuilder.CreateIndex(
            name: "IX_MessageConversationParticipants_ConversationId_UserId_ParticipantType",
            table: "MessageConversationParticipants",
            columns: new[] { "ConversationId", "UserId", "ParticipantType" },
            unique: true);

        if (ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer")
        {
            migrationBuilder.Sql("""
                UPDATE [dbo].[MessageConversations]
                SET [DirectConversationKey] = NULL
                WHERE [ConversationType] IN (N'AgentDirect', N'ClientAgent', N'ClientJourney');

                ;WITH [RankedParticipants] AS
                (
                    SELECT
                        [participant].[ConversationId],
                        [participant].[ParticipantType],
                        LOWER(LTRIM(RTRIM([participant].[UserId]))) AS [UserId],
                        ROW_NUMBER() OVER
                        (
                            PARTITION BY [participant].[ConversationId]
                            ORDER BY [participant].[ParticipantType], LOWER(LTRIM(RTRIM([participant].[UserId]))), [participant].[Id]
                        ) AS [ParticipantOrder],
                        COUNT(*) OVER (PARTITION BY [participant].[ConversationId]) AS [ParticipantCount]
                    FROM [dbo].[MessageConversationParticipants] AS [participant]
                    WHERE [participant].[IsActive] = 1
                ),
                [CandidateKeys] AS
                (
                    SELECT
                        [conversation].[Id],
                        CONCAT(
                            [conversation].[ConversationType],
                            N'|',
                            MAX(CASE WHEN [participant].[ParticipantOrder] = 1 THEN CONCAT(LEN([participant].[ParticipantType]), N':', [participant].[ParticipantType], LEN([participant].[UserId]), N':', [participant].[UserId]) END),
                            N'|',
                            MAX(CASE WHEN [participant].[ParticipantOrder] = 2 THEN CONCAT(LEN([participant].[ParticipantType]), N':', [participant].[ParticipantType], LEN([participant].[UserId]), N':', [participant].[UserId]) END)
                        ) AS [DirectConversationKey]
                    FROM [dbo].[MessageConversations] AS [conversation]
                    INNER JOIN [RankedParticipants] AS [participant]
                        ON [participant].[ConversationId] = [conversation].[Id]
                    WHERE [conversation].[ConversationType] IN (N'AgentDirect', N'ClientAgent', N'ClientJourney')
                    GROUP BY [conversation].[Id], [conversation].[ConversationType]
                    HAVING MAX([participant].[ParticipantCount]) = 2 AND COUNT(*) = 2
                ),
                [CanonicalKeys] AS
                (
                    SELECT
                        [Id],
                        [DirectConversationKey],
                        ROW_NUMBER() OVER
                        (
                            PARTITION BY [DirectConversationKey]
                            ORDER BY [Id]
                        ) AS [IdentityRank]
                    FROM [CandidateKeys]
                )
                UPDATE [conversation]
                SET [DirectConversationKey] = [key].[DirectConversationKey]
                FROM [dbo].[MessageConversations] AS [conversation]
                INNER JOIN [CanonicalKeys] AS [key]
                    ON [key].[Id] = [conversation].[Id]
                WHERE [key].[IdentityRank] = 1;
                """);
        }
        else if (ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            migrationBuilder.Sql("""
                UPDATE "MessageConversations"
                SET "DirectConversationKey" = NULL
                WHERE "ConversationType" IN ('AgentDirect', 'ClientAgent', 'ClientJourney');

                WITH "RankedParticipants" AS
                (
                    SELECT
                        "participant"."ConversationId",
                        "participant"."ParticipantType",
                        lower(trim("participant"."UserId")) AS "UserId",
                        ROW_NUMBER() OVER
                        (
                            PARTITION BY "participant"."ConversationId"
                            ORDER BY "participant"."ParticipantType", lower(trim("participant"."UserId")), "participant"."Id"
                        ) AS "ParticipantOrder",
                        COUNT(*) OVER (PARTITION BY "participant"."ConversationId") AS "ParticipantCount"
                    FROM "MessageConversationParticipants" AS "participant"
                    WHERE "participant"."IsActive" = 1
                ),
                "CandidateKeys" AS
                (
                    SELECT
                        "conversation"."Id",
                        "conversation"."ConversationType" || '|' ||
                        MAX(CASE WHEN "participant"."ParticipantOrder" = 1 THEN length("participant"."ParticipantType") || ':' || "participant"."ParticipantType" || length("participant"."UserId") || ':' || "participant"."UserId" END) || '|' ||
                        MAX(CASE WHEN "participant"."ParticipantOrder" = 2 THEN length("participant"."ParticipantType") || ':' || "participant"."ParticipantType" || length("participant"."UserId") || ':' || "participant"."UserId" END) AS "DirectConversationKey"
                    FROM "MessageConversations" AS "conversation"
                    INNER JOIN "RankedParticipants" AS "participant"
                        ON "participant"."ConversationId" = "conversation"."Id"
                    WHERE "conversation"."ConversationType" IN ('AgentDirect', 'ClientAgent', 'ClientJourney')
                    GROUP BY "conversation"."Id", "conversation"."ConversationType"
                    HAVING MAX("participant"."ParticipantCount") = 2 AND COUNT(*) = 2
                ),
                "CanonicalKeys" AS
                (
                    SELECT
                        "Id",
                        "DirectConversationKey",
                        ROW_NUMBER() OVER
                        (
                            PARTITION BY "DirectConversationKey"
                            ORDER BY "Id"
                        ) AS "IdentityRank"
                    FROM "CandidateKeys"
                )
                UPDATE "MessageConversations"
                SET "DirectConversationKey" =
                (
                    SELECT "DirectConversationKey"
                    FROM "CanonicalKeys"
                    WHERE "CanonicalKeys"."Id" = "MessageConversations"."Id"
                      AND "CanonicalKeys"."IdentityRank" = 1
                )
                WHERE "Id" IN
                (
                    SELECT "Id"
                    FROM "CanonicalKeys"
                    WHERE "IdentityRank" = 1
                );
                """);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_MessageConversationParticipants_ConversationId_UserId_ParticipantType",
            table: "MessageConversationParticipants");

        migrationBuilder.CreateIndex(
            name: "IX_MessageConversationParticipants_ConversationId_UserId",
            table: "MessageConversationParticipants",
            columns: new[] { "ConversationId", "UserId" },
            unique: true);
    }
}
