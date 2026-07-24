using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260724100000_AddPlatformManagedRecurringBilling")]
public partial class AddPlatformManagedRecurringBilling : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(name: "EndedUtc", table: "ClientSubscriptions", type: "datetime2", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "IsPlatformManaged", table: "ClientSubscriptions", type: "bit", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<DateTime>(name: "LastChargeAttemptUtc", table: "ClientSubscriptions", type: "datetime2", nullable: true);
        migrationBuilder.AddColumn<DateTime>(name: "LastSuccessfulChargeUtc", table: "ClientSubscriptions", type: "datetime2", nullable: true);
        migrationBuilder.AddColumn<DateTime>(name: "NextChargeAttemptUtc", table: "ClientSubscriptions", type: "datetime2", nullable: true);
        migrationBuilder.AddColumn<DateTime>(name: "PlatformManagedSinceUtc", table: "ClientSubscriptions", type: "datetime2", nullable: true);

        migrationBuilder.AddColumn<int>(name: "AttemptNumber", table: "SubscriptionPayments", type: "int", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<DateTime>(name: "ClaimedUtc", table: "SubscriptionPayments", type: "datetime2", nullable: true);
        migrationBuilder.AddColumn<string>(name: "ClaimToken", table: "SubscriptionPayments", type: "nvarchar(128)", maxLength: 128, nullable: true);
        migrationBuilder.AddColumn<string>(name: "IdempotencyKey", table: "SubscriptionPayments", type: "nvarchar(128)", maxLength: 128, nullable: true);
        migrationBuilder.AddColumn<string>(name: "Kind", table: "SubscriptionPayments", type: "nvarchar(40)", maxLength: 40, nullable: false, defaultValue: "CommerceOneTime");
        migrationBuilder.AddColumn<string>(name: "ProviderRequestId", table: "SubscriptionPayments", type: "nvarchar(160)", maxLength: 160, nullable: true);
        migrationBuilder.AddColumn<DateTime>(name: "RetryNotBeforeUtc", table: "SubscriptionPayments", type: "datetime2", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "Retryable", table: "SubscriptionPayments", type: "bit", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<DateTime>(name: "ScheduledChargeUtc", table: "SubscriptionPayments", type: "datetime2", nullable: true);

        migrationBuilder.CreateIndex(name: "IX_ClientSubscriptions_IsPlatformManaged_Status_NextBillingDateUtc", table: "ClientSubscriptions", columns: new[] { "IsPlatformManaged", "Status", "NextBillingDateUtc" });
        migrationBuilder.CreateIndex(name: "IX_ClientSubscriptions_Status_NextBillingDateUtc", table: "ClientSubscriptions", columns: new[] { "Status", "NextBillingDateUtc" });
        migrationBuilder.CreateIndex(name: "IX_SubscriptionPayments_ClientSubscriptionId_BillingPeriodStartUtc_AttemptNumber", table: "SubscriptionPayments", columns: new[] { "ClientSubscriptionId", "BillingPeriodStartUtc", "AttemptNumber" }, unique: true, filter: "[ClientSubscriptionId] IS NOT NULL AND [BillingPeriodStartUtc] IS NOT NULL");
        migrationBuilder.CreateIndex(name: "IX_SubscriptionPayments_Provider_ProviderEnvironment_IdempotencyKey", table: "SubscriptionPayments", columns: new[] { "Provider", "ProviderEnvironment", "IdempotencyKey" }, unique: true, filter: "[IdempotencyKey] IS NOT NULL");
        migrationBuilder.CreateIndex(name: "IX_SubscriptionPayments_Status_RetryNotBeforeUtc_ScheduledChargeUtc", table: "SubscriptionPayments", columns: new[] { "Status", "RetryNotBeforeUtc", "ScheduledChargeUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_ClientSubscriptions_IsPlatformManaged_Status_NextBillingDateUtc", table: "ClientSubscriptions");
        migrationBuilder.DropIndex(name: "IX_ClientSubscriptions_Status_NextBillingDateUtc", table: "ClientSubscriptions");
        migrationBuilder.DropIndex(name: "IX_SubscriptionPayments_ClientSubscriptionId_BillingPeriodStartUtc_AttemptNumber", table: "SubscriptionPayments");
        migrationBuilder.DropIndex(name: "IX_SubscriptionPayments_Provider_ProviderEnvironment_IdempotencyKey", table: "SubscriptionPayments");
        migrationBuilder.DropIndex(name: "IX_SubscriptionPayments_Status_RetryNotBeforeUtc_ScheduledChargeUtc", table: "SubscriptionPayments");

        migrationBuilder.DropColumn(name: "EndedUtc", table: "ClientSubscriptions");
        migrationBuilder.DropColumn(name: "IsPlatformManaged", table: "ClientSubscriptions");
        migrationBuilder.DropColumn(name: "LastChargeAttemptUtc", table: "ClientSubscriptions");
        migrationBuilder.DropColumn(name: "LastSuccessfulChargeUtc", table: "ClientSubscriptions");
        migrationBuilder.DropColumn(name: "NextChargeAttemptUtc", table: "ClientSubscriptions");
        migrationBuilder.DropColumn(name: "PlatformManagedSinceUtc", table: "ClientSubscriptions");

        migrationBuilder.DropColumn(name: "AttemptNumber", table: "SubscriptionPayments");
        migrationBuilder.DropColumn(name: "ClaimedUtc", table: "SubscriptionPayments");
        migrationBuilder.DropColumn(name: "ClaimToken", table: "SubscriptionPayments");
        migrationBuilder.DropColumn(name: "IdempotencyKey", table: "SubscriptionPayments");
        migrationBuilder.DropColumn(name: "Kind", table: "SubscriptionPayments");
        migrationBuilder.DropColumn(name: "ProviderRequestId", table: "SubscriptionPayments");
        migrationBuilder.DropColumn(name: "RetryNotBeforeUtc", table: "SubscriptionPayments");
        migrationBuilder.DropColumn(name: "Retryable", table: "SubscriptionPayments");
        migrationBuilder.DropColumn(name: "ScheduledChargeUtc", table: "SubscriptionPayments");
    }
}
