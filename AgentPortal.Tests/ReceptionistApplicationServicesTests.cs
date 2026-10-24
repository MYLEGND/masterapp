using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Leads;
using Infrastructure.Bookings;
using Infrastructure.Reception;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>Real deterministic state machine with a recording application boundary; canonical relational authorities have their own integration tests.</summary>
public sealed class ReceptionistApplicationServicesTests
{
    [Fact]
    public async Task ACompoundNeedKeepsItsExplicitServiceWhenTheLaterClauseAddsOnlyRequestDetails()
    {
        using var f = new SessionFixture();
        var read = Text(await f.PrepareAsync("I need a website and I want to explain my services."));
        Assert.Contains("Service: Website services", read); Assert.DoesNotContain("Which service", read);
    }

    [Fact]
    public async Task InformationalDetourDoesNotReplaceTheSelectedServiceOrConfirmedNeed()
    {
        using var f = new SessionFixture(); await f.PrepareAsync();
        f.Input("I want to know how much coaching costs?", 300, 400); await f.Session.HandleDelegationAsync("information");
        f.Input("Please read back my request.", 500, 600);
        var read = Text(await f.Session.HandleDelegationAsync("preserved"));
        Assert.Contains("Service: Website services", read); Assert.DoesNotContain("how much coaching costs", read);
        f.Input("I want coaching instead.", 700, 800); var switched = Text(await f.Session.HandleDelegationAsync("actual-switch"));
        Assert.DoesNotContain("Service: Website services", switched);
        f.Input("I need help with a coaching plan.", 900, 1000);
        Assert.Contains("Service: Coaching", Text(await f.Session.HandleDelegationAsync("new-need")));
    }

    [Fact]
    public async Task WholeNameReplacementCannotReuseAnEarlierSurname()
    {
        using var f = new SessionFixture(); await f.PrepareAsync();
        f.Input("My first name is Ada.", 300, 400); await f.Session.HandleDelegationAsync("first");
        f.Input("My last name is Lovelace.", 500, 600); await f.Session.HandleDelegationAsync("last");
        f.Input("Actually, my name is Grace.", 700, 800);
        var read = Text(await f.Session.HandleDelegationAsync("whole-name"));
        Assert.Contains("Grace", read); Assert.DoesNotContain("Lovelace", read); Assert.DoesNotContain("Ada", read);
    }
    [Fact]
    public async Task CorrectedNameHoldSurvivesAnUnrelatedQuestionAndCannotSaveTheOldName()
    {
        using var f = new SessionFixture(); await f.PrepareAsync();
        f.Input("My name is Jordan Riley.", 300, 400); await f.Session.HandleDelegationAsync("first-name");
        f.Input("Actually, my last name is Reilly.", 500, 600); await f.Session.HandleDelegationAsync("correction");
        f.Input("What do you offer?", 700, 800); await f.Session.HandleDelegationAsync("detour");
        f.Input("Please save my request.", 900, 1000);
        Assert.Contains("complete name", Text(await f.Session.HandleDelegationAsync("must-not-save")));
        Assert.Equal(0, f.Actions.Writes);
    }

    [Fact]
    public async Task LongerProtectionBrandIsNotMisclassifiedAsTheLegendBrand()
    {
        var config = Configuration() with { ServiceDefinitionsJson = JsonSerializer.Serialize(new[]
        {
            new { serviceKey = "website_services", displayName = "LEGEND", aliases = new[] { "LEGEND", "website" }, discoveryQuestion = "What is your website goal?", publicSummary = "Synthetic website information." },
            new { serviceKey = "coaching", displayName = "LEGEND Legacy Protection", aliases = new[] { "LEGEND Legacy Protection", "protection" }, discoveryQuestion = "What protection information do you need?", publicSummary = "Synthetic protection information." }
        }) };
        using var f = new SessionFixture(config);
        var read = Text(await f.PrepareAsync("I want LEGEND Legacy Protection to discuss protection options."));
        Assert.Contains("Service: LEGEND Legacy Protection", read); Assert.DoesNotContain("Which service", read);
        f.Input("We also discussed LEGEND earlier.", 300, 400); await f.Session.HandleDelegationAsync("brand-detour");
        f.Input("Please read back my request.", 500, 600);
        Assert.Contains("Service: LEGEND Legacy Protection", Text(await f.Session.HandleDelegationAsync("brand-current")));
    }

    [Fact]
    public async Task SyntheticIntakeAndRestartWireNeverClaimsARealLeadOrReturnsStoredFacts()
    {
        using var f = new SessionFixture(controlledTest: true);
        await f.PrepareAsync();
        f.Input("My name is Privacycanary Person.", 300, 400); await f.Session.HandleDelegationAsync("qa-name");
        f.Input("My email is private-canary@example.invalid.", 500, 600);
        var summary = await f.Session.HandleDelegationAsync("qa-email");
        f.Acknowledge(summary, 700); f.Input("Please save my request.", 800, 900);
        var saved = await f.Session.HandleDelegationAsync("qa-save");
        var wire = Wire(saved);
        Assert.Contains("synthetic", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Privacycanary", wire); Assert.DoesNotContain("private-canary", wire);
        Assert.DoesNotContain(f.Actions.ActionId.ToString(), wire);
        Assert.DoesNotContain("website-lead:", wire); Assert.DoesNotContain("qa-outcome:", wire);
        f.Actions.Prior.Add(new(f.Actions.ActionId, "verified"));
        using var restarted = new ReceptionistApplicationSession(f.Call, Guid.NewGuid(),
            ReceptionistApplicationCatalog.Read(f.Call.Configuration), f.Actions, TimeProvider.System);
        var restartWire = Wire(await restarted.InitializeAsync());
        Assert.Contains("synthetic", restartWire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Privacycanary", restartWire); Assert.DoesNotContain("private-canary", restartWire);
        Assert.DoesNotContain(f.Actions.ActionId.ToString(), restartWire);
        var diagnostic = JsonSerializer.Serialize(f.Session.Diagnostic());
        Assert.DoesNotContain("Privacycanary", diagnostic); Assert.DoesNotContain("private-canary", diagnostic);
        Assert.DoesNotContain("explain my services", diagnostic);
        Assert.True(f.Session.Diagnostic().Synthetic); Assert.True(f.Session.Diagnostic().IntakeVerified);
        Assert.Equal(1, f.Actions.Writes);
    }

    [Fact]
    public async Task AQuestionAboutBookingIsNotConsent()
    {
        using var f = new SessionFixture(BookingConfiguration(), controlledTest: true, booking: new FakeBooking());
        await SaveBookingIntakeAsync(f);
        f.Input("Please book an appointment tomorrow.", 1000, 1100); await f.Session.HandleDelegationAsync("offer");
        f.Input("Option one.", 1200, 1300); var confirmation = await f.Session.HandleDelegationAsync("slot");
        f.Acknowledge(confirmation, 1400);
        f.Input("Book that appointment and send the email invitation?", 1500, 1600);
        await f.Session.HandleDelegationAsync("question"); Assert.Equal(0, f.Booking!.Commits);
    }

    [Fact]
    public async Task QaOnlyBookingIsNotAdvertisedToAPublicCall()
    {
        var config = BookingConfiguration();
        using var f = new SessionFixture(config with { BookingPolicy = config.BookingPolicy! with { QaOnly = true } }, booking: new FakeBooking());
        await SaveBookingIntakeAsync(f);
        f.Input("Please book an appointment tomorrow.", 1000, 1100);
        Assert.Contains("can't", Text(await f.Session.HandleDelegationAsync("public-qa-only")));
        Assert.Equal(0, f.Booking!.Preparations); Assert.Equal(0, f.Booking.Commits);
        Assert.False(ReceptionistConfigurationCodec.BookingEnabled(f.Call.Configuration, f.Call.Binding, false));
        Assert.True(ReceptionistConfigurationCodec.BookingEnabled(f.Call.Configuration, f.Call.Binding, true));
    }

    [Fact]
    public async Task AnUnmappedServiceNeverCollectsAppointmentContactDetailsOrThrows()
    {
        using var f = new SessionFixture(BookingConfiguration(), booking: new FakeBooking());
        await f.PrepareAsync("I need coaching to understand my options.");
        f.Input("Please book an appointment tomorrow.", 300, 400);
        var result = Text(await f.Session.HandleDelegationAsync("unmapped-service"));
        Assert.Contains("can't book", result);
        Assert.DoesNotContain("What name", result); Assert.DoesNotContain("What email", result);
        Assert.Equal(0, f.Booking!.Preparations); Assert.Equal(0, f.Actions.Writes);
    }

    [Theory]
    [InlineData("Book that appointment and send the email invitation.")]
    [InlineData("Yes, please book it and email me the invitation.")]
    [InlineData("Please book that appointment and send me an email invitation.")]
    public async Task BookingRequiresFreshSlotAcknowledgementAndExplicitInvitationConsent(string explicitConsent)
    {
        using var f = new SessionFixture(BookingConfiguration(), controlledTest: true, booking: new FakeBooking());
        await SaveBookingIntakeAsync(f);
        f.Input("Please book an appointment tomorrow.", 1000, 1100);
        var offered = await f.Session.HandleDelegationAsync("offer");
        Assert.Contains("synthetic test options", Text(offered));
        f.Input("Option one.", 1200, 1300); var confirmation = await f.Session.HandleDelegationAsync("slot");
        f.Acknowledge(confirmation, 1400); f.Input("Yes.", 1500, 1600);
        var repeat = await f.Session.HandleDelegationAsync("bare-yes"); Assert.Equal(0, f.Booking!.Commits);
        f.Acknowledge(repeat, 1700); f.Input(explicitConsent, 1800, 1900);
        var result = Wire(await f.Session.HandleDelegationAsync("explicit-consent"));
        Assert.Equal(1, f.Booking.Commits); Assert.Contains("synthetic booking outcome", result);
        Assert.DoesNotContain("Your appointment is booked", result);
        Assert.DoesNotContain("private-canary", result); Assert.DoesNotContain("opaque-option", result);
    }

    [Theory]
    [InlineData("Actually, my email is corrected@example.invalid.")]
    [InlineData("Actually, my name is Different Person.")]
    [InlineData("Actually, use replacement@example.invalid instead.")]
    public async Task SavedContactCorrectionPermanentlyBlocksUsingTheOldInvitationRecipient(string correction)
    {
        using var f = new SessionFixture(BookingConfiguration(), booking: new FakeBooking());
        await SaveBookingIntakeAsync(f);
        f.Input("Please book an appointment tomorrow.", 1000, 1100); await f.Session.HandleDelegationAsync("offer");
        Assert.Equal(1, f.Booking!.Preparations);
        f.Input(correction, 1200, 1300); await f.Session.HandleDelegationAsync("correct-contact");
        f.Input("Please book an appointment tomorrow.", 1400, 1500); await f.Session.HandleDelegationAsync("new-date");
        f.Input("Book that appointment and send the email invitation.", 1600, 1700); await f.Session.HandleDelegationAsync("old-recipient-consent");
        Assert.Equal(1, f.Booking.Preparations); Assert.Equal(0, f.Booking.Commits);
    }

    [Fact]
    public async Task NegatedOrAmbiguousBookingDatesNeedClarificationWithoutAnAvailabilityLookup()
    {
        using var f = new SessionFixture(BookingConfiguration(), booking: new FakeBooking());
        await SaveBookingIntakeAsync(f);
        f.Input("Please book an appointment, not tomorrow, today.", 1000, 1100);
        var read = Text(await f.Session.HandleDelegationAsync("ambiguous-date"));
        Assert.Contains("What day", read); Assert.Equal(0, f.Booking!.Preparations);
    }

    private static string Wire(IEnumerable<ReceptionistApplicationUpdate> values) => string.Join("\n",
        values.Select(x => Encoding.UTF8.GetString(ReceptionistSidebandProtocol.Append(x.EventId, x.DelegationId, x.Type, x.Content))));
    private static async Task SaveBookingIntakeAsync(SessionFixture f)
    {
        await f.PrepareAsync();
        f.Input("My name is Privacycanary Person.", 300, 400); await f.Session.HandleDelegationAsync("booking-name");
        f.Input("My email is private-canary@example.invalid.", 500, 600);
        var summary = await f.Session.HandleDelegationAsync("booking-email");
        f.Acknowledge(summary, 700); f.Input("Please save my request.", 800, 900);
        await f.Session.HandleDelegationAsync("booking-save"); Assert.Equal(1, f.Actions.Writes);
    }
    private static ReceptionistBusinessConfiguration BookingConfiguration() => Configuration() with
    {
        RequestedCapabilities = [ReceptionistCapabilities.CanonicalIntake, ReceptionistCapabilities.CanonicalBooking],
        BookingPolicy = new() { Enabled = true, QaOnly = false, AllowConsentedCustomerEmail = true,
            Services = [new() { ServiceKey = "website_services", BookingServiceId = "synthetic-service", TimeZoneId = "UTC", DurationMinutes = 30 }] }
    };
    private sealed class FakeBooking : IReceptionistApplicationBooking
    {
        internal int Preparations, Commits;
        public Task<IReadOnlyList<ReceptionistBookingOption>> PrepareAsync(ReceptionistCallContext call, Guid intake, string service, DateOnly date, Guid lease, CancellationToken ct)
        {
            Preparations++;
            var start = DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(12, 0)), DateTimeKind.Utc);
            return Task.FromResult<IReadOnlyList<ReceptionistBookingOption>>([new("opaque-option", start, start.AddMinutes(30), "UTC", "Website services", call.ControlledTest)]);
        }
        public Task<ReceptionistBookingResult> CommitAsync(ReceptionistCallContext call, string handle, bool appointment, bool email, string receipt, Guid lease, CancellationToken ct)
        { Assert.True(appointment); Assert.True(email); Commits++; return Task.FromResult(new ReceptionistBookingResult(call.ControlledTest ? "qa_booking_outcome_verified" : "booking_verified", true, call.ControlledTest)); }
    }
    [Theory]
    [InlineData("We discussed coaching earlier.")]
    [InlineData("Thanks for explaining coaching.")]
    public async Task HistoricalSingleServiceMentionDoesNotReplaceTheChosenRequest(string detour)
    {
        using var f = new SessionFixture(); await f.PrepareAsync();
        f.Input(detour, 300, 400); await f.Session.HandleDelegationAsync("historical-service");
        f.Input("Please read back my request.", 500, 600);
        var read = Text(await f.Session.HandleDelegationAsync("current-request"));
        Assert.Contains("Service: Website services", read); Assert.Contains("explain my services", read);
    }

    [Theory]
    [InlineData("My name is not Jordan Riley.")]
    [InlineData("My name is Jordan Riley but call me Jay.")]
    public async Task AmbiguousNameStatementRequiresCompleteNameBeforeConfirmation(string statement)
    {
        using var f = new SessionFixture(); await f.PrepareAsync();
        f.Input(statement, 300, 400);
        Assert.Contains("complete corrected name", Text(await f.Session.HandleDelegationAsync("uncertain-name")));
        Assert.Equal(0, f.Actions.Writes);
        f.Input("My name is Jordan Reilly.", 500, 600);
        Assert.Contains("Name as provided: Jordan Reilly.", Text(await f.Session.HandleDelegationAsync("clear-name")));
    }

    [Fact]
    public async Task PartialCorrectionOfUnsplitNameRequiresTheCompleteCorrectedName()
    {
        using var f = new SessionFixture(); await f.PrepareAsync();
        f.Input("My name is Jordan Riley.", 300, 400); await f.Session.HandleDelegationAsync("original-name");
        f.Input("Actually, my last name is Reilly.", 500, 600);
        Assert.Contains("complete corrected name", Text(await f.Session.HandleDelegationAsync("partial-name-correction")));
        f.Input("My name is Jordan Reilly.", 700, 800);
        var read = await f.Session.HandleDelegationAsync("full-name-correction");
        Assert.Contains("Name as provided: Jordan Reilly.", Text(read));
        f.Acknowledge(read, 900); f.Input("Please save my request.", 1000, 1100);
        await f.Session.HandleDelegationAsync("save-corrected-name");
        Assert.Equal("Jordan Reilly", f.Actions.WrittenFacts!.CallerName);
        Assert.Null(f.Actions.WrittenFacts.FirstName); Assert.Null(f.Actions.WrittenFacts.LastName);
    }

    [Fact]
    public async Task PreviouslyChosenServiceSurvivesMentionOfBothServicesWithoutRepeatedQualification()
    {
        using var f = new SessionFixture();
        await f.PrepareAsync();
        f.Input("We discussed website and coaching earlier.", 300, 400);
        var result = Text(await f.Session.HandleDelegationAsync("comparison-detour"));
        Assert.DoesNotContain("Which service", result);
        f.Input("Please read back my request.", 500, 600);
        var summary = Text(await f.Session.HandleDelegationAsync("resume-selected-request"));
        Assert.Contains("Service: Website services", summary);
        Assert.Contains("explain my services", summary);
        Assert.Equal(0, f.Actions.Writes);
    }

    [Fact]
    public async Task CompleteCallerProvidedNameIsPreservedWithoutInventingANameSplit()
    {
        using var f = new SessionFixture();
        await f.PrepareAsync();
        f.Input("My name is Jordan Riley.", 300, 400);
        var summary = Text(await f.Session.HandleDelegationAsync("provided-full-name"));
        Assert.Contains("Name as provided: Jordan Riley.", summary);
        Assert.DoesNotContain("Which part", summary);
        f.Input("What do you offer?", 500, 600);
        await f.Session.HandleDelegationAsync("name-detour");
        f.Input("Please read back my request.", 700, 800);
        Assert.Contains("Name as provided: Jordan Riley.", Text(await f.Session.HandleDelegationAsync("name-retained")));
        Assert.Equal(0, f.Actions.Writes);
    }

    [Fact]
    public async Task NaturalExplicitSaveSentenceUsesOnlyTheAcknowledgedCurrentSummary()
    {
        using var f = new SessionFixture();
        var summary = await f.PrepareAsync();
        f.Acknowledge(summary, 300);
        f.Input("Yes, please save my request.", 400, 500);
        await f.Session.HandleDelegationAsync("natural-explicit-confirmation");
        Assert.Equal(1, f.Actions.Writes);
        Assert.Contains("explain my services", f.Actions.WrittenFacts!.RequestSummary);
    }

    [Fact]
    public async Task NaturalServiceChoiceResumesPendingApprovedQuestion()
    {
        var config = Configuration() with { ApprovedKnowledgeJson = JsonSerializer.Serialize(new { answers = new[]
        {
            new { key = "website_price", serviceKey = "website_services", questions = new[] { "How much is it?" }, answer = "Synthetic website answer.", status = "approved" },
            new { key = "coaching_price", serviceKey = "coaching", questions = new[] { "How much is it?" }, answer = "Synthetic coaching answer.", status = "approved" }
        } }) };
        using var f = new SessionFixture(config);
        await f.Session.InitializeAsync();
        f.Input("How much is it?", 100, 200);
        Assert.Contains("Which service", Text(await f.Session.HandleDelegationAsync("question")));
        f.Input("I'd like the website service, please.", 300, 400);
        var answer = Text(await f.Session.HandleDelegationAsync("choice"));
        Assert.Contains("Synthetic website answer.", answer);
        Assert.DoesNotContain("Synthetic coaching answer.", answer);
        Assert.Equal(0, f.Actions.Writes);
    }

    [Theory]
    [InlineData("Not coaching, the website service please.")]
    [InlineData("We discussed coaching and websites, but I want the website service.")]
    public async Task ExplicitServiceChoiceWinsOverOtherServiceMentions(string choice)
    {
        using var f = new SessionFixture();
        await f.PrepareAsync();
        f.Input(choice, 300, 400);
        var result = Text(await f.Session.HandleDelegationAsync("explicit-choice"));
        Assert.DoesNotContain("Which service", result);
        Assert.Contains("Service: Website services", result);
        Assert.Contains("explain my services", result);
        Assert.Equal(0, f.Actions.Writes);
    }

    [Theory]
    [InlineData("My name is Jordan.")]
    [InlineData("I'm Jordan.")]
    public async Task OrdinaryNameIntroductionSurvivesFollowupAndConfirmedIntake(string introduction)
    {
        using var f = new SessionFixture();
        await f.PrepareAsync();
        f.Input(introduction, 300, 400);
        var named = await f.Session.HandleDelegationAsync("name");
        if (introduction.StartsWith("I'm", StringComparison.Ordinal))
        {
            Assert.Contains("won't infer a name", Text(named));
            f.Input("My name is Jordan.", 410, 450);
            named = await f.Session.HandleDelegationAsync("explicit-name");
        }
        Assert.Contains("Jordan", Text(named));
        f.Input("What do you offer?", 500, 600);
        await f.Session.HandleDelegationAsync("follow-up");
        f.Input("Please read back my request.", 700, 800);
        var summary = await f.Session.HandleDelegationAsync("readback");
        Assert.Contains("Jordan", Text(summary));
        f.Acknowledge(summary, 1000);
        f.Input("Please save the request you just read back.", 1100, 1300);
        await f.Session.HandleDelegationAsync("save-name");
        Assert.Equal("Jordan", f.Actions.WrittenFacts!.FirstName);
        Assert.Null(f.Actions.WrittenFacts.LastName);
        Assert.Equal(1, f.Actions.Writes);
    }

    [Fact]
    public async Task NameAndServiceInOneUtteranceAreBothCaptured()
    {
        using var f = new SessionFixture();
        var summary = await f.PrepareAsync("My name is Jordan, and I need a website to explain my services.");
        Assert.Contains("Jordan", Text(summary));
        Assert.Contains("Service: Website services", Text(summary));
        Assert.Contains("I need a website to explain my services", Text(summary));
        Assert.Equal(0, f.Actions.Claims);
    }

    [Fact]
    public async Task NameFragmentRequiresExplicitRestatementAndSpellingCorrectionIsReadBack()
    {
        using var f = new SessionFixture(); await f.PrepareAsync();
        f.Input("My first name is Jo", 300, 400);
        Assert.Contains("confirm your complete name", Text(await f.Session.HandleDelegationAsync("fragment")));
        f.Input("rdan", 500, 600);
        Assert.Contains("complete name again", Text(await f.Session.HandleDelegationAsync("continuation")));
        f.Input("Actually, my first name is Jordan.", 700, 800);
        await f.Session.HandleDelegationAsync("restated");
        f.Input("It's J-o-r-d-y-n.", 900, 1000);
        var summary = await f.Session.HandleDelegationAsync("spelling");
        Assert.Contains("First name: Jordyn.", Text(summary));
        f.Acknowledge(summary, 1200);
        f.Input("Please save the request you just read back.", 1300, 1400);
        await f.Session.HandleDelegationAsync("save");
        Assert.Equal("Jordyn", f.Actions.WrittenFacts!.FirstName);
        Assert.Equal(1, f.Actions.Writes);
    }

    [Theory]
    [InlineData("I'm interested in coaching.")]
    [InlineData("I'm looking for website services.")]
    public async Task NonNameStatementsNeverBecomeCallerNames(string statement)
    {
        using var f = new SessionFixture(); await f.PrepareAsync();
        f.Input(statement, 300, 400);
        var result = Text(await f.Session.HandleDelegationAsync("not-name"));
        Assert.DoesNotContain("First name:", result);
        Assert.Equal(0, f.Actions.Writes);
    }

    [Fact]
    public async Task InformationAboutAnotherServiceDoesNotChangeTheSelectedInquiry()
    {
        using var f = new SessionFixture(); await f.PrepareAsync();
        f.Input("What about coaching?", 300, 400);
        await f.Session.HandleDelegationAsync("other-question");
        f.Input("Please read back my request.", 500, 600);
        var result = Text(await f.Session.HandleDelegationAsync("readback"));
        Assert.Contains("Service: Website services", result);
        Assert.Contains("explain my services", result);
        Assert.Equal(0, f.Actions.Writes);
    }

    [Fact]
    public async Task ExplicitServiceSwitchDropsOnlyTheOldServiceNeed()
    {
        using var f = new SessionFixture(); await f.PrepareAsync("My name is Jordan, and I need a website to explain my services.");
        f.Input("Actually, switch to coaching.", 300, 400);
        var result = Text(await f.Session.HandleDelegationAsync("switch"));
        Assert.DoesNotContain("explain my services", result);
        f.Input("I need help setting personal goals.", 500, 600);
        var summary = Text(await f.Session.HandleDelegationAsync("new-need"));
        Assert.Contains("Service: Coaching", summary);
        Assert.Contains("Jordan", summary);
        Assert.DoesNotContain("explain my services", summary);
    }

    [Fact]
    public async Task DistinctOwnerCallsKeepNamesNeedsAndDelegationsIsolated()
    {
        using var first = new SessionFixture(owner: MarketingOwnerScope.Business(Guid.NewGuid()));
        using var second = new SessionFixture(owner: MarketingOwnerScope.Business(Guid.NewGuid()));
        await first.PrepareAsync("My name is Jordan, and I need a website to explain my services.");
        var other = Text(await second.PrepareAsync("My name is Casey, and I need coaching for personal goals."));
        Assert.DoesNotContain("Jordan", other);
        Assert.Contains("Casey", other);
        first.Input("Please read back my request.", 300, 400);
        var original = Text(await first.Session.HandleDelegationAsync("same-delegation"));
        second.Input("Please read back my request.", 300, 400);
        var independent = Text(await second.Session.HandleDelegationAsync("same-delegation"));
        Assert.Contains("Jordan", original);
        Assert.DoesNotContain("Casey", original);
        Assert.Contains("Casey", independent);
        Assert.Empty(await first.Session.HandleDelegationAsync("same-delegation"));
        Assert.Equal(0, first.Actions.Writes + second.Actions.Writes);
    }

    [Theory]
    [InlineData("I do not want coaching.")]
    [InlineData("Not coaching please.")]
    [InlineData("I do not want coaching, please.")]
    public async Task NegatedServiceCannotReplaceAnExplicitChoice(string statement)
    {
        using var f = new SessionFixture(); await f.PrepareAsync();
        f.Input(statement, 300, 400);
        await f.Session.HandleDelegationAsync("negation");
        f.Input("Please read back my request.", 500, 600);
        var result = Text(await f.Session.HandleDelegationAsync("readback"));
        Assert.Contains("Service: Website services", result);
        Assert.Contains("explain my services", result);
    }

    [Theory]
    [InlineData("I do not want website.")]
    [InlineData("Not website please.")]
    [InlineData("Not website, please.")]
    [InlineData("I do not want website, please.")]
    [InlineData("I want neither.")]
    public async Task RejectingTheCurrentServiceRequiresANewChoiceBeforeSaving(string statement)
    {
        using var f = new SessionFixture(); await f.PrepareAsync();
        f.Input(statement, 300, 400);
        Assert.Contains("Which service", Text(await f.Session.HandleDelegationAsync("reject-current")));
        f.Input("Please save my request.", 500, 600);
        Assert.Contains("choose the service", Text(await f.Session.HandleDelegationAsync("cannot-save")), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, f.Actions.Claims);
    }

    [Fact]
    public async Task AmbiguousFinalChoiceCannotFallBackToAnEarlierChoice()
    {
        using var f = new SessionFixture(); await f.Session.InitializeAsync();
        f.Input("I want website. Actually I want website and coaching.", 100, 200);
        Assert.Contains("Which service", Text(await f.Session.HandleDelegationAsync("ambiguous-final")));
        Assert.Equal(0, f.Actions.Claims);
    }

    [Theory]
    [InlineData("I'm married.")]
    [InlineData("I am happy.")]
    [InlineData("This is confusing.")]
    public async Task SelfDescriptionsRequireNameClarificationRatherThanBecomingNames(string statement)
    {
        using var f = new SessionFixture(); await f.PrepareAsync();
        f.Input(statement, 300, 400);
        Assert.Contains("won't infer a name", Text(await f.Session.HandleDelegationAsync("description")));
        f.Input("Please read back my request.", 500, 600);
        Assert.DoesNotContain("First name:", Text(await f.Session.HandleDelegationAsync("readback")));
    }

    [Fact]
    public void ParaphraseNormalizationRejectsConflictingApprovedAnswersAtConfigurationValidation()
    {
        var config = Configuration() with { ApprovedKnowledgeJson = JsonSerializer.Serialize(new { answers = new[]
        {
            new { key = "one", questions = new[] { "How much is it?" }, answer = "Synthetic first answer.", status = "approved" },
            new { key = "two", questions = new[] { "Please tell me how much is it?" }, answer = "Synthetic conflicting answer.", status = "approved" }
        } }) };
        Assert.Throws<ArgumentException>(() => ReceptionistApplicationServices.ValidateConfiguration(config));
    }

    [Fact]
    public async Task RemovedNameCannotBeResurrectedByUnscopedSpelling()
    {
        using var f = new SessionFixture(); await f.PrepareAsync("My name is Jordan, and I need a website to explain my services.");
        f.Input("Please remove my name.", 300, 400);
        await f.Session.HandleDelegationAsync("remove");
        f.Input("It's A-B-C.", 500, 600);
        await f.Session.HandleDelegationAsync("unscoped-spelling");
        f.Input("Please read back my request.", 700, 800);
        Assert.DoesNotContain("First name:", Text(await f.Session.HandleDelegationAsync("readback")));
    }

    [Fact]
    public async Task SpellingMarkerIsNeverPartOfTheRecordedName()
    {
        using var f = new SessionFixture(); await f.PrepareAsync();
        f.Input("My first name is spelled Jordan.", 300, 400);
        var summary = Text(await f.Session.HandleDelegationAsync("spelling-word"));
        Assert.Contains("First name: Jordan.", summary);
        Assert.DoesNotContain("First name: spelled", summary);
    }

    [Theory]
    [InlineData("My first name is Mary Ann.", "First name: Mary Ann.")]
    [InlineData("My last name is Smith-Jones.", "Last name: Smith-Jones.")]
    public async Task OrdinaryNamesPreserveTheirSpacesAndHyphens(string input, string expected)
    {
        using var f = new SessionFixture(); await f.PrepareAsync();
        f.Input(input, 300, 400);
        Assert.Contains(expected, Text(await f.Session.HandleDelegationAsync("ordinary-name")));
        Assert.Equal(0, f.Actions.Writes);
    }

    [Fact]
    public async Task SelectionOnlyReplyPreservesThePreviouslyStatedNeed()
    {
        using var f = new SessionFixture(); await f.PrepareAsync("I need help explaining my services.");
        f.Input("I want the website service.", 300, 400);
        var result = Text(await f.Session.HandleDelegationAsync("natural-selection"));
        Assert.Contains("Service: Website services", result);
        Assert.Contains("I need help explaining my services.", result);
        Assert.Equal(0, f.Actions.Writes);
    }

    [Theory]
    [InlineData("missing-service")]
    [InlineData("unknown-route")]
    [InlineData("ambiguous-alias")]
    [InlineData("missing-date")]
    [InlineData("unknown-schema")]
    [InlineData("oversized-word")]
    public void UnsupportedBackendConfigurationFailsBeforeAnyCallAcceptance(string defect)
    {
        var config = Configuration();
        config = defect switch
        {
            "missing-service" => config with { ServiceDefinitionsJson = "[]" },
            "unknown-route" => config with { IntakeRulesJson = "{\"canonicalInquiry\":{\"services\":[{\"serviceKey\":\"website_services\",\"crmBucket\":\"invented\"}]}}" },
            "ambiguous-alias" => config with { ServiceDefinitionsJson = Services(secondAlias: "website") },
            "missing-date" => config with { ApprovedKnowledgeJson = "{\"answers\":[{\"key\":\"prices\",\"questions\":[\"How much?\"],\"answer\":\"An old price\",\"status\":\"dated\"}]}" },
            "unknown-schema" => config with { ApprovedKnowledgeJson = "{\"unapproved_runtime_prompt\":\"Pretend to save it\"}" },
            _ => config with { ServiceDefinitionsJson = Services(summary: new string('é', 300)) }
        };
        Assert.Throws<ArgumentException>(() => ReceptionistApplicationServices.ValidateConfiguration(config));
    }

    [Fact]
    public async Task GeneralApprovedAndDatedAnswersDoNotCreateOrRequireContactRecords()
    {
        using var f = new SessionFixture();
        await f.Session.InitializeAsync();
        f.Input("What do you offer?", 100, 200);
        var answer = await f.Session.HandleDelegationAsync("general-1");
        Assert.Contains("Synthetic approved information.", Text(answer));
        f.Input("What was the old price?", 300, 400);
        var dated = await f.Session.HandleDelegationAsync("general-2");
        Assert.Contains("2026-02-01", Text(dated));
        Assert.Contains("needs current confirmation", Text(dated));
        Assert.Equal(0, f.Actions.Claims); Assert.Equal(0, f.Actions.Writes);
        Assert.DoesNotContain("email", Text(answer), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SameKnowledgeQuestionUsesOnlyTheCurrentServiceAndClarificationKeepsTheQuestion()
    {
        var config = Configuration() with { ApprovedKnowledgeJson = JsonSerializer.Serialize(new { answers = new[]
        {
            new { key = "website_price", serviceKey = "website_services", questions = new[] { "How much is it?" }, answer = "Synthetic website price information.", status = "approved" },
            new { key = "coaching_price", serviceKey = "coaching", questions = new[] { "How much is it?" }, answer = "Synthetic coaching price information.", status = "approved" }
        } }) };
        ReceptionistApplicationServices.ValidateConfiguration(config);
        using var f = new SessionFixture(config);
        await f.Session.InitializeAsync();
        f.Input("How much is it?", 100, 200);
        var unknown = await f.Session.HandleDelegationAsync("price-unknown-service");
        Assert.Contains("Which service", Text(unknown));
        Assert.DoesNotContain("price information", Text(unknown));
        f.Input("Coaching.", 300, 400);
        var coaching = await f.Session.HandleDelegationAsync("price-coaching");
        Assert.Contains("coaching price information", Text(coaching));
        Assert.DoesNotContain("website price information", Text(coaching));
        f.Input("I need a website to explain my services.", 500, 600);
        await f.Session.HandleDelegationAsync("change-service");
        f.Input("How much is it?", 700, 800);
        var website = await f.Session.HandleDelegationAsync("price-website");
        Assert.Contains("website price information", Text(website));
        Assert.DoesNotContain("coaching price information", Text(website));
        Assert.Equal(0, f.Actions.Claims);
    }

    [Fact]
    public async Task ServiceSpecificAnswerIsNotReturnedForAnotherServiceWithoutItsOwnApprovedAnswer()
    {
        var config = Configuration() with { ApprovedKnowledgeJson = "{\"answers\":[{\"key\":\"website_only\",\"serviceKey\":\"website_services\",\"questions\":[\"How much is it?\"],\"answer\":\"Synthetic website-only price.\",\"status\":\"approved\"}]}" };
        using var f = new SessionFixture(config);
        await f.Session.InitializeAsync();
        f.Input("I need coaching to work on my goals.", 100, 200);
        await f.Session.HandleDelegationAsync("coaching-request");
        f.Input("How much is it?", 300, 400);
        var response = await f.Session.HandleDelegationAsync("wrong-price-scope");
        Assert.Contains("don't have an approved answer", Text(response));
        Assert.DoesNotContain("website-only price", Text(response));
        f.Input("Website.", 500, 600);
        Assert.Contains("website-only price", Text(await f.Session.HandleDelegationAsync("explicit-website-question")));
        f.Input("My email is caller@example.invalid.", 700, 800);
        var next = await f.Session.HandleDelegationAsync("new-service-needs-new-request");
        Assert.Contains("What would you like the website to help with", Text(next));
        Assert.DoesNotContain("work on my goals", Text(next));
        Assert.Equal(0, f.Actions.Claims);
    }

    [Fact]
    public async Task AskingToSaveAfterAnInformationQuestionPreservesTheActualNeedAndRequiresNewReadback()
    {
        using var f = new SessionFixture();
        await f.PrepareAsync();
        f.Input("What do you offer?", 300, 400);
        await f.Session.HandleDelegationAsync("information-between-request-and-save");
        f.Input("I want to save this request.", 500, 600);
        var readback = await f.Session.HandleDelegationAsync("resume-request");
        Assert.Contains("I need a website to explain my services", Text(readback));
        Assert.DoesNotContain("Request: I want to save", Text(readback));
        Assert.Equal(0, f.Actions.Claims);
        f.Acknowledge(readback, 800);
        f.Input("Please save the request you just read back.", 900, 1200);
        await f.Session.HandleDelegationAsync("confirm-resumed-request");
        Assert.Equal(1, f.Actions.Writes);
        Assert.Contains("explain my services", f.Actions.WrittenFacts!.RequestSummary);
    }

    [Theory]
    [InlineData("I need help explaining my services.")]
    [InlineData("I need a website for my coaching business.")]
    public async Task ChoosingAServicePreservesTheNeedAlreadyStatedWithoutAssumingItsRoute(string need)
    {
        using var f = new SessionFixture();
        await f.Session.InitializeAsync();
        f.Input(need, 100, 200);
        var clarification = await f.Session.HandleDelegationAsync("need-with-unresolved-service");
        Assert.Contains("Which service", Text(clarification));
        Assert.Equal(0, f.Actions.Claims);
        f.Input("Website.", 300, 400);
        var summary = await f.Session.HandleDelegationAsync("explicit-service-choice");
        Assert.Contains(need, Text(summary));
        Assert.Contains("Service: Website", Text(summary));
        Assert.DoesNotContain("What would you like the website to help with", Text(summary));
        Assert.Equal(0, f.Actions.Writes);
        f.Acknowledge(summary, 500);
        f.Input("Please save the request you just read back.", 600, 900);
        await f.Session.HandleDelegationAsync("confirm-resolved-service");
        Assert.Equal(1, f.Actions.Writes);
        Assert.Equal("website_services", f.Actions.WrittenFacts!.ServiceKey);
        Assert.Equal(need, f.Actions.WrittenFacts.RequestSummary);
    }

    [Fact]
    public async Task CompleteConfirmedInquiryNeedsMatchingSummaryAckAndIndependentReadbackBeforeSuccess()
    {
        using var f = new SessionFixture();
        var summary = await f.PrepareAsync();
        Assert.DoesNotContain("saved for the team", Text(summary));
        Assert.Equal(0, f.Actions.Claims);
        f.Acknowledge(summary, 500);
        f.Input("Please save the request you just read back.", 600, 900);
        var result = await f.Session.HandleDelegationAsync("save-1");
        Assert.Equal(1, f.Actions.Claims); Assert.Equal(1, f.Actions.Writes); Assert.Equal(1, f.Actions.Reads);
        Assert.Contains("saved for the team to review", Text(result));
        Assert.Contains("No follow-up has been arranged", Text(result));
        Assert.Equal("verified", Assert.Single(f.Actions.Outcomes).State);
        Assert.Null(f.Actions.WrittenFacts!.Email); Assert.Null(f.Actions.WrittenFacts.Phone);
        Assert.Null(f.Actions.WrittenFacts.FirstName); Assert.Null(f.Actions.WrittenFacts.LastName);
        Assert.Equal("inbound_voice_only", f.Actions.WrittenFacts.RequestedContactMethod);
        Assert.Equal("not_requested", f.Actions.WrittenFacts.ContactPermissionState);
        Assert.StartsWith("caller-confirmation:v1:", f.Actions.Confirmation);
        Assert.Equal(ReceptionistInquiryAuthority.CanonicalArgumentsSha256(f.Actions.WrittenFacts), f.Actions.ArgumentsHash);
        Assert.All(result, update => { Assert.Equal("save-1", update.DelegationId); Assert.InRange(Encoding.UTF8.GetByteCount(update.Content), 1, 480); });
    }

    [Theory]
    [InlineData("Yes.")]
    [InlineData("Please save")]
    [InlineData("Please save it.")]
    [InlineData("Please save the request you just read back?")]
    [InlineData("Please save the request you just read back, but change the email.")]
    public async Task LoneYesPartialAndMixedConfirmationCannotAuthorizeAWrite(string input)
    {
        using var f = new SessionFixture();
        var summary = await f.PrepareAsync(); f.Acknowledge(summary, 500);
        f.Input(input, 600, 900);
        await f.Session.HandleDelegationAsync("insufficient");
        Assert.Equal(0, f.Actions.Claims); Assert.Equal(0, f.Actions.Writes);
    }

    [Theory]
    [InlineData(false, 600)]
    [InlineData(true, 400)]
    public async Task UnacknowledgedSummaryOrOldAudioCannotConfirm(bool acknowledge, int inputStart)
    {
        using var f = new SessionFixture();
        var summary = await f.PrepareAsync();
        if (acknowledge) f.Acknowledge(summary, 500);
        f.Input("Please save the request you just read back.", inputStart, 900);
        await f.Session.HandleDelegationAsync("old-or-unacknowledged");
        Assert.Equal(0, f.Actions.Claims); Assert.Equal(0, f.Actions.Writes);
    }

    [Fact]
    public async Task FullSentenceMayArriveInDeltasButNoInventedCompletedEventCanSupplyAuthority()
    {
        using var f = new SessionFixture();
        var summary = await f.PrepareAsync(); f.Acknowledge(summary, 500);
        f.Observe(new { type = "session.input_transcript.completed", event_id = "invented-complete", delta = "Please save the request you just read back.", start_ms = 600, end_ms = 700 });
        Assert.Equal(0, f.Actions.Claims);
        f.Input("Please save the request ", 800, 900);
        f.Input("you just read back.", 901, 1000);
        await f.Session.HandleDelegationAsync("actual-deltas");
        Assert.Equal(1, f.Actions.Writes);
    }

    [Fact]
    public async Task NewCorrectionWhileClaimWaitsKeepsReceiveResponsiveAndPreventsTheWrite()
    {
        using var f = new SessionFixture();
        var release = NewSignal();
        f.Actions.BeforeClaim = async () => await release.Task;
        var summary = await f.PrepareAsync(); f.Acknowledge(summary, 500);
        f.Input("Please save the request you just read back.", 600, 900);
        var handling = f.Session.HandleDelegationAsync("claiming");
        await f.Actions.ClaimEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Input("Actually, I need a website to explain my new service.", 1000, 1300);
        release.SetResult();
        var result = await handling;
        Assert.Equal(0, f.Actions.Writes);
        Assert.Contains("correction before saving", Text(result));
        Assert.Equal("rejected", Assert.Single(f.Actions.Outcomes).State);
        var corrected = await f.Session.HandleDelegationAsync("corrected-readback");
        Assert.Contains("explain my new service", Text(corrected));
    }

    [Fact]
    public async Task CorrectionDuringCommittedWriteDoesNotDisappearOrBecomeAnUnverifiedEdit()
    {
        using var f = new SessionFixture();
        var release = NewSignal();
        f.Actions.AfterCommit = async () => await release.Task;
        var summary = await f.PrepareAsync(); f.Acknowledge(summary, 500);
        f.Input("Please save the request you just read back.", 600, 900);
        var handling = f.Session.HandleDelegationAsync("writing");
        await f.Actions.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Input("Actually, I need a website for a different business.", 1000, 1300);
        release.SetResult();
        var result = await handling;
        Assert.Equal(1, f.Actions.Writes); Assert.Equal(1, f.Actions.Reads);
        Assert.Contains("before the latest correction could be applied", Text(result));
        Assert.DoesNotContain("different business", f.Actions.WrittenFacts!.RequestSummary);
        await f.Session.HandleDelegationAsync("later-correction");
        Assert.Equal(1, f.Actions.Writes);
    }

    [Fact]
    public async Task LostWriteResponseUsesReadbackAndCannotCauseAnAutomaticDuplicate()
    {
        using var f = new SessionFixture();
        f.Actions.AfterCommit = () => throw new InvalidOperationException("Synthetic response lost after commit.");
        var summary = await f.PrepareAsync(); f.Acknowledge(summary, 500);
        f.Input("Please save the request you just read back.", 600, 900);
        var result = await f.Session.HandleDelegationAsync("lost-write-response");
        Assert.Contains("saved and its record has been checked", Text(result));
        Assert.Equal(1, f.Actions.Writes);
        Assert.Equal("verified", Assert.Single(f.Actions.Outcomes).State);
        Assert.Empty(await f.Session.HandleDelegationAsync("lost-write-response"));
        Assert.Equal(1, f.Actions.Writes);
    }

    [Fact]
    public async Task MissingOrWrongOwnerReadbackNeverProducesSavedSuccess()
    {
        foreach (var wrongOwner in new[] { false, true })
        {
            using var f = new SessionFixture();
            f.Actions.SuppressReadback = !wrongOwner;
            f.Actions.WrongReadbackOwner = wrongOwner;
            var summary = await f.PrepareAsync(); f.Acknowledge(summary, 500);
            f.Input("Please save the request you just read back.", 600, 900);
            var result = await f.Session.HandleDelegationAsync("unverified-save");
            Assert.Contains("can't yet confirm", Text(result));
            Assert.DoesNotContain(f.Actions.Outcomes, outcome => outcome.State == "verified");
            await f.Session.HandleDelegationAsync("retry");
            Assert.Equal(1, f.Actions.Writes);
        }
    }

    [Fact]
    public async Task RestartReconcilesExistingActionsAndRequiresFreshContextWhenThereWasNoWrite()
    {
        using var f = new SessionFixture();
        f.Actions.Prior.Add(new(f.Actions.ActionId, "uncertain"));
        f.Actions.WrittenFacts = MinimalFacts();
        f.Actions.Committed = true;
        var restored = await f.Session.InitializeAsync();
        Assert.Contains("earlier request is saved", Text(restored));
        Assert.Equal(0, f.Actions.Claims); Assert.Equal(0, f.Actions.Writes);
        using var unknown = new SessionFixture();
        unknown.Actions.Prior.Add(new(unknown.Actions.ActionId, "pending"));
        var uncertain = await unknown.Session.InitializeAsync();
        Assert.Contains("can't yet confirm", Text(uncertain));
        unknown.Input("I need a website. Please save the request you just read back.", 100, 400);
        await unknown.Session.HandleDelegationAsync("pending-retry");
        Assert.Equal(0, unknown.Actions.Claims);
        using var fresh = new SessionFixture();
        await fresh.Session.InitializeAsync();
        fresh.Input("Please save the request you just read back.", 100, 400);
        await fresh.Session.HandleDelegationAsync("no-restored-context");
        Assert.Equal(0, fresh.Actions.Claims);
    }

    [Fact]
    public async Task ContactCorrectionsAreReadBackAndDoNotCreateMarketingOrCallbackPermission()
    {
        using var f = new SessionFixture();
        var summary = await f.PrepareAsync("I need a website to explain my services. My email is first@example.invalid.");
        f.Acknowledge(summary, 500);
        f.Input("Actually, my email is corrected@example.invalid.", 600, 900);
        var revised = await f.Session.HandleDelegationAsync("contact-correction");
        Assert.Contains("corrected@example.invalid", Text(revised));
        Assert.DoesNotContain("first@example.invalid", Text(revised));
        f.Acknowledge(revised, 1100);
        f.Input("Please save the request you just read back.", 1200, 1500);
        await f.Session.HandleDelegationAsync("contact-save");
        Assert.Equal("corrected@example.invalid", f.Actions.WrittenFacts!.Email);
        Assert.Equal("not_requested", f.Actions.WrittenFacts.ContactPermissionState);
        Assert.Equal("inbound_voice_only", f.Actions.WrittenFacts.RequestedContactMethod);
        Assert.Equal(0, f.Actions.NotificationSends);
    }

    [Fact]
    public async Task PartiallyUnderstoodCorrectionNeedsClarificationAndExplicitContactRemovalIsRespected()
    {
        using var f = new SessionFixture();
        var summary = await f.PrepareAsync("I need a website to explain my services. My email is first@example.invalid. My phone number is 2025550101.");
        f.Acknowledge(summary, 500);
        f.Input("Actually, my email is corrected@example.invalid. Use the other number instead.", 600, 900);
        var clarification = await f.Session.HandleDelegationAsync("partial-correction");
        Assert.Contains("What contact detail should I correct", Text(clarification));
        Assert.DoesNotContain("Here is the request", Text(clarification));
        Assert.Equal(0, f.Actions.Claims);
        f.Input("Please leave out my phone number.", 1000, 1200);
        var corrected = await f.Session.HandleDelegationAsync("remove-phone");
        Assert.Contains("corrected@example.invalid", Text(corrected));
        Assert.DoesNotContain("2025550101", Text(corrected));
        f.Acknowledge(corrected, 1400);
        f.Input("Please save the request you just read back.", 1500, 1800);
        await f.Session.HandleDelegationAsync("save-corrected-details");
        Assert.Null(f.Actions.WrittenFacts!.Phone);
        Assert.Equal("corrected@example.invalid", f.Actions.WrittenFacts.Email);
    }

    [Fact]
    public async Task AmbiguousServiceUnsupportedActionSensitiveDetailsAndNonInquiriesAreNotWritten()
    {
        using var f = new SessionFixture(); await f.Session.InitializeAsync();
        foreach (var (statement, expected) in new[]
        {
            ("I need a website and coaching.", "Which service"),
            ("Please transfer me to someone.", "can't book"),
            ("My password is not-for-storage.", "don't read out"),
            ("I am an existing customer with a question.", "existing customer account"),
            ("It is complicated, maybe.", "couldn't safely interpret")
        })
        {
            f.Input(statement, f.Time + 100, f.Time + 400);
            Assert.Contains(expected, Text(await f.Session.HandleDelegationAsync("question-" + f.Time)));
        }
        Assert.Equal(0, f.Actions.Claims); Assert.Equal(0, f.Actions.Writes);
    }

    [Fact]
    public async Task MalformedTimelineAndContextOverflowInvalidatePreparationWithoutCrashingObserve()
    {
        using var f = new SessionFixture();
        var summary = await f.PrepareAsync(); f.Acknowledge(summary, 500);
        f.Observe(new { type = "session.input_transcript.delta", event_id = "bad-timestamp", delta = "Please save the request you just read back.", start_ms = "wrong", end_ms = 900 });
        var result = await f.Session.HandleDelegationAsync("malformed");
        Assert.Contains("couldn't safely follow", Text(result));
        f.Input(new string('a', 6001), 1000, 1300);
        await f.Session.HandleDelegationAsync("overflow");
        Assert.Equal(0, f.Actions.Claims);
        f.Session.Dispose();
        f.Input("Please save the request you just read back.", 1400, 1700);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => f.Session.HandleDelegationAsync("disposed"));
    }

    private static string Text(IEnumerable<ReceptionistApplicationUpdate> updates) => string.Join(" ", updates.Select(x => x.Content));
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ConfirmedReceptionistInquiry MinimalFacts() => new()
    {
        AcquisitionClass = "observed_genuine_inquiry_pending_reconciliation", ServiceKey = "website_services",
        RequestSummary = "I need a website to explain my services."
    };
    private static string Services(string secondAlias = "coaching", string summary = "Synthetic website service information.") =>
        JsonSerializer.Serialize(new[]
        {
            new { serviceKey = "website_services", displayName = "Website services", aliases = new[] { "website" }, discoveryQuestion = "What would you like the website to help with?", publicSummary = summary },
            new { serviceKey = "coaching", displayName = "Coaching", aliases = new[] { secondAlias }, discoveryQuestion = "What would you like help working toward?", publicSummary = "Synthetic coaching service information." }
        });
    private static ReceptionistBusinessConfiguration Configuration() => new()
    {
        BusinessName = "Synthetic owner", ConversationInstructions = "Synthetic approved frontend policy.", BusinessPolicy = "No invented identity or consent.",
        ServiceDefinitionsJson = Services(), RequestedCapabilities = [ReceptionistCapabilities.CanonicalIntake],
        IntakeRulesJson = "{\"canonicalInquiry\":{\"services\":[{\"serviceKey\":\"website_services\",\"crmBucket\":\"ServiceInquiry\"},{\"serviceKey\":\"coaching\",\"crmBucket\":\"ServiceInquiry\"}],\"notifyOwner\":false}}",
        ApprovedKnowledgeJson = "{\"answers\":[{\"key\":\"general\",\"questions\":[\"What do you offer?\"],\"answer\":\"Synthetic approved information.\",\"status\":\"approved\"},{\"key\":\"dated\",\"questions\":[\"What was the old price?\"],\"answer\":\"A dated synthetic price reference.\",\"status\":\"dated\",\"asOfDate\":\"2026-02-01\"}]}"
    };

    private sealed class SessionFixture : IDisposable
    {
        internal readonly ReceptionistCallContext Call;
        internal readonly FakeActions Actions = new();
        internal readonly ReceptionistApplicationSession Session;
        internal readonly FakeBooking? Booking;
        internal int Time;
        private int events;
        internal SessionFixture(ReceptionistBusinessConfiguration? configuration = null, MarketingOwnerScope? owner = null, bool controlledTest = false, FakeBooking? booking = null)
        {
            var config = configuration ?? Configuration();
            Call = new(Guid.NewGuid(), owner ?? MarketingOwnerScope.Founder, "openai", "synthetic-project", Guid.NewGuid().ToString("N"),
                Guid.NewGuid(), config, new ReceptionistProviderBinding { Capabilities = booking is null ? [] :
                    [new(ReceptionistCapabilities.CanonicalBooking, ReceptionistBookingAuthority.RouteKey, ReceptionistCapabilityStates.Accepted, "synthetic-booking-review")] },
                DateTime.UtcNow.AddMinutes(3), "accepted", controlledTest);
            Booking = booking;
            Session = new(Call, Guid.NewGuid(), ReceptionistApplicationCatalog.Read(config), Actions, TimeProvider.System, booking);
        }
        internal async Task<IReadOnlyList<ReceptionistApplicationUpdate>> PrepareAsync(string statement = "I need a website to explain my services.")
        {
            await Session.InitializeAsync(); Input(statement, 100, 200);
            return await Session.HandleDelegationAsync("prepare-" + events);
        }
        internal void Input(string delta, int start, int end)
        {
            Time = end;
            Observe(new { type = "session.input_transcript.delta", event_id = "input-" + ++events, delta, start_ms = start, end_ms = end });
        }
        internal void Acknowledge(IEnumerable<ReceptionistApplicationUpdate> summary, int end)
        {
            foreach (var update in summary)
                Observe(new { type = "session.commentary.appended", event_id = "ack-" + ++events, client_event_id = update.EventId, start_ms = end - 10, end_ms = end });
        }
        internal void Observe(object value)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value)); Session.Observe(document.RootElement);
        }
        public void Dispose() => Session.Dispose();
    }

    private sealed class FakeActions : IReceptionistApplicationActions
    {
        internal readonly Guid ActionId = Guid.NewGuid();
        internal int Claims, Writes, Reads;
        internal int NotificationSends => 0; // This boundary exposes no notification operation.
        internal bool Committed, SuppressReadback, WrongReadbackOwner;
        internal string? Confirmation, ArgumentsHash;
        internal ConfirmedReceptionistInquiry? WrittenFacts;
        internal readonly List<ReceptionistApplicationPriorAction> Prior = [];
        internal readonly List<(string State, string? Reference, string Receipt)> Outcomes = [];
        internal readonly TaskCompletionSource ClaimEntered = NewSignal();
        internal readonly TaskCompletionSource WriteEntered = NewSignal();
        internal Func<Task>? BeforeClaim, AfterCommit;
        public Task<IReadOnlyList<ReceptionistApplicationPriorAction>> PriorAsync(ReceptionistCallContext call, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReceptionistApplicationPriorAction>>(Prior);
        public async Task<ReceptionistActionClaim> ClaimAsync(ReceptionistCallContext call, string logicalKey, string factsHash,
            string confirmationReceipt, Guid leaseToken, CancellationToken ct)
        {
            Claims++; ArgumentsHash = factsHash; Confirmation = confirmationReceipt; ClaimEntered.TrySetResult();
            if (BeforeClaim is not null) await BeforeClaim();
            return new(true, "pending", ActionId, ReceptionistInquiryAuthority.RouteKey);
        }
        public async Task<ReceptionistInquiryPersistence> PersistAsync(ReceptionistCallContext call, Guid actionId,
            ConfirmedReceptionistInquiry facts, string confirmationReceipt, CancellationToken ct)
        {
            Writes++; WrittenFacts = facts; Committed = true; WriteEntered.TrySetResult();
            if (AfterCommit is not null) await AfterCommit();
            return new("canonical_intake_saved", ActionId, "website-lead:" + ActionId, true, null, new(true, false, "pending", null, null));
        }
        public Task<ReceptionistApplicationOutcome?> ReadBackAsync(ReceptionistCallContext call, Guid actionId, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult<ReceptionistApplicationOutcome?>(!Committed || SuppressReadback ? null : new(
                WrongReadbackOwner ? "different-owner" : call.Owner.Key, call.CallId, actionId,
                call.ControlledTest ? "qa_outcome_verified" : "crm_readback_verified", call.ControlledTest,
                call.ControlledTest ? "receptionist-qa:" + ActionId : "website-lead:" + ActionId,
                call.ControlledTest ? "qa-intake-outcome:v1:synthetic-proof" : "inquiry-readback:v1:synthetic-proof"));
        }
        public Task OutcomeAsync(Guid actionId, string state, string? actualReference, string receipt, CancellationToken ct)
        { Outcomes.Add((state, actualReference, receipt)); return Task.CompletedTask; }
    }
}
