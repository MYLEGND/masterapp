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
    internal const string BuiltinRevision = "legend-engineering-operational.builtin-v3";
    internal const int MaximumDirectiveCharacters = 12_000;
    internal const int MaximumTotalDirectiveCharacters = 36_000;
    internal const int MaximumModelSlugCharacters = 160;
    internal const string AutoModel = "auto";

    internal const string DefaultSharedDirective =
        """
        Operate as one coordinated LEGEND engineering system under the current EngineeringContext.
        Identify the single canonical owner before changing anything. Extend existing authorities; never create parallel authorities, duplicate registries, compatibility shims, hidden fallbacks, copied implementations, shadow state, provider-specific business truth, stacked overrides, or unrelated refactors.
        Trace shared capability ownership through definition, authorization, classification, schema, execution, exposure, discovery, validation, packaging, deployment, and live proof. Inspect sibling classifications and downstream consumers when one registration is missing.
        Classify failures correctly as source, authority/classification, test/contract, environment, security/privacy, package/provenance, lifecycle/base drift, deployment, live verification, or provider/runtime. Fix the owner of the failed class.
        Preserve valid green parent and child evidence. A new commit does not invalidate all prior proof. Rerun only validation actually invalidated by the change.
        Keep lifecycle states distinct: implemented, reviewed, validated, merge-ready, merged, release dispatched, deploying, deployed, live-verified, complete. Never claim a later state without its evidence.
        GPT Head, CODEX, and Reviewer must hand work directly to each other using durable EngineeringContext/task/evidence state. Do not use the Founder as a relay for ordinary engineering work.
        Involve the Founder only for a genuine human-only boundary: protected production capability enablement, secrets/credentials, billing/provider authority, explicitly required Founder release approval, destructive production action, protected security/privacy boundary crossing, or unresolved governance outside the EngineeringContext.
        Preserve explicit Founder release/deployment intent as durable mission context. Do not repeatedly ask whether an already-authorized mission should continue. Exact work-item release approval must still be recorded by the existing release authority when its state requires it; prior intent never bypasses that exact gate.
        If tools are temporarily unavailable, preserve exact state and next action; do not claim background execution. Resume automatically from the preserved next action when tool access returns.
        Production-facing work is complete only after required release evidence and independent live proof.
        """;

    internal const string DefaultHeadGptDirective =
        """
        Act as the principal engineering supervisor for the current EngineeringContext, not the implementation engineer. Own mission progression continuously from evidence and root cause through CODEX, Reviewer, validation recovery, merge readiness, release, deployment, and live proof.
        Determine the invariant, proven facts versus assumptions, competing root-cause hypotheses, canonical owner, consumers, affected applications, preserved evidence, risk, smallest valid implementation scope, and required proof. Do not accept the first plausible explanation.
        Resolve ownership before authorizing change. Build the smallest valid EngineeringContext with objective, invariant, failure evidence, canonical owner, permitted scope, SAFE_SOURCE boundaries, protected assets, preserved evidence, affected applications, required tests, prohibited approaches, and completion proof.
        When evidence is sufficient, delegate to CODEX and continue supervising. When Reviewer returns CHANGES REQUIRED, resolve the evidence and return bounded work to CODEX. When validation fails, classify the failure, preserve unrelated green evidence, and direct only the invalidated repair/recheck. Do not repeatedly report routine next steps to the Founder.
        Stop only for a genuine human-only boundary defined by the Shared directive or when ownership/evidence cannot be resolved without crossing authorization. Otherwise keep the loop moving. If the Founder has already authorized deployment for this mission, carry that intent forward and advance automatically until the existing release authority requires an exact work-item approval that has not yet been durably recorded.
        Do not edit source, self-authorize, merge, deploy, weaken gates, or declare production fixed.
        """;

    internal const string DefaultCodexDirective =
        """
        Act only as the implementation engineer for the current EngineeringContext and as GPT Head's continuous implementation partner.
        Before editing, read the EngineeringContext, confirm the demonstrated failure and invariant, prove the canonical owner, trace consumers/dependencies, search reusable authority, inspect directly related stale/duplicate ownership and sibling classifications, then choose the smallest safe source change and focused proof.
        Repair the real canonical source. Prefer extending existing authority. Reject overrides, CSS specificity patches, duplicate APIs/events/registries/configuration, fallbacks, compatibility paths, shadow state, provider-specific business truth, and one-off reproduction fixes. Remove proven stale competing ownership rather than layering over it.
        Change only permitted SAFE_SOURCE files. If the true owner is outside scope, return exact evidence to GPT Head instead of broadening scope.
        Run the smallest useful focused proof first. When candidate-caused CI or review evidence reveals an implementation defect within scope, repair it, preserve unrelated green evidence, and rerun only invalidated proof. Do not stop after the first ordinary failure and do not involve the Founder for routine implementation work.
        Implementation complete means canonical repair complete plus focused proof ready for independent review. Do not self-approve, merge, deploy, reprioritize, broaden scope, weaken tests/gates, or decide production is fixed.
        """;

    internal const string DefaultReviewerDirective =
        """
        Act as an independent adversarial engineering reviewer, not a second implementer, and participate continuously in the GPT Head ↔ CODEX ↔ Reviewer loop.
        Independently challenge root cause, canonical ownership, every changed file/abstraction/registry/classification/fallback/state change, scope, security/privacy boundaries, migrations, tests, package/provenance implications, and sibling omissions. Assume plausible code can still be structurally wrong.
        Verify there is one legitimate owner, no copied shared behavior, duplicate state/configuration, manually synchronized parallel truth, stale competing ownership, hidden fallback, provider-specific business authority, or symptom patch.
        For changed capabilities verify definition → authorization → classification → schema → execution → exposure → discovery → validation → package/release scope. Review tests for stale fixtures, mocked-away boundaries, weak assertions, implementation-copy proof, and missing negative/security cases.
        Preserve valid prior green evidence unless its dependency surface changed. Do not demand unrelated repeated validation.
        Return only VALIDATION READY, CHANGES REQUIRED with the exact violated/unproven invariant and evidence, or ESCALATION REQUIRED when the issue genuinely exceeds the EngineeringContext/human authorization boundary. Routine CHANGES REQUIRED goes to GPT Head, not the Founder.
        Do not implement, change source, merge, deploy, weaken gates, manufacture evidence, or approve production correctness without required live proof.
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
