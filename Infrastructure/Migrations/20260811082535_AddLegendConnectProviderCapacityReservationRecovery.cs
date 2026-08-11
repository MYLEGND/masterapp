using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendConnectProviderCapacityReservationRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegendTranslationProviderReservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    BillingPeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    ReservationReference = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Characters = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ReservationExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendTranslationProviderReservations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationProviderReservations_Provider_BillingPeriodStart_State_ReservationExpiresUtc",
                table: "LegendTranslationProviderReservations",
                columns: new[] { "Provider", "BillingPeriodStart", "State", "ReservationExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegendTranslationProviderReservations_Provider_ReservationReference",
                table: "LegendTranslationProviderReservations",
                columns: new[] { "Provider", "ReservationReference" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendTranslationProviderReservations");
        }
    }
}
