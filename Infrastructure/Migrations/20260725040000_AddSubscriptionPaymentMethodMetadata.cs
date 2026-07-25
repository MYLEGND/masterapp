using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260725040000_AddSubscriptionPaymentMethodMetadata")]
public partial class AddSubscriptionPaymentMethodMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PaymentMethodBrand",
            table: "ClientSubscriptions",
            maxLength: 40,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PaymentMethodCardholderName",
            table: "ClientSubscriptions",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "PaymentMethodExpirationMonth",
            table: "ClientSubscriptions",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "PaymentMethodExpirationYear",
            table: "ClientSubscriptions",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PaymentMethodLast4",
            table: "ClientSubscriptions",
            maxLength: 4,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "PaymentMethodUpdatedUtc",
            table: "ClientSubscriptions",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PaymentMethodBrand",
            table: "ClientSubscriptions");

        migrationBuilder.DropColumn(
            name: "PaymentMethodCardholderName",
            table: "ClientSubscriptions");

        migrationBuilder.DropColumn(
            name: "PaymentMethodExpirationMonth",
            table: "ClientSubscriptions");

        migrationBuilder.DropColumn(
            name: "PaymentMethodExpirationYear",
            table: "ClientSubscriptions");

        migrationBuilder.DropColumn(
            name: "PaymentMethodLast4",
            table: "ClientSubscriptions");

        migrationBuilder.DropColumn(
            name: "PaymentMethodUpdatedUtc",
            table: "ClientSubscriptions");
    }
}
