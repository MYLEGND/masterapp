using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeClientPaymentMethods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var isSqlServer = ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase);

            migrationBuilder.AddColumn<Guid>(
                name: "DefaultPaymentMethodId",
                table: "ClientSubscriptions",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClientPaymentMethodId",
                table: "SubscriptionPayments",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClientPaymentMethods",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    ClientProfileId = table.Column<Guid>(nullable: false),
                    Provider = table.Column<string>(maxLength: 40, nullable: false),
                    ProviderEnvironment = table.Column<string>(maxLength: 40, nullable: false),
                    ProviderPaymentMethodId = table.Column<string>(maxLength: 160, nullable: false),
                    DisplayName = table.Column<string>(maxLength: 80, nullable: true),
                    CardBrand = table.Column<string>(maxLength: 40, nullable: true),
                    Last4 = table.Column<string>(maxLength: 4, nullable: true),
                    ExpirationMonth = table.Column<int>(nullable: true),
                    ExpirationYear = table.Column<int>(nullable: true),
                    CardholderName = table.Column<string>(maxLength: 200, nullable: true),
                    BillingAddressLine1 = table.Column<string>(maxLength: 200, nullable: true),
                    BillingAddressLine2 = table.Column<string>(maxLength: 200, nullable: true),
                    BillingCity = table.Column<string>(maxLength: 120, nullable: true),
                    BillingState = table.Column<string>(maxLength: 120, nullable: true),
                    BillingPostalCode = table.Column<string>(maxLength: 32, nullable: true),
                    BillingCountryCode = table.Column<string>(maxLength: 8, nullable: true),
                    CreatedUtc = table.Column<DateTime>(nullable: false),
                    UpdatedUtc = table.Column<DateTime>(nullable: false),
                    RetiredUtc = table.Column<DateTime>(nullable: true),
                    RowVersion = table.Column<byte[]>(
                        type: isSqlServer ? "rowversion" : "BLOB",
                        rowVersion: isSqlServer,
                        nullable: false,
                        defaultValueSql: isSqlServer ? null : "X''")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientPaymentMethods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientPaymentMethods_ClientProfiles_ClientProfileId",
                        column: x => x.ClientProfileId,
                        principalTable: "ClientProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            if (ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    ;WITH DistinctStoredMethods AS
                    (
                        SELECT
                            subscription.[ClientProfileId],
                            subscription.[Provider],
                            subscription.[ProviderEnvironment],
                            subscription.[ProviderPaymentMethodId],
                            subscription.[PaymentMethodBrand],
                            subscription.[PaymentMethodLast4],
                            subscription.[PaymentMethodExpirationMonth],
                            subscription.[PaymentMethodExpirationYear],
                            subscription.[PaymentMethodCardholderName],
                            subscription.[CreatedUtc],
                            subscription.[PaymentMethodUpdatedUtc],
                            ROW_NUMBER() OVER
                            (
                                PARTITION BY subscription.[Provider], subscription.[ProviderEnvironment], subscription.[ProviderPaymentMethodId]
                                ORDER BY subscription.[PaymentMethodUpdatedUtc] DESC, subscription.[UpdatedUtc] DESC, subscription.[CreatedUtc] DESC
                            ) AS [RowNumber]
                        FROM [ClientSubscriptions] AS subscription
                        WHERE subscription.[ProviderPaymentMethodId] IS NOT NULL
                          AND LTRIM(RTRIM(subscription.[ProviderPaymentMethodId])) <> ''
                    )
                    INSERT INTO [ClientPaymentMethods]
                    (
                        [Id], [ClientProfileId], [Provider], [ProviderEnvironment], [ProviderPaymentMethodId],
                        [CardBrand], [Last4], [ExpirationMonth], [ExpirationYear], [CardholderName],
                        [CreatedUtc], [UpdatedUtc]
                    )
                    SELECT
                        NEWID(), [ClientProfileId], [Provider], [ProviderEnvironment], [ProviderPaymentMethodId],
                        [PaymentMethodBrand], [PaymentMethodLast4], [PaymentMethodExpirationMonth], [PaymentMethodExpirationYear], [PaymentMethodCardholderName],
                        [CreatedUtc], COALESCE([PaymentMethodUpdatedUtc], [CreatedUtc])
                    FROM DistinctStoredMethods
                    WHERE [RowNumber] = 1;

                    UPDATE subscription
                    SET [DefaultPaymentMethodId] = paymentMethod.[Id]
                    FROM [ClientSubscriptions] AS subscription
                    INNER JOIN [ClientPaymentMethods] AS paymentMethod
                        ON paymentMethod.[Provider] = subscription.[Provider]
                       AND paymentMethod.[ProviderEnvironment] = subscription.[ProviderEnvironment]
                       AND paymentMethod.[ProviderPaymentMethodId] = subscription.[ProviderPaymentMethodId];
                    """);
            }
            else if (ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    INSERT INTO "ClientPaymentMethods"
                    (
                        "Id", "ClientProfileId", "Provider", "ProviderEnvironment", "ProviderPaymentMethodId",
                        "CardBrand", "Last4", "ExpirationMonth", "ExpirationYear", "CardholderName",
                        "CreatedUtc", "UpdatedUtc"
                    )
                    SELECT
                        lower(
                            substr(hex(randomblob(4)), 1, 8) || '-' ||
                            substr(hex(randomblob(2)), 1, 4) || '-' ||
                            substr(hex(randomblob(2)), 1, 4) || '-' ||
                            substr(hex(randomblob(2)), 1, 4) || '-' ||
                            substr(hex(randomblob(6)), 1, 12)),
                        subscription."ClientProfileId", subscription."Provider", subscription."ProviderEnvironment", subscription."ProviderPaymentMethodId",
                        subscription."PaymentMethodBrand", subscription."PaymentMethodLast4", subscription."PaymentMethodExpirationMonth", subscription."PaymentMethodExpirationYear", subscription."PaymentMethodCardholderName",
                        subscription."CreatedUtc", COALESCE(subscription."PaymentMethodUpdatedUtc", subscription."CreatedUtc")
                    FROM "ClientSubscriptions" AS subscription
                    WHERE subscription."ProviderPaymentMethodId" IS NOT NULL
                      AND trim(subscription."ProviderPaymentMethodId") <> ''
                      AND NOT EXISTS
                      (
                          SELECT 1
                          FROM "ClientPaymentMethods" AS existingMethod
                          WHERE existingMethod."Provider" = subscription."Provider"
                            AND existingMethod."ProviderEnvironment" = subscription."ProviderEnvironment"
                            AND existingMethod."ProviderPaymentMethodId" = subscription."ProviderPaymentMethodId"
                      );

                    UPDATE "ClientSubscriptions"
                    SET "DefaultPaymentMethodId" =
                    (
                        SELECT paymentMethod."Id"
                        FROM "ClientPaymentMethods" AS paymentMethod
                        WHERE paymentMethod."Provider" = "ClientSubscriptions"."Provider"
                          AND paymentMethod."ProviderEnvironment" = "ClientSubscriptions"."ProviderEnvironment"
                          AND paymentMethod."ProviderPaymentMethodId" = "ClientSubscriptions"."ProviderPaymentMethodId"
                    );
                    """);
            }

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

            migrationBuilder.DropColumn(
                name: "ProviderPaymentMethodId",
                table: "ClientSubscriptions");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPayments_ClientPaymentMethodId",
                table: "SubscriptionPayments",
                column: "ClientPaymentMethodId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSubscriptions_DefaultPaymentMethodId",
                table: "ClientSubscriptions",
                column: "DefaultPaymentMethodId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientPaymentMethods_ClientProfileId_RetiredUtc",
                table: "ClientPaymentMethods",
                columns: new[] { "ClientProfileId", "RetiredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientPaymentMethods_Provider_ProviderEnvironment_ProviderPaymentMethodId",
                table: "ClientPaymentMethods",
                columns: new[] { "Provider", "ProviderEnvironment", "ProviderPaymentMethodId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientSubscriptions_ClientPaymentMethods_DefaultPaymentMethodId",
                table: "ClientSubscriptions",
                column: "DefaultPaymentMethodId",
                principalTable: "ClientPaymentMethods",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_SubscriptionPayments_ClientPaymentMethods_ClientPaymentMethodId",
                table: "SubscriptionPayments",
                column: "ClientPaymentMethodId",
                principalTable: "ClientPaymentMethods",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClientSubscriptions_ClientPaymentMethods_DefaultPaymentMethodId",
                table: "ClientSubscriptions");

            migrationBuilder.DropForeignKey(
                name: "FK_SubscriptionPayments_ClientPaymentMethods_ClientPaymentMethodId",
                table: "SubscriptionPayments");

            migrationBuilder.DropIndex(
                name: "IX_SubscriptionPayments_ClientPaymentMethodId",
                table: "SubscriptionPayments");

            migrationBuilder.DropIndex(
                name: "IX_ClientSubscriptions_DefaultPaymentMethodId",
                table: "ClientSubscriptions");

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

            migrationBuilder.AddColumn<string>(
                name: "ProviderPaymentMethodId",
                table: "ClientSubscriptions",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PaymentMethodUpdatedUtc",
                table: "ClientSubscriptions",
                nullable: true);

            if (ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    UPDATE subscription
                    SET
                        [ProviderPaymentMethodId] = paymentMethod.[ProviderPaymentMethodId],
                        [PaymentMethodBrand] = paymentMethod.[CardBrand],
                        [PaymentMethodLast4] = paymentMethod.[Last4],
                        [PaymentMethodExpirationMonth] = paymentMethod.[ExpirationMonth],
                        [PaymentMethodExpirationYear] = paymentMethod.[ExpirationYear],
                        [PaymentMethodCardholderName] = paymentMethod.[CardholderName],
                        [PaymentMethodUpdatedUtc] = paymentMethod.[UpdatedUtc]
                    FROM [ClientSubscriptions] AS subscription
                    INNER JOIN [ClientPaymentMethods] AS paymentMethod
                        ON paymentMethod.[Id] = subscription.[DefaultPaymentMethodId];
                    """);
            }
            else if (ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    UPDATE "ClientSubscriptions"
                    SET
                        "ProviderPaymentMethodId" = (SELECT "ProviderPaymentMethodId" FROM "ClientPaymentMethods" WHERE "Id" = "ClientSubscriptions"."DefaultPaymentMethodId"),
                        "PaymentMethodBrand" = (SELECT "CardBrand" FROM "ClientPaymentMethods" WHERE "Id" = "ClientSubscriptions"."DefaultPaymentMethodId"),
                        "PaymentMethodLast4" = (SELECT "Last4" FROM "ClientPaymentMethods" WHERE "Id" = "ClientSubscriptions"."DefaultPaymentMethodId"),
                        "PaymentMethodExpirationMonth" = (SELECT "ExpirationMonth" FROM "ClientPaymentMethods" WHERE "Id" = "ClientSubscriptions"."DefaultPaymentMethodId"),
                        "PaymentMethodExpirationYear" = (SELECT "ExpirationYear" FROM "ClientPaymentMethods" WHERE "Id" = "ClientSubscriptions"."DefaultPaymentMethodId"),
                        "PaymentMethodCardholderName" = (SELECT "CardholderName" FROM "ClientPaymentMethods" WHERE "Id" = "ClientSubscriptions"."DefaultPaymentMethodId"),
                        "PaymentMethodUpdatedUtc" = (SELECT "UpdatedUtc" FROM "ClientPaymentMethods" WHERE "Id" = "ClientSubscriptions"."DefaultPaymentMethodId");
                    """);
            }

            migrationBuilder.DropColumn(
                name: "DefaultPaymentMethodId",
                table: "ClientSubscriptions");

            migrationBuilder.DropColumn(
                name: "ClientPaymentMethodId",
                table: "SubscriptionPayments");

            migrationBuilder.DropTable(
                name: "ClientPaymentMethods");
        }
    }
}
