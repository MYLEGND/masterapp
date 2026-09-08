using Azure.Security.KeyVault.Secrets;
using ProtectWebsite.Services.Configuration;
using Xunit;
using Infrastructure.DailyScripture;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPortal.Tests;

public class ProtectKeyVaultSecretManagerTests
{
    [Fact]
    public void DailyScriptureRegistrationResolvesInPublicWebsiteWithoutMessagingModule()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MasterAppDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        services.AddDailyScripture(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDailyScriptureManagementService>());
    }

    [Theory]
    [InlineData(true, "ConnectionStrings--MasterAppDb", false)]
    [InlineData(true, "connectionstrings--masterappdb", false)]
    [InlineData(false, "ConnectionStrings--MasterAppDb", true)]
    [InlineData(true, "AzureAd--ClientSecret", true)]
    public void LoadsOnlyEnvironmentAppropriateSecrets(bool development, string name, bool expected)
    {
        Assert.Equal(expected, new ProtectKeyVaultSecretManager(development).Load(new SecretProperties(name)));
    }
}
