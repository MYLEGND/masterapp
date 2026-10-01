using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using AgentPortal.Controllers;
using AgentPortal.Models;
using AgentPortal.Security;
using AgentPortal.Services.Engineering;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

public sealed class FounderEngineeringCommandCenterTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MasterAppDbContext _db;
    private readonly LegendEngineeringContractAuthority _authority;

    public FounderEngineeringCommandCenterTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new MasterAppDbContext(options);
        CreateTables();
        _authority = new LegendEngineeringContractAuthority(_db);
    }

    [Fact]
    public async Task BuiltinContract_IsCanonicalUntilFounderPublishesARevision()
    {
        var current = await _authority.GetCurrentAsync(default);

        Assert.Equal(LegendEngineeringContractAuthority.BuiltinRevision, current.Revision);
        Assert.Equal(0, current.Version);
        Assert.True(current.ModelExecutionEnabled);
        Assert.Contains("Preserve successful evidence", current.SharedDirective, StringComparison.Ordinal);
        Assert.Contains("engineering supervisor", current.HeadGptDirective, StringComparison.Ordinal);
        Assert.Contains("implementation engineer", current.CodexDirective, StringComparison.Ordinal);
        Assert.Empty(await _authority.GetHistoryAsync(10, default));
    }

    [Fact]
    public async Task Update_IsRevisionLocked_AndCreatesImmutableHistory()
    {
        var current = await _authority.GetCurrentAsync(default);
        var updated = await _authority.UpdateAsync(
            current.Revision,
            true,
            current.SharedDirective + "\n- Never restart a green gate.",
            current.HeadGptDirective,
            current.CodexDirective,
            current.ReviewerDirective,
            "Founder",
            default);

        Assert.Equal(1, updated.Version);
        Assert.NotEqual(current.Revision, updated.Revision);

        var history = await _authority.GetHistoryAsync(10, default);
        var saved = Assert.Single(history);
        Assert.Equal(updated.Revision, saved.Revision);
        Assert.Contains("Never restart a green gate.", saved.SharedDirective, StringComparison.Ordinal);

        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _authority.UpdateAsync(
                current.Revision,
                true,
                "stale",
                current.HeadGptDirective,
                current.CodexDirective,
                current.ReviewerDirective,
                "Founder",
                default));
        Assert.Equal("engineering_operational_contract_changed", stale.Message);
    }

    [Fact]
    public async Task NoOpUpdate_PreservesRevisionAndDoesNotInvalidateContexts()
    {
        var current = await _authority.GetCurrentAsync(default);
        var first = await _authority.UpdateAsync(
            current.Revision,
            true,
            current.SharedDirective + "\n- Keep this revision stable.",
            current.HeadGptDirective,
            current.CodexDirective,
            current.ReviewerDirective,
            "Founder",
            default);

        var replay = await _authority.UpdateAsync(
            first.Revision,
            first.ModelExecutionEnabled,
            first.SharedDirective,
            first.HeadGptDirective,
            first.CodexDirective,
            first.ReviewerDirective,
            "Founder",
            default);

        Assert.Equal(first.Revision, replay.Revision);
        Assert.Equal(first.Version, replay.Version);
        Assert.Single(await _authority.GetHistoryAsync(10, default));
    }

    [Fact]
    public async Task BindingAuthority_RejectsStaleRevision_AndFounderPause()
    {
        var builtin = await _authority.GetCurrentAsync(default);
        var initial = await _authority.ValidateBindingAsync(builtin.Revision, default);
        Assert.True(initial.Valid);
        Assert.Equal("engineering_operational_contract_current", initial.Code);

        var changed = await _authority.UpdateAsync(
            builtin.Revision,
            true,
            "new shared",
            builtin.HeadGptDirective,
            builtin.CodexDirective,
            builtin.ReviewerDirective,
            "Founder",
            default);

        var stale = await _authority.ValidateBindingAsync(builtin.Revision, default);
        Assert.False(stale.Valid);
        Assert.Equal("engineering_operational_contract_changed", stale.Code);

        var current = await _authority.ValidateBindingAsync(changed.Revision, default);
        Assert.True(current.Valid);

        var paused = await _authority.UpdateAsync(
            changed.Revision,
            false,
            changed.SharedDirective,
            changed.HeadGptDirective,
            changed.CodexDirective,
            changed.ReviewerDirective,
            "Founder",
            default);

        var blocked = await _authority.ValidateBindingAsync(paused.Revision, default);
        Assert.False(blocked.Valid);
        Assert.Equal("engineering_operational_execution_paused", blocked.Code);
    }

    [Fact]
    public async Task Restore_CreatesNewRevision_InsteadOfRewritingHistory()
    {
        var builtin = await _authority.GetCurrentAsync(default);
        var first = await _authority.UpdateAsync(
            builtin.Revision,
            true,
            "first shared",
            "first head",
            "first codex",
            "first reviewer",
            "Founder",
            default);
        var second = await _authority.UpdateAsync(
            first.Revision,
            false,
            "second shared",
            "second head",
            "second codex",
            "second reviewer",
            "Founder",
            default);

        var restored = await _authority.RestoreAsync(
            second.Revision,
            first.Revision,
            "Founder",
            default);

        Assert.Equal(3, restored.Version);
        Assert.NotEqual(first.Revision, restored.Revision);
        Assert.Equal("first shared", restored.SharedDirective);
        Assert.True(restored.ModelExecutionEnabled);
        Assert.Equal(3, (await _authority.GetHistoryAsync(10, default)).Count);
    }

    [Fact]
    public async Task ContractSizeAndControlCharacters_FailClosed()
    {
        var current = await _authority.GetCurrentAsync(default);

        var oversized = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _authority.UpdateAsync(
                current.Revision,
                true,
                new string('x', LegendEngineeringContractAuthority.MaximumDirectiveCharacters + 1),
                "",
                "",
                "",
                "Founder",
                default));
        Assert.Equal("engineering_contract_directive_invalid", oversized.Message);

        var control = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _authority.UpdateAsync(
                current.Revision,
                true,
                "bad\u0001directive",
                "",
                "",
                "",
                "Founder",
                default));
        Assert.Equal("engineering_contract_directive_invalid", control.Message);
    }

    [Fact]
    public void FounderEngineeringController_IsFounderOnly_AndMutationsRequireAntiforgery()
    {
        var type = typeof(FounderEngineeringController);
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(type.GetCustomAttribute<FounderOnlyAttribute>());

        foreach (var name in new[]
                 {
                     nameof(FounderEngineeringController.SaveContract),
                     nameof(FounderEngineeringController.RestoreContract),
                     nameof(FounderEngineeringController.DisconnectChatGpt)
                 })
        {
            var method = type.GetMethod(name)!;
            Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());
            Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        }
    }

    [Fact]
    public void EditableContractInput_HasNoAuthorityExpansionFields()
    {
        var fields = typeof(FounderEngineeringContractInput)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var forbidden in new[]
                 {
                     "AllowedTools", "AllowedSourceClasses", "RiskClass", "ProtectedAreas",
                     "LeaseIdentity", "ReleaseAuthority", "MergeAuthority", "ApiKey",
                     "AccessToken", "RefreshToken"
                 })
            Assert.DoesNotContain(forbidden, fields);
    }

    private void CreateTables()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE LegendEngineeringOperationalContract (
              ContractKey TEXT PRIMARY KEY,
              Revision TEXT NOT NULL,
              Version INTEGER NOT NULL,
              ModelExecutionEnabled INTEGER NOT NULL,
              SharedDirective TEXT NOT NULL,
              HeadGptDirective TEXT NOT NULL,
              CodexDirective TEXT NOT NULL,
              ReviewerDirective TEXT NOT NULL,
              UpdatedUtc TEXT NOT NULL,
              UpdatedBy TEXT NOT NULL);

            CREATE TABLE LegendEngineeringOperationalContractHistory (
              Revision TEXT PRIMARY KEY,
              Version INTEGER NOT NULL UNIQUE,
              ModelExecutionEnabled INTEGER NOT NULL,
              SharedDirective TEXT NOT NULL,
              HeadGptDirective TEXT NOT NULL,
              CodexDirective TEXT NOT NULL,
              ReviewerDirective TEXT NOT NULL,
              UpdatedUtc TEXT NOT NULL,
              UpdatedBy TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
