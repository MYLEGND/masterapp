using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Leads;
using Infrastructure.Reception;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>Real deterministic state machine with a recording application boundary; canonical relational authorities have their own integration tests.</summary>
public sealed class ReceptionistApplicationServicesTests
{
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
        internal int Time;
        private int events;
        internal SessionFixture(ReceptionistBusinessConfiguration? configuration = null, MarketingOwnerScope? owner = null)
        {
            var config = configuration ?? Configuration();
            Call = new(Guid.NewGuid(), owner ?? MarketingOwnerScope.Founder, "openai", "synthetic-project", Guid.NewGuid().ToString("N"),
                Guid.NewGuid(), config, new ReceptionistProviderBinding(), DateTime.UtcNow.AddMinutes(3), "accepted", false);
            Session = new(Call, Guid.NewGuid(), ReceptionistApplicationCatalog.Read(config), Actions, TimeProvider.System);
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
        public Task<ReceptionistInquiryReadback?> ReadBackAsync(ReceptionistCallContext call, Guid actionId, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult<ReceptionistInquiryReadback?>(!Committed || SuppressReadback ? null : new(
                ActionId, "website-lead:" + ActionId, Guid.NewGuid(), ActionId.ToString("N"),
                WrongReadbackOwner ? "different-owner" : call.Owner.Key, call.CallId, actionId, WrittenFacts!,
                ReceptionistInquiryAuthority.CanonicalArgumentsSha256(WrittenFacts!), "inquiry-readback:v1:synthetic-proof",
                "crm_readback_verified", "owner_notification_disabled", new(true, false, "pending", null, null)));
        }
        public Task OutcomeAsync(Guid actionId, string state, string? actualReference, string receipt, CancellationToken ct)
        { Outcomes.Add((state, actualReference, receipt)); return Task.CompletedTask; }
    }
}
