using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20261001193000_ExtendFounderEngineeringActivation")]
    public partial class ExtendFounderEngineeringActivation : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutonomousEngineeringEnabled",
                table: "LegendEngineeringOperationalContract",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "HeadGptModel",
                table: "LegendEngineeringOperationalContract",
                type: "nvarchar(160)",
                maxLength: 160,
                nullable: false,
                defaultValue: "auto");

            migrationBuilder.AddColumn<string>(
                name: "CodexModel",
                table: "LegendEngineeringOperationalContract",
                type: "nvarchar(160)",
                maxLength: 160,
                nullable: false,
                defaultValue: "auto");

            migrationBuilder.AddColumn<string>(
                name: "ReviewerModel",
                table: "LegendEngineeringOperationalContract",
                type: "nvarchar(160)",
                maxLength: 160,
                nullable: false,
                defaultValue: "auto");

            migrationBuilder.AddColumn<bool>(
                name: "AutonomousEngineeringEnabled",
                table: "LegendEngineeringOperationalContractHistory",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "HeadGptModel",
                table: "LegendEngineeringOperationalContractHistory",
                type: "nvarchar(160)",
                maxLength: 160,
                nullable: false,
                defaultValue: "auto");

            migrationBuilder.AddColumn<string>(
                name: "CodexModel",
                table: "LegendEngineeringOperationalContractHistory",
                type: "nvarchar(160)",
                maxLength: 160,
                nullable: false,
                defaultValue: "auto");

            migrationBuilder.AddColumn<string>(
                name: "ReviewerModel",
                table: "LegendEngineeringOperationalContractHistory",
                type: "nvarchar(160)",
                maxLength: 160,
                nullable: false,
                defaultValue: "auto");

            migrationBuilder.CreateTable(
                name: "LegendEngineeringChatGptPlanClientRegistration",
                columns: table => new
                {
                    RegistrationKey = table.Column<string>(
                        type: "nvarchar(64)",
                        maxLength: 64,
                        nullable: false),
                    ClientId = table.Column<string>(
                        type: "nvarchar(200)",
                        maxLength: 200,
                        nullable: false),
                    AuthenticationMethod = table.Column<string>(
                        type: "nvarchar(32)",
                        maxLength: 32,
                        nullable: false),
                    ClientSecretCiphertext = table.Column<string>(
                        type: "nvarchar(max)",
                        nullable: true),
                    EligibilityConfirmed = table.Column<bool>(
                        type: "bit",
                        nullable: false),
                    Revision = table.Column<string>(
                        type: "nvarchar(32)",
                        maxLength: 32,
                        nullable: false),
                    UpdatedUtc = table.Column<DateTime>(
                        type: "datetime2",
                        nullable: false)
                },
                constraints: table =>
                    table.PrimaryKey(
                        "PK_LegendEngineeringChatGptPlanClientRegistration",
                        x => x.RegistrationKey));

            migrationBuilder.CreateTable(
                name: "LegendEngineeringChatGptPlanOAuthTransactions",
                columns: table => new
                {
                    StateHash = table.Column<string>(
                        type: "nvarchar(64)",
                        maxLength: 64,
                        nullable: false),
                    ClientId = table.Column<string>(
                        type: "nvarchar(200)",
                        maxLength: 200,
                        nullable: false),
                    CodeVerifierCiphertext = table.Column<string>(
                        type: "nvarchar(max)",
                        nullable: false),
                    NonceCiphertext = table.Column<string>(
                        type: "nvarchar(max)",
                        nullable: false),
                    RedirectUri = table.Column<string>(
                        type: "nvarchar(512)",
                        maxLength: 512,
                        nullable: false),
                    CreatedUtc = table.Column<DateTime>(
                        type: "datetime2",
                        nullable: false),
                    ExpiresUtc = table.Column<DateTime>(
                        type: "datetime2",
                        nullable: false)
                },
                constraints: table =>
                    table.PrimaryKey(
                        "PK_LegendEngineeringChatGptPlanOAuthTransactions",
                        x => x.StateHash));

            migrationBuilder.CreateIndex(
                name: "IX_LegendEngineeringChatGptPlanOAuthTransactions_ExpiresUtc",
                table: "LegendEngineeringChatGptPlanOAuthTransactions",
                column: "ExpiresUtc");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendEngineeringChatGptPlanOAuthTransactions");

            migrationBuilder.DropTable(
                name: "LegendEngineeringChatGptPlanClientRegistration");

            migrationBuilder.DropColumn(
                name: "AutonomousEngineeringEnabled",
                table: "LegendEngineeringOperationalContract");

            migrationBuilder.DropColumn(
                name: "HeadGptModel",
                table: "LegendEngineeringOperationalContract");

            migrationBuilder.DropColumn(
                name: "CodexModel",
                table: "LegendEngineeringOperationalContract");

            migrationBuilder.DropColumn(
                name: "ReviewerModel",
                table: "LegendEngineeringOperationalContract");

            migrationBuilder.DropColumn(
                name: "AutonomousEngineeringEnabled",
                table: "LegendEngineeringOperationalContractHistory");

            migrationBuilder.DropColumn(
                name: "HeadGptModel",
                table: "LegendEngineeringOperationalContractHistory");

            migrationBuilder.DropColumn(
                name: "CodexModel",
                table: "LegendEngineeringOperationalContractHistory");

            migrationBuilder.DropColumn(
                name: "ReviewerModel",
                table: "LegendEngineeringOperationalContractHistory");
        }
    }
}
