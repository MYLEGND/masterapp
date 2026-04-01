using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PiiFieldEncryption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Widen PII columns to hold DPAPI ciphertext (~250 chars Base64).
            // AlterColumn is only required on SQL Server; SQLite TEXT has no enforced length.
            // NormalizedEmail columns/indexes are NOT here — they are owned by
            // IdentityEmailNormalization and Stage1IdentityHardening migrations.
            if (ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
                return;

            migrationBuilder.AlterColumn<string>(
                name: "BankAccountNumber",
                table: "OnboardingSubmissions",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "BankRoutingNumber",
                table: "OnboardingSubmissions",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "DriverLicenseNumber",
                table: "OnboardingSubmissions",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(80)",
                oldMaxLength: 80,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "SsnLast4",
                table: "OnboardingSubmissions",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(4)",
                oldMaxLength: 4,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore original SQL Server column widths.
            // WARNING: rolling back after encrypted data has been written will truncate ciphertext.
            if (ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
                return;

            migrationBuilder.AlterColumn<string>(
                name: "BankAccountNumber",
                table: "OnboardingSubmissions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(400)",
                oldMaxLength: 400);

            migrationBuilder.AlterColumn<string>(
                name: "BankRoutingNumber",
                table: "OnboardingSubmissions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(400)",
                oldMaxLength: 400);

            migrationBuilder.AlterColumn<string>(
                name: "DriverLicenseNumber",
                table: "OnboardingSubmissions",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(400)",
                oldMaxLength: 400,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "SsnLast4",
                table: "OnboardingSubmissions",
                type: "nvarchar(4)",
                maxLength: 4,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(400)",
                oldMaxLength: 400,
                oldNullable: true);
        }
    }
}
