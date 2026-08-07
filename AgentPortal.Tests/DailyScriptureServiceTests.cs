using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.DailyScripture;
using Infrastructure.Messaging;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class DailyScriptureServiceTests
{
    [Fact]
    public async Task Resolver_ScheduledOverrideWinsAndPreservesTheExactPassage()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var date = new DateOnly(2026, 8, 7);
        const string passage = "1 Trust in the LORD with all thine heart.\n2 And in all thy ways acknowledge him.\n\nReflect without alteration.";
        db.DailyScriptureOverrides.Add(new DailyScriptureOverride
        {
            DisplayDate = date,
            Reference = "Proverbs 3:5–6",
            Translation = "KJV",
            PassageText = passage,
            IsActive = true,
            CreatedByUserId = "manager-oid",
            CreatedByParticipantType = MessagingParticipantTypes.Agent,
            UpdatedByUserId = "manager-oid",
            UpdatedByParticipantType = MessagingParticipantTypes.Agent
        });
        await db.SaveChangesAsync();

        var resolver = CreateResolver(db);
        var resolved = await resolver.GetForDateAsync(date);

        Assert.Equal(DailyScriptureSources.ScheduledOverride, resolved.Source);
        Assert.Equal("Proverbs 3:5–6", resolved.Reference);
        Assert.Equal(passage, resolved.PassageText);
        Assert.Equal(passage, resolved.Text);
    }

    [Fact]
    public async Task Resolver_UsesTheEstablishedStableCatalogWhenNoOverrideExists()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var resolver = CreateResolver(db);
        var date = new DateOnly(2026, 8, 9);

        var first = await resolver.GetForDateAsync(date);
        var second = await resolver.GetForDateAsync(date);

        Assert.Equal(DailyScriptureSources.DailyCatalog, first.Source);
        Assert.NotEmpty(first.Verses);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Resolver_DoesNotLeakYesterdayOrTomorrowOverridesIntoToday()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var today = new DateOnly(2026, 8, 7);
        db.DailyScriptureOverrides.AddRange(
            OverrideFor(today.AddDays(-1), "Yesterday"),
            OverrideFor(today.AddDays(1), "Tomorrow"));
        await db.SaveChangesAsync();

        var resolved = await CreateResolver(db).GetForDateAsync(today);

        Assert.Equal(DailyScriptureSources.DailyCatalog, resolved.Source);
        Assert.NotEqual("Yesterday", resolved.Reference);
        Assert.NotEqual("Tomorrow", resolved.Reference);
    }

    [Fact]
    public async Task Resolver_UsesTheConfiguredPhoenixBusinessDayAtTheUtcBoundary()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var resolver = CreateResolver(db);

        Assert.Equal(
            new DateOnly(2026, 8, 6),
            resolver.GetBusinessDate(new DateTime(2026, 8, 7, 6, 59, 0, DateTimeKind.Utc)));
        Assert.Equal(
            new DateOnly(2026, 8, 7),
            resolver.GetBusinessDate(new DateTime(2026, 8, 7, 7, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task AuthorizedManager_CanCreateUpdateAndRemoveOneDateScopedOverride()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var access = GrantedAccess();
        var resolver = CreateResolver(db);
        var management = new DailyScriptureManagementService(db, resolver, access.Object);
        var actor = new MessagingActor("manager-oid", MessagingParticipantTypes.Agent);
        var futureDate = resolver.GetBusinessDate(DateTime.UtcNow).AddDays(2);
        var original = new DailyScriptureOverrideDraft(
            futureDate,
            "Psalm 27",
            "KJV",
            "1 The LORD is my light and my salvation; whom shall I fear?");

        var created = await management.CreateAsync(actor, original);
        Assert.True(created.Succeeded, created.ErrorMessage);
        var saved = Assert.IsType<DailyScriptureOverrideSummary>(created.Value);

        var updated = await management.UpdateAsync(
            actor,
            saved.Id,
            original with { Reference = "Psalm 27:1" });
        Assert.True(updated.Succeeded, updated.ErrorMessage);
        Assert.Equal("Psalm 27:1", updated.Value!.Reference);

        var resolved = await resolver.GetForDateAsync(futureDate);
        Assert.Equal(DailyScriptureSources.ScheduledOverride, resolved.Source);
        Assert.Equal("Psalm 27:1", resolved.Reference);

        var removed = await management.RemoveAsync(actor, saved.Id);
        Assert.True(removed.Succeeded, removed.ErrorMessage);
        Assert.Equal(
            DailyScriptureSources.DailyCatalog,
            (await resolver.GetForDateAsync(futureDate)).Source);
    }

    [Fact]
    public async Task UnauthorizedUser_CannotCreateAnOverride()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var access = new Mock<IControlledResourceAccessService>(MockBehavior.Strict);
        access.Setup(service => service.GetAccessAsync(
                It.IsAny<MessagingActor>(),
                ControlledResourceTypes.ScriptureManagement,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ControlledResourceAccess(
                ControlledResourceTypes.ScriptureManagement,
                ControlledResourceAccessStates.NotGranted,
                false));
        var resolver = CreateResolver(db);
        var management = new DailyScriptureManagementService(db, resolver, access.Object);

        var result = await management.CreateAsync(
            new MessagingActor("ordinary-user", MessagingParticipantTypes.Client),
            new DailyScriptureOverrideDraft(
                resolver.GetBusinessDate(DateTime.UtcNow),
                "Psalm 1",
                "KJV",
                "Blessed is the man."));

        Assert.False(result.Succeeded);
        Assert.Equal("DAILY_SCRIPTURE_FORBIDDEN", result.ErrorCode);
        Assert.Empty(db.DailyScriptureOverrides);
    }

    private static DailyScriptureService CreateResolver(
        Infrastructure.Data.MasterAppDbContext db) =>
        new(db, new DailyScriptureOptions { BusinessTimeZoneId = "America/Phoenix" });

    private static DailyScriptureOverride OverrideFor(DateOnly date, string reference) => new()
    {
        DisplayDate = date,
        Reference = reference,
        Translation = "KJV",
        PassageText = reference,
        IsActive = true,
        CreatedByUserId = "manager-oid",
        CreatedByParticipantType = MessagingParticipantTypes.Agent,
        UpdatedByUserId = "manager-oid",
        UpdatedByParticipantType = MessagingParticipantTypes.Agent
    };

    private static Mock<IControlledResourceAccessService> GrantedAccess()
    {
        var access = new Mock<IControlledResourceAccessService>(MockBehavior.Strict);
        access.Setup(service => service.GetAccessAsync(
                It.IsAny<MessagingActor>(),
                ControlledResourceTypes.ScriptureManagement,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ControlledResourceAccess(
                ControlledResourceTypes.ScriptureManagement,
                ControlledResourceAccessStates.Granted,
                false));
        return access;
    }
}
