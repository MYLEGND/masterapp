using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Protect_Website.Models;
using Xunit;

namespace AgentPortal.Tests;

public class QuoteProductInstrumentationContractTests_LicenseApplicability
{
    [Theory]
    [InlineData("Valid", false)]
    [InlineData("Permit", false)]
    [InlineData("Expired", false)]
    [InlineData("Suspended", false)]
    [InlineData("Revoked", false)]
    [InlineData("", false)]
    [InlineData("unknown", false)]
    [InlineData("not licensed", false)]
    [InlineData("Not Licensed", true)]
    public void LicenseDetails_RequireValuesUnlessExplicitlyUnlicensed(string status, bool optional)
    {
        var auto = new AutoQuoteFormModel { DLStatus = status };
        var home = new HomeQuoteFormModel { DLStatus = status };
        var driver = new Driver { DLStatus = status };
        foreach (var (model, field) in new (object, string)[] {
            (auto, nameof(auto.DriversLicenseNumber)), (auto, nameof(auto.DLState)),
            (home, nameof(home.DriversLicenseNumber)), (home, nameof(home.DLState)),
            (driver, nameof(driver.DLNumber)), (driver, nameof(driver.DLState)), (driver, nameof(driver.AgeLicensed)) })
        {
            foreach (var value in new string?[] { null, "", " " })
                Assert.Equal(optional, Validate(model, field, value));
            Assert.True(Validate(model, field, "retained answer"));
        }
        Assert.False(Validate(auto, nameof(auto.DLStatus), ""));
        Assert.False(Validate(home, nameof(home.DLStatus), ""));
        Assert.False(Validate(driver, nameof(driver.DLStatus), ""));
    }

    [Theory]
    [InlineData("0 years")]
    [InlineData("Not applicable")]
    [InlineData("1 year")]
    public void HomeHistory_PreservesTruthfulAnswer(string answer)
    {
        var model = new HomeQuoteFormModel { AutoYearsWithPriorCarrierOrAgent = answer };
        Assert.True(Validate(model, nameof(model.AutoYearsWithPriorCarrierOrAgent), answer));
        Assert.Equal(answer, model.AutoYearsWithPriorCarrierOrAgent);
        Assert.False(Validate(model, nameof(model.AutoYearsWithPriorCarrierOrAgent), ""));
    }

    private static bool Validate(object model, string field, object? value) =>
        Validator.TryValidateProperty(value, new ValidationContext(model) { MemberName = field }, new List<ValidationResult>());
}
