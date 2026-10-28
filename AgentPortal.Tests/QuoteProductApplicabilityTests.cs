using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Protect_Website.Models;
using Xunit;

namespace AgentPortal.Tests;

public class QuoteProductApplicabilityTests
{
    [Theory]
    [InlineData("HO4 - Renters", true)]
    [InlineData("HO3 - Dwelling", false)]
    [InlineData("HO5", false)]
    [InlineData("HO6 - Condo", false)]
    [InlineData("", false)]
    [InlineData("unknown", false)]
    public void BuildingOwnerQuestions_OptionalOnlyForExplicitRenters(string product, bool optional)
    {
        var model = new HomeQuoteFormModel { PolicyFormType = product };
        foreach (var field in new[] { "PurchaseDate", "DwellingCoverage", "EstReplacementCost" })
        {
            Assert.Equal(optional, Validate(model, field, null));
            if (field != "PurchaseDate") Assert.Equal(optional, Validate(model, field, " "));
        }
        Assert.True(Validate(model, "PurchaseDate", new DateTime(2020, 1, 1)));
        Assert.True(Validate(model, "DwellingCoverage", "250000"));
        Assert.True(Validate(model, "EstReplacementCost", "300000"));
        Assert.False(Validate(model, "PersonalLiability", null));
        Assert.False(Validate(model, "PolicyFormType", null));
    }

    [Fact]
    public void ChangingProduct_RevalidatesWithoutDiscardingAnswers()
    {
        var model = new HomeQuoteFormModel { PolicyFormType = "HO4 - Renters", DwellingCoverage = "125000" };
        Assert.True(Validate(model, "PurchaseDate", null));
        model.PolicyFormType = "HO3 - Dwelling";
        Assert.False(Validate(model, "PurchaseDate", null));
        Assert.Equal("125000", model.DwellingCoverage);
        model.PolicyFormType = "HO4 - Renters";
        Assert.True(Validate(model, "PurchaseDate", null));
        Assert.Equal("125000", model.DwellingCoverage);
    }

    [Theory]
    [InlineData("Better Doctor Network", "Dental, vision & hearing provider access")]
    [InlineData("Prescription Coverage", "Dental, vision & hearing benefits")]
    [InlineData("Lower Deductible", "Lower Deductible")]
    [InlineData("", "Not provided")]
    public void DvhDisplay_PreservesAnswerIdentity(string value, string expected)
    {
        var model = new DentalVisionHearingQuoteFormModel { PrimaryConcern = value };
        Assert.Equal(expected, DentalVisionHearingQuoteFormModel.PrimaryConcernDisplay(model.PrimaryConcern));
        Assert.Equal(value, model.PrimaryConcern);
    }

    private static bool Validate(object model, string field, object? value) =>
        Validator.TryValidateProperty(value, new ValidationContext(model) { MemberName = field }, new List<ValidationResult>());
}
