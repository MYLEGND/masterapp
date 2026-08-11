using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConsentedLiveTranslationLearning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowsConsentedTranslationLearning",
                table: "MobileProfileSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PromotionOutcome",
                table: "LegendTranslationLearningEvents",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowsConsentedTranslationLearning",
                table: "MobileProfileSettings");

            migrationBuilder.DropColumn(
                name: "PromotionOutcome",
                table: "LegendTranslationLearningEvents");
        }
    }
}
