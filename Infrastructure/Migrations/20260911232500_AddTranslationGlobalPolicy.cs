using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTranslationGlobalPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegendTranslationGlobalPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    MonthlyCharacterAllowance = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendTranslationGlobalPolicies", x => x.Id);
                    table.CheckConstraint("CK_LegendTranslationGlobalPolicies_Singleton", "[Id] = 1 AND [MonthlyCharacterAllowance] >= 0");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendTranslationGlobalPolicies");
        }
    }
}
