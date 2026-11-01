using Domain.Billing;
using Infrastructure.Billing;
using Infrastructure.Data;
using Infrastructure.Leads;
using Infrastructure.Households;
using Infrastructure.Identity;
using System;
using System.Linq;
using System.Threading.Tasks;
using Legend.Commerce;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class TrainingEnrollmentDependencyResolutionTests
{
    [Fact]
    public void CanonicalCommerceAndBillingRegistrationsResolveTrainingEnrollmentWithoutExternalEffects()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        // Construct only: no connection opening, schema creation, query or write.
        services.AddDbContext<MasterAppDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        services.AddLegendCommerceCore();
        services.AddMasterAppBilling(configuration);
        var gateway = new Mock<IBillingGateway>(MockBehavior.Strict);
        var mail = new Mock<IWebsiteInquiryEmailSender>(MockBehavior.Strict);
        services.AddScoped<IBillingGateway>(_ => gateway.Object);
        services.AddScoped<IWebsiteInquiryEmailSender>(_ => mail.Object);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<TrainingEnrollmentService>());
        gateway.VerifyNoOtherCalls();
        mail.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HouseholdEntitlementStillRequiresRealProvisioningConfiguration()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDbContext<MasterAppDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        services.AddMasterAppBilling(configuration);
        Assert.Equal(typeof(HouseholdMembershipService), services.Single(x =>
            x.ServiceType == typeof(IHouseholdMembershipService)).ImplementationType);
        Assert.Equal(typeof(ClientEntraLifecycleService), services.Single(x =>
            x.ServiceType == typeof(IClientEntraLifecycleService)).ImplementationType);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        var entitlements = scope.ServiceProvider.GetRequiredService<IBillingEntitlementService>();
        // Unknown entitlements neither resolve provisioning nor query the database.
        var unknown = await entitlements.EvaluateAsync(new BillingEntitlementEvaluationRequest(
            Guid.NewGuid(), "unknown", DateTime.UtcNow));
        Assert.Equal("UNKNOWN_ENTITLEMENT", unknown.ReasonCode);
        // Household access must resolve the real service and retain its credential validation.
        // Missing tenant fails in the constructor before any SQL/Graph operation can occur.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => entitlements.EvaluateAsync(
            new BillingEntitlementEvaluationRequest(Guid.NewGuid(), BillingEntitlementKeys.ClientAppFullAccess, DateTime.UtcNow)));
        Assert.Contains("Missing required configuration: GraphProvisioning:TenantId", error.Message);
    }

    [Fact]
    public void BillingRegistrationPreservesExistingHouseholdAndLifecycleRegistrations()
    {
        IServiceCollection services = new ServiceCollection();
        var household = ServiceDescriptor.Scoped<IHouseholdMembershipService, HouseholdMembershipService>();
        var lifecycle = ServiceDescriptor.Scoped<IClientEntraLifecycleService, ClientEntraLifecycleService>();
        services.Add(household);
        services.Add(lifecycle);
        services.AddMasterAppBilling(new ConfigurationBuilder().Build());
        Assert.Same(household, Assert.Single(services.Where(x => x.ServiceType == typeof(IHouseholdMembershipService))));
        Assert.Same(lifecycle, Assert.Single(services.Where(x => x.ServiceType == typeof(IClientEntraLifecycleService))));
    }

}
