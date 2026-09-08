using Azure.Security.KeyVault.Secrets;
using ProtectWebsite.Services.Configuration;
using Xunit;

namespace AgentPortal.Tests;

public class ProtectKeyVaultSecretManagerTests
{
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
