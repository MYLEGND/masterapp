using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

public partial class AddFounderAiMessageProvenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "AuthorKind", table: "InternalMessages",
            type: "nvarchar(32)", maxLength: 32, nullable: false, defaultValue: "Human");
        migrationBuilder.AddColumn<string>(name: "AiTurnMetadataJson", table: "InternalMessages",
            type: "nvarchar(max)", maxLength: 1000000, nullable: true);
        // SQL Server already uses nvarchar(max) for the former 10,000-character
        // mapping. Remove that model limit without changing existing bodies.
        migrationBuilder.AlterColumn<string>(name: "Body", table: "InternalMessages",
            type: "nvarchar(max)", nullable: false, oldClrType: typeof(string),
            oldType: "nvarchar(max)", oldMaxLength: 10000);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Rollback requires preserving/exporting any new-format transcripts
        // first. Silently dropping provenance or reinterpreting model output
        // as human-authored messages would destroy the security boundary.
        if (ActiveProvider.Contains("SqlServer"))
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [InternalMessages]
                    WHERE [AuthorKind] <> 'Human' OR [AiTurnMetadataJson] IS NOT NULL
                        OR DATALENGTH([Body]) > 20000)
                    THROW 51000, 'Preserve Founder AI transcripts before reverting message provenance.', 1;
                """);
        else
            throw new System.NotSupportedException("Reverse this migration only through a reviewed SQL Server rollback after preserving Founder AI transcripts.");
        migrationBuilder.AlterColumn<string>(name: "Body", table: "InternalMessages",
            type: "nvarchar(max)", maxLength: 10000, nullable: false,
            oldClrType: typeof(string), oldType: "nvarchar(max)");
        migrationBuilder.DropColumn(name: "AiTurnMetadataJson", table: "InternalMessages");
        migrationBuilder.DropColumn(name: "AuthorKind", table: "InternalMessages");
    }
}
