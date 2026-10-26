using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCanonicalSubscriptionPaymentCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CommerceCaptureVerifiedUtc",
                table: "SubscriptionPayments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommerceCompletionContextJson",
                table: "SubscriptionPayments",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SubscriptionPaymentCompletionEffects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubscriptionPaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    SafeErrorCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionPaymentCompletionEffects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubscriptionPaymentCompletionEffects_SubscriptionPayments_SubscriptionPaymentId",
                        column: x => x.SubscriptionPaymentId,
                        principalTable: "SubscriptionPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionPaymentRefunds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubscriptionPaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ProviderReference = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionPaymentRefunds", x => x.Id);
                    table.CheckConstraint("CK_SubscriptionPaymentRefunds_PositiveAmount", "[AmountMinor] > 0");
                    table.ForeignKey(
                        name: "FK_SubscriptionPaymentRefunds_SubscriptionPayments_SubscriptionPaymentId",
                        column: x => x.SubscriptionPaymentId,
                        principalTable: "SubscriptionPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPaymentCompletionEffects_SubscriptionPaymentId_Kind",
                table: "SubscriptionPaymentCompletionEffects",
                columns: new[] { "SubscriptionPaymentId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPaymentRefunds_SubscriptionPaymentId_IdempotencyKey",
                table: "SubscriptionPaymentRefunds",
                columns: new[] { "SubscriptionPaymentId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Financial/recovery history is never discarded by application rollback.
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [SubscriptionPaymentCompletionEffects]) OR EXISTS (SELECT 1 FROM [SubscriptionPaymentRefunds]) OR EXISTS (SELECT 1 FROM [SubscriptionPayments] WHERE [CommerceCaptureVerifiedUtc] IS NOT NULL OR [CommerceCompletionContextJson] IS NOT NULL) THROW 51000, 'Canonical payment history exists; use a compatible application rollback.', 1;");
            migrationBuilder.DropTable(
                name: "SubscriptionPaymentCompletionEffects");

            migrationBuilder.DropTable(
                name: "SubscriptionPaymentRefunds");

            migrationBuilder.DropColumn(
                name: "CommerceCaptureVerifiedUtc",
                table: "SubscriptionPayments");

            migrationBuilder.DropColumn(
                name: "CommerceCompletionContextJson",
                table: "SubscriptionPayments");
        }
    }
}
