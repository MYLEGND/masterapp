using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCommerceBusinessPlatformScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CommerceBusinessMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommerceBusinessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    RoleKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CanManageStorefront = table.Column<bool>(type: "bit", nullable: false),
                    CanManageCatalog = table.Column<bool>(type: "bit", nullable: false),
                    CanManageOrders = table.Column<bool>(type: "bit", nullable: false),
                    CanManageAnalytics = table.Column<bool>(type: "bit", nullable: false),
                    CanManageTeam = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceBusinessMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceBusinessMembers_CommerceBusinesses_CommerceBusinessId",
                        column: x => x.CommerceBusinessId,
                        principalTable: "CommerceBusinesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommerceBusinessStorefrontSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommerceBusinessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandHeadline = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    BrandSubheadline = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    AccentColor = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    LogoUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    StorefrontStatus = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceBusinessStorefrontSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceBusinessStorefrontSettings_CommerceBusinesses_CommerceBusinessId",
                        column: x => x.CommerceBusinessId,
                        principalTable: "CommerceBusinesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommerceBusinessSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommerceBusinessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    PlanName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    MonthlyPriceCents = table.Column<int>(type: "int", nullable: false),
                    BillingProvider = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    BillingCustomerId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    BillingSubscriptionId = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    TrialEndsUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CurrentPeriodEndsUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommerceBusinessSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommerceBusinessSubscriptions_CommerceBusinesses_CommerceBusinessId",
                        column: x => x.CommerceBusinessId,
                        principalTable: "CommerceBusinesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommerceBusinessMembers_CommerceBusinessId_NormalizedEmail",
                table: "CommerceBusinessMembers",
                columns: new[] { "CommerceBusinessId", "NormalizedEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceBusinessMembers_NormalizedEmail",
                table: "CommerceBusinessMembers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "IX_CommerceBusinessStorefrontSettings_CommerceBusinessId",
                table: "CommerceBusinessStorefrontSettings",
                column: "CommerceBusinessId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceBusinessSubscriptions_CommerceBusinessId",
                table: "CommerceBusinessSubscriptions",
                column: "CommerceBusinessId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommerceBusinessSubscriptions_Status",
                table: "CommerceBusinessSubscriptions",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommerceBusinessMembers");

            migrationBuilder.DropTable(
                name: "CommerceBusinessStorefrontSettings");

            migrationBuilder.DropTable(
                name: "CommerceBusinessSubscriptions");
        }
    }
}
