using System;
using System.IO;
using Infrastructure.Commerce.Payments;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

public sealed class MerchantPaymentProtectionTests
{
    [Fact]
    public void ManagementAndCheckoutShareOnlyPaymentPurpose()
    {
        var root = Path.Combine(Path.GetTempPath(), "merchant-protection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IHostEnvironment Host(string name) {
            var host = new Mock<IHostEnvironment>();
            host.SetupGet(x=>x.EnvironmentName).Returns("Development");
            host.SetupGet(x=>x.ContentRootPath).Returns(Path.Combine(root,name));
            return host.Object;
        }
        try
        {
            var config = new ConfigurationBuilder().Build();
            using var management = MerchantPaymentProtection.CreateShared(config,Host("Protect"));
            using var checkout = MerchantPaymentProtection.CreateShared(config,Host("Parfait"));
            var purposes = new[]{"Legend.Merchant.Credential.v1","connection-one","business-one","Square","Production"};
            var encrypted=management.Provider.CreateProtector(purposes).Protect("synthetic-credential");
            Assert.Equal("synthetic-credential",checkout.Provider.CreateProtector(purposes).Unprotect(encrypted));
            foreach(var (index, value) in new[]{(1,"connection-two"),(2,"business-two"),(3,"AnotherProvider"),(4,"Sandbox")})
            {
                var mismatchedPurposes = (string[])purposes.Clone();
                mismatchedPurposes[index] = value;
                Assert.Throws<System.Security.Cryptography.CryptographicException>(()=>checkout.Provider.CreateProtector(mismatchedPurposes).Unprotect(encrypted));
            }
            Assert.Throws<System.Security.Cryptography.CryptographicException>(()=>checkout.Provider.CreateProtector("Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationMiddleware").Unprotect(encrypted));
        }
        finally { Directory.Delete(root,true); }
    }
    [Fact]
    public void ProductionCannotSilentlyUseAnAppLocalRing()
    {
        var host = new Mock<IHostEnvironment>(); host.SetupGet(x=>x.EnvironmentName).Returns("Production");
        Assert.Throws<InvalidOperationException>(()=>MerchantPaymentProtection.CreateShared(new ConfigurationBuilder().Build(),host.Object));
    }
}
