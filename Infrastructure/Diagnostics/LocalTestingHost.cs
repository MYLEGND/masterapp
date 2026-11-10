using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.Diagnostics;

/// <summary>Explicit local testing never loads the operator's production secrets or starts background sync/delivery.</summary>
public static class LocalTestingHost
{
    private const string HostedServicesKey = "Legend.LocalTesting.InitialHostedServices";

    public static void Configure(WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>("LocalTesting:Enabled")) return;
        if (!builder.Environment.IsDevelopment())
            throw new InvalidOperationException("LocalTesting requires Development and the persistent local test database.");
        foreach (var source in builder.Configuration.Sources.OfType<JsonConfigurationSource>()
                     .Where(source => Path.GetFileName(source.Path) == "secrets.json").ToArray())
            builder.Configuration.Sources.Remove(source);
        // Keep framework hosting; application delivery/sync jobs must not import real records into test data.
        ((IDictionary<object, object>)builder.Host.Properties)[HostedServicesKey] =
            builder.Services.Where(service => service.ServiceType == typeof(IHostedService)).ToHashSet();
    }

    public static void Prepare(WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>("LocalTesting:Enabled")) return;
        if (!builder.Host.Properties.TryGetValue(HostedServicesKey, out var value) ||
            value is not HashSet<ServiceDescriptor> initial)
            throw new InvalidOperationException("Local testing configuration was not initialized.");
        foreach (var service in builder.Services.Where(service => service.ServiceType == typeof(IHostedService)
                     && !initial.Contains(service)).ToArray())
            builder.Services.Remove(service);
    }
}
