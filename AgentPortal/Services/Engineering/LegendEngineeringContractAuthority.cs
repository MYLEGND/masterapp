using System.Data;
using System.Data.Common;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services.Engineering;

internal sealed record LegendEngineeringOperationalContract(
    string Revision,
    long Version,
    bool ModelExecutionEnabled,
    bool AutonomousEngineeringEnabled,
    string HeadGptModel,
    string CodexModel,
    string ReviewerModel,
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
        _ => string.Empty
    };

    internal string ModelForTier(string tier) => tier switch
    {
        Domain.Engineering.EngineeringModelTier.CodeImplementation => CodexModel,
        Domain.Engineering.EngineeringModelTier.IndependentReview => ReviewerModel,
        _ => HeadGptModel
    };
}

internal sealed record LegendEngineeringContractRevision(
    string Revision,
    long Version,
    bool ModelExecutionEnabled,
    bool AutonomousEngineeringEnabled,
    string HeadGptModel,
    string CodexModel,
    string ReviewerModel,
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
    Task<(bool Valid, string Code, LegendEngineeringOperationalContract Contract)> ValidateBindingAsync(
        string? revision,
        CancellationToken cancellationToken);

    // Compatibility surface for existing callers that only edit the directive contract.
    // New activation/runtime controls remain on the same canonical row.
    Task<LegendEngineeringOperationalContract> UpdateAsync(
        string expectedRevision,
        bool modelExecutionEnabled,
        string? sharedDirective,
        string? headGptDirective,
        string? codexDirective,
        string? reviewerDirective,
        string updatedBy,
        CancellationToken cancellationToken);

    Task<LegendEngineeringOperationalContract> UpdateAllAsync(
        string expectedRevision,
        bool modelExecutionEnabled,
        bool autonomousEngineeringEnabled,
        string? headGptModel,
        string? codexModel,
        string? reviewerModel,
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
    internal const string BuiltinRevision = "legend-engineering-operational.builtin-v2";
    internal const int MaximumDirectiveCharacters = 12_000;
    internal const int MaximumTotalDirectiveCharacters = 36_000;
    internal const int MaximumModelSlugCharacters = 160;
    internal const string AutoModel = "auto";

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

    public async Task<(bool Valid, string Code, LegendEngineeringOperationalContract Contract)> ValidateBindingAsync(
        string? revision,
        CancellationToken cancellationToken)
    {
        var current = await GetCurrentAsync(cancellationToken);
        if (!current.ModelExecutionEnabled)
            return (false, "engineering_operational_execution_paused", current);
        if (string.IsNullOrWhiteSpace(revision) ||
            !string.Equals(revision, current.Revision, StringComparison.Ordinal))
            return (false, "engineering_operational_contract_changed", current);
        return (true, "engineering_operational_contract_current", current);
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
                SELECT [Revision],[Version],[ModelExecutionEnabled],[AutonomousEngineeringEnabled],
                       [HeadGptModel],[CodexModel],[ReviewerModel],
                       [SharedDirective],[HeadGptDirective],[CodexDirective],[ReviewerDirective],
                       [UpdatedUtc],[UpdatedBy]
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
        return WithContractTransactionAsync(async (connection, transaction) =>
        {
            var current = await ReadCurrentAsync(connection, transaction, cancellationToken) ?? Defaults();
            RequireExpected(current.Revision, expectedRevision);
            var normalized = Normalize(
                modelExecutionEnabled,
                current.AutonomousEngineeringEnabled,
                current.HeadGptModel,
                current.CodexModel,
                current.ReviewerModel,
                sharedDirective,
                headGptDirective,
                codexDirective,
                reviewerDirective,
                updatedBy);
            return await WriteAsync(connection, transaction, current, normalized, cancellationToken);
        }, cancellationToken);
    }

    public Task<LegendEngineeringOperationalContract> UpdateAllAsync(
        string expectedRevision,
        bool modelExecutionEnabled,
        bool autonomousEngineeringEnabled,
        string? headGptModel,
        string? codexModel,
        string? reviewerModel,
        string? sharedDirective,
        string? headGptDirective,
        string? codexDirective,
        string? reviewerDirective,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(
            modelExecutionEnabled,
            autonomousEngineeringEnabled,
            headGptModel,
            codexModel,
            reviewerModel,
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
                source.AutonomousEngineeringEnabled,
                source.HeadGptModel,
                source.CodexModel,
                source.ReviewerModel,
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
            true,
            AutoModel,
            AutoModel,
            AutoModel,
            DefaultSharedDirective,
            DefaultHeadGptDirective,
            DefaultCodexDirective,
            DefaultReviewerDirective,
            DateTime.UnixEpoch,
            "SYSTEM_DEFAULT");

    private static ContractInput Normalize(
        bool modelExecutionEnabled,
        bool autonomousEngineeringEnabled,
        string? headGptModel,
        string? codexModel,
        string? reviewerModel,
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

        return new(
            modelExecutionEnabled,
            autonomousEngineeringEnabled,
            NormalizeModel(headGptModel),
            NormalizeModel(codexModel),
            NormalizeModel(reviewerModel),
            shared,
            head,
            codex,
            reviewer,
            actor);
    }

    private static string NormalizeModel(string? value)
    {
        var model = string.IsNullOrWhiteSpace(value) ? AutoModel : value.Trim();
        if (string.Equals(model, AutoModel, StringComparison.OrdinalIgnoreCase))
            return AutoModel;
        if (model.Length > MaximumModelSlugCharacters ||
            model.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '.' or '-' or '_' or '/' or ':')))
            throw new InvalidOperationException("engineering_model_binding_invalid");
        return model;
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
        if (Equivalent(current, input))
            return current;

        var now = DateTime.UtcNow;
        var next = new LegendEngineeringOperationalContract(
            Guid.NewGuid().ToString("N"),
            checked(current.Version + 1),
            input.ModelExecutionEnabled,
            input.AutonomousEngineeringEnabled,
            input.HeadGptModel,
            input.CodexModel,
            input.ReviewerModel,
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
                    [AutonomousEngineeringEnabled]=@autonomous,[HeadGptModel]=@headModel,
                    [CodexModel]=@codexModel,[ReviewerModel]=@reviewerModel,
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
                    ([ContractKey],[Revision],[Version],[ModelExecutionEnabled],[AutonomousEngineeringEnabled],
                     [HeadGptModel],[CodexModel],[ReviewerModel],[SharedDirective],[HeadGptDirective],
                     [CodexDirective],[ReviewerDirective],[UpdatedUtc],[UpdatedBy])
                    VALUES ('founder-default',@revision,@version,@enabled,@autonomous,@headModel,@codexModel,
                            @reviewerModel,@shared,@head,@codex,@reviewer,@updated,@by)
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
                ([Revision],[Version],[ModelExecutionEnabled],[AutonomousEngineeringEnabled],
                 [HeadGptModel],[CodexModel],[ReviewerModel],[SharedDirective],[HeadGptDirective],
                 [CodexDirective],[ReviewerDirective],[UpdatedUtc],[UpdatedBy])
                VALUES (@revision,@version,@enabled,@autonomous,@headModel,@codexModel,@reviewerModel,
                        @shared,@head,@codex,@reviewer,@updated,@by)
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
            SELECT [Revision],[Version],[ModelExecutionEnabled],[AutonomousEngineeringEnabled],
                   [HeadGptModel],[CodexModel],[ReviewerModel],
                   [SharedDirective],[HeadGptDirective],[CodexDirective],[ReviewerDirective],
                   [UpdatedUtc],[UpdatedBy]
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
            SELECT [Revision],[Version],[ModelExecutionEnabled],[AutonomousEngineeringEnabled],
                   [HeadGptModel],[CodexModel],[ReviewerModel],
                   [SharedDirective],[HeadGptDirective],[CodexDirective],[ReviewerDirective],
                   [UpdatedUtc],[UpdatedBy]
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
            reader.GetBoolean(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetDateTime(11),
            reader.GetString(12));

    private static LegendEngineeringContractRevision ReadRevision(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetBoolean(2),
            reader.GetBoolean(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetDateTime(11),
            reader.GetString(12));

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

    private static bool Equivalent(
        LegendEngineeringOperationalContract current,
        ContractInput input) =>
        current.ModelExecutionEnabled == input.ModelExecutionEnabled &&
        current.AutonomousEngineeringEnabled == input.AutonomousEngineeringEnabled &&
        string.Equals(current.HeadGptModel, input.HeadGptModel, StringComparison.Ordinal) &&
        string.Equals(current.CodexModel, input.CodexModel, StringComparison.Ordinal) &&
        string.Equals(current.ReviewerModel, input.ReviewerModel, StringComparison.Ordinal) &&
        string.Equals(current.SharedDirective, input.SharedDirective, StringComparison.Ordinal) &&
        string.Equals(current.HeadGptDirective, input.HeadGptDirective, StringComparison.Ordinal) &&
        string.Equals(current.CodexDirective, input.CodexDirective, StringComparison.Ordinal) &&
        string.Equals(current.ReviewerDirective, input.ReviewerDirective, StringComparison.Ordinal);

    private static void BindCurrent(DbCommand command, LegendEngineeringOperationalContract value)
    {
        Add(command, "@revision", value.Revision);
        Add(command, "@version", value.Version);
        Add(command, "@enabled", value.ModelExecutionEnabled);
        Add(command, "@autonomous", value.AutonomousEngineeringEnabled);
        Add(command, "@headModel", value.HeadGptModel);
        Add(command, "@codexModel", value.CodexModel);
        Add(command, "@reviewerModel", value.ReviewerModel);
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
        bool AutonomousEngineeringEnabled,
        string HeadGptModel,
        string CodexModel,
        string ReviewerModel,
        string SharedDirective,
        string HeadGptDirective,
        string CodexDirective,
        string ReviewerDirective,
        string UpdatedBy);
}
