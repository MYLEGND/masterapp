using System.Data;
using System.Data.Common;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services.Engineering;

internal sealed record LegendEngineeringOperationalContract(
    string Revision,
    long Version,
    bool ModelExecutionEnabled,
    string SharedDirective,
    string HeadGptDirective,
    string CodexDirective,
    string ReviewerDirective,
    DateTime UpdatedUtc,
    string UpdatedBy)
{
    internal string DirectiveForRole(string role) => role switch
    {
        Domain.Engineering.EngineeringRole.HeadGpt => HeadGptDirective,
        Domain.Engineering.EngineeringRole.CodexImplementer => CodexDirective,
        Domain.Engineering.EngineeringRole.IndependentReviewer => ReviewerDirective,
        _ => SharedDirective
    };
}

internal sealed record LegendEngineeringContractRevision(
    string Revision,
    long Version,
    bool ModelExecutionEnabled,
    string SharedDirective,
    string HeadGptDirective,
    string CodexDirective,
    string ReviewerDirective,
    DateTime UpdatedUtc,
    string UpdatedBy);

internal interface ILegendEngineeringContractAuthority
{
    Task<LegendEngineeringOperationalContract> GetCurrentAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<LegendEngineeringContractRevision>> GetHistoryAsync(int take, CancellationToken cancellationToken);
    Task<LegendEngineeringOperationalContract> UpdateAsync(
        string expectedRevision,
        bool modelExecutionEnabled,
        string? sharedDirective,
        string? headGptDirective,
        string? codexDirective,
        string? reviewerDirective,
        string updatedBy,
        CancellationToken cancellationToken);
    Task<LegendEngineeringOperationalContract> RestoreAsync(
        string expectedRevision,
        string restoreRevision,
        string updatedBy,
        CancellationToken cancellationToken);
}

internal sealed class LegendEngineeringContractAuthority(MasterAppDbContext db)
    : ILegendEngineeringContractAuthority
{
    internal const string BuiltinRevision = "legend-engineering-operational.builtin-v1";
    internal const int MaximumDirectiveCharacters = 12_000;
    internal const int MaximumTotalDirectiveCharacters = 36_000;

    internal const string DefaultSharedDirective =
        """
        Preserve successful evidence and advance monotonically. Diagnose the canonical owner before changing code.
        Never create parallel authorities, overrides, compatibility shims, duplicate registries, or unrelated refactors.
        Fix only demonstrated failures, preserve compatible green validation, deploy only affected applications, and do not call work complete until live proof passes.
        """;

    internal const string DefaultHeadGptDirective =
        """
        Act as the engineering supervisor, not the implementation engineer.
        Establish evidence sufficiency, root cause, canonical authority, dependency scope, risk, and the smallest valid task topology.
        Escalate or stop when evidence is insufficient. Do not edit repository source, self-authorize, merge, deploy, or weaken a gate.
        """;

    internal const string DefaultCodexDirective =
        """
        Act only as the implementation engineer for the current EngineeringContext.
        Repair the canonical root cause on the isolated candidate, change only permitted SAFE_SOURCE files, add focused regression proof, and respond only to actual review or CI evidence.
        Do not reprioritize work, broaden scope, create competing systems, merge, deploy, or decide that production is fixed.
        """;

    internal const string DefaultReviewerDirective =
        """
        Act as an independent reviewer, not a second implementer.
        Challenge the root-cause claim, detect patches, duplicate authorities, unrelated changes, privacy/security boundary violations, stale evidence, and inadequate tests.
        Approve validation only when the candidate is canonical, minimal, and sufficiently proven.
        """;

    public async Task<LegendEngineeringOperationalContract> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            var current = await ReadCurrentAsync(connection, null, cancellationToken);
            return current ?? Defaults();
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    public async Task<IReadOnlyList<LegendEngineeringContractRevision>> GetHistoryAsync(
        int take,
        CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 25);
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT [Revision],[Version],[ModelExecutionEnabled],[SharedDirective],[HeadGptDirective],
                       [CodexDirective],[ReviewerDirective],[UpdatedUtc],[UpdatedBy]
                FROM [LegendEngineeringOperationalContractHistory]
                ORDER BY [Version] DESC
                """;
            var rows = new List<LegendEngineeringContractRevision>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (rows.Count < take && await reader.ReadAsync(cancellationToken))
                rows.Add(ReadRevision(reader));
            return rows;
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    public Task<LegendEngineeringOperationalContract> UpdateAsync(
        string expectedRevision,
        bool modelExecutionEnabled,
        string? sharedDirective,
        string? headGptDirective,
        string? codexDirective,
        string? reviewerDirective,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(
            modelExecutionEnabled,
            sharedDirective,
            headGptDirective,
            codexDirective,
            reviewerDirective,
            updatedBy);
        return WithContractTransactionAsync(async (connection, transaction) =>
        {
            var current = await ReadCurrentAsync(connection, transaction, cancellationToken) ?? Defaults();
            RequireExpected(current.Revision, expectedRevision);
            return await WriteAsync(connection, transaction, current, normalized, cancellationToken);
        }, cancellationToken);
    }

    public Task<LegendEngineeringOperationalContract> RestoreAsync(
        string expectedRevision,
        string restoreRevision,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(restoreRevision) || restoreRevision.Length > 64)
            throw new InvalidOperationException("engineering_contract_restore_revision_invalid");

        return WithContractTransactionAsync(async (connection, transaction) =>
        {
            var current = await ReadCurrentAsync(connection, transaction, cancellationToken) ?? Defaults();
            RequireExpected(current.Revision, expectedRevision);
            var source = await ReadHistoryAsync(connection, transaction, restoreRevision, cancellationToken)
                ?? throw new InvalidOperationException("engineering_contract_restore_revision_not_found");
            var normalized = Normalize(
                source.ModelExecutionEnabled,
                source.SharedDirective,
                source.HeadGptDirective,
                source.CodexDirective,
                source.ReviewerDirective,
                updatedBy);
            return await WriteAsync(connection, transaction, current, normalized, cancellationToken);
        }, cancellationToken);
    }

    private static LegendEngineeringOperationalContract Defaults() =>
        new(
            BuiltinRevision,
            0,
            true,
            DefaultSharedDirective,
            DefaultHeadGptDirective,
            DefaultCodexDirective,
            DefaultReviewerDirective,
            DateTime.UnixEpoch,
            "SYSTEM_DEFAULT");

    private static ContractInput Normalize(
        bool modelExecutionEnabled,
        string? sharedDirective,
        string? headGptDirective,
        string? codexDirective,
        string? reviewerDirective,
        string updatedBy)
    {
        var shared = NormalizeText(sharedDirective);
        var head = NormalizeText(headGptDirective);
        var codex = NormalizeText(codexDirective);
        var reviewer = NormalizeText(reviewerDirective);
        if (shared.Length + head.Length + codex.Length + reviewer.Length > MaximumTotalDirectiveCharacters)
            throw new InvalidOperationException("engineering_contract_too_large");

        var actor = string.IsNullOrWhiteSpace(updatedBy) ? "Founder" : updatedBy.Trim();
        if (actor.Length > 128 || actor.Any(char.IsControl))
            throw new InvalidOperationException("engineering_contract_actor_invalid");

        return new(modelExecutionEnabled, shared, head, codex, reviewer, actor);
    }

    private static string NormalizeText(string? value)
    {
        var normalized = (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Trim();
        if (normalized.Length > MaximumDirectiveCharacters ||
            normalized.Any(character => char.IsControl(character) && character is not ('\n' or '\t')))
            throw new InvalidOperationException("engineering_contract_directive_invalid");
        return normalized;
    }

    private static void RequireExpected(string currentRevision, string? expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(expectedRevision) ||
            !string.Equals(currentRevision, expectedRevision.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("engineering_operational_contract_changed");
    }

    private static async Task<LegendEngineeringOperationalContract> WriteAsync(
        DbConnection connection,
        DbTransaction transaction,
        LegendEngineeringOperationalContract current,
        ContractInput input,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var next = new LegendEngineeringOperationalContract(
            Guid.NewGuid().ToString("N"),
            checked(current.Version + 1),
            input.ModelExecutionEnabled,
            input.SharedDirective,
            input.HeadGptDirective,
            input.CodexDirective,
            input.ReviewerDirective,
            now,
            input.UpdatedBy);

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE [LegendEngineeringOperationalContract] SET
                    [Revision]=@revision,[Version]=@version,[ModelExecutionEnabled]=@enabled,
                    [SharedDirective]=@shared,[HeadGptDirective]=@head,[CodexDirective]=@codex,
                    [ReviewerDirective]=@reviewer,[UpdatedUtc]=@updated,[UpdatedBy]=@by
                WHERE [ContractKey]='founder-default' AND [Revision]=@expected
                """;
            BindCurrent(update, next);
            Add(update, "@expected", current.Revision);
            var changed = await update.ExecuteNonQueryAsync(cancellationToken);
            if (current.Version > 0 && changed != 1)
                throw new InvalidOperationException("engineering_operational_contract_changed");
            if (current.Version == 0 && changed == 0)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO [LegendEngineeringOperationalContract]
                    ([ContractKey],[Revision],[Version],[ModelExecutionEnabled],[SharedDirective],[HeadGptDirective],
                     [CodexDirective],[ReviewerDirective],[UpdatedUtc],[UpdatedBy])
                    VALUES ('founder-default',@revision,@version,@enabled,@shared,@head,@codex,@reviewer,@updated,@by)
                    """;
                BindCurrent(insert, next);
                try
                {
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (DbException)
                {
                    throw new InvalidOperationException("engineering_operational_contract_changed");
                }
            }
        }

        await using (var history = connection.CreateCommand())
        {
            history.Transaction = transaction;
            history.CommandText = """
                INSERT INTO [LegendEngineeringOperationalContractHistory]
                ([Revision],[Version],[ModelExecutionEnabled],[SharedDirective],[HeadGptDirective],[CodexDirective],
                 [ReviewerDirective],[UpdatedUtc],[UpdatedBy])
                VALUES (@revision,@version,@enabled,@shared,@head,@codex,@reviewer,@updated,@by)
                """;
            BindCurrent(history, next);
            await history.ExecuteNonQueryAsync(cancellationToken);
        }

        return next;
    }

    private static async Task<LegendEngineeringOperationalContract?> ReadCurrentAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT [Revision],[Version],[ModelExecutionEnabled],[SharedDirective],[HeadGptDirective],
                   [CodexDirective],[ReviewerDirective],[UpdatedUtc],[UpdatedBy]
            FROM [LegendEngineeringOperationalContract]
            WHERE [ContractKey]='founder-default'
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCurrent(reader) : null;
    }

    private static async Task<LegendEngineeringContractRevision?> ReadHistoryAsync(
        DbConnection connection,
        DbTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT [Revision],[Version],[ModelExecutionEnabled],[SharedDirective],[HeadGptDirective],
                   [CodexDirective],[ReviewerDirective],[UpdatedUtc],[UpdatedBy]
            FROM [LegendEngineeringOperationalContractHistory]
            WHERE [Revision]=@revision
            """;
        Add(command, "@revision", revision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRevision(reader) : null;
    }

    private static LegendEngineeringOperationalContract ReadCurrent(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetBoolean(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetDateTime(7),
            reader.GetString(8));

    private static LegendEngineeringContractRevision ReadRevision(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetBoolean(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetDateTime(7),
            reader.GetString(8));

    private async Task<T> WithContractTransactionAsync<T>(
        Func<DbConnection, DbTransaction, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var transaction =
                await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            try
            {
                var result = await action(connection, transaction);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                try { await transaction.RollbackAsync(cancellationToken); } catch { }
                throw;
            }
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    private static void BindCurrent(DbCommand command, LegendEngineeringOperationalContract value)
    {
        Add(command, "@revision", value.Revision);
        Add(command, "@version", value.Version);
        Add(command, "@enabled", value.ModelExecutionEnabled);
        Add(command, "@shared", value.SharedDirective);
        Add(command, "@head", value.HeadGptDirective);
        Add(command, "@codex", value.CodexDirective);
        Add(command, "@reviewer", value.ReviewerDirective);
        Add(command, "@updated", value.UpdatedUtc);
        Add(command, "@by", value.UpdatedBy);
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record ContractInput(
        bool ModelExecutionEnabled,
        string SharedDirective,
        string HeadGptDirective,
        string CodexDirective,
        string ReviewerDirective,
        string UpdatedBy);
}
