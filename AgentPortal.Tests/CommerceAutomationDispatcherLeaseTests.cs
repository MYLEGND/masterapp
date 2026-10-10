using System;
using System.Linq;
using System.Threading.Tasks;
using Legend.Commerce;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommerceAutomationDispatcherLeaseTests
{
    [Fact]
    public async Task InMemoryCommerceNeverGrantsCrossHostDispatcherLease()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var granted = await new SqlCommerceAutomationDispatchLease(db).TryAcquireAsync();
        Assert.Null(granted);
    }

    [Fact]
    public void OneExplicitWorkerIsRegisteredEvenWhenCalledTwice()
    {
        var services = new ServiceCollection();
        services.AddLegendCommerceAutomationWorker();
        services.AddLegendCommerceAutomationWorker();
        Assert.Single(services.Where(x => x.ServiceType == typeof(IHostedService) &&
            x.ImplementationType == typeof(ParfaitCustomerAutomationHostedService)));
        Assert.Single(services.Where(x => x.ServiceType == typeof(ICommerceAutomationDispatchLease)));
    }
}
