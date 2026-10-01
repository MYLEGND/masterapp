using System.Data;
using System.Data.Common;
using System.Text.Json;
using Domain.Engineering;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services.Engineering;

internal sealed class LegendEngineeringStateStore(MasterAppDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal async Task<EngineeringWorkItemSnapshot> AttachIncidentAsync(
        RuntimeDiagnosticIncident incident,
        EngineeringPolicyDecision decision,
        CancellationToken cancellationToken)
    {
        var liveSha = incident.ReleaseVerified && LegendEngineeringPolicies.IsImmutableSha(incident.GitCommitHash)
            ? incident.GitCommitHash!.ToLowerInvariant()
            : "0000000000000000000000000000000000000000";
        var workKey = LegendEngineeringPolicies.ComputeWorkKey(decision.CanonicalAuthorityKey, liveSha);
        return await WithSerializedLeaseAuthorityAsync(async (connection, transaction) =>
        {
            var existing = await ReadWorkItemByKeyAsync(connection, transaction, workKey, cancellationToken);
            var now = DateTime.UtcNow;
            if (existing is not null)
            {
                var incidentIds = existing.IncidentIds.Contains(incident.Id)
                    ? existing.IncidentIds
                    : existing.IncidentIds.Concat([incident.Id]).OrderBy(id => id).ToArray();
                var evidenceRevision = LegendEngineeringPolicies.EvidenceRevision(incident, incidentIds);
                var state = existing.State is "COMPLETED" or "CLOSED"
                    ? InitialState(decision, recurring: true)
                    : existing.State;
                var preserveWorkflowRole = existing.State is
                    "LEASED" or "AGENT_ACTIVE" or "CANDIDATE_PREPARED" or "REVIEW_REQUIRED" or
                    "REVIEWED" or "VALIDATED" or "RELEASE_REQUESTED" or
                    "FOUNDER_RELEASE_APPROVAL_REQUIRED" or "CI_FAILED_NEEDS_EVIDENCE" or
                    "REVIEW_REJECTED" or "RELEASE_BLOCKED" or "FOUNDER_ESCALATION";
                var updated = existing with
                {
                    IncidentIds = incidentIds,
                    EvidenceRevision = evidenceRevision,
                    Severity = Math.Max(existing.Severity, decision.Severity),
                    RevenueImpact = Math.Max(existing.RevenueImpact, decision.RevenueImpact),
                    UserImpact = Math.Max(existing.UserImpact, decision.UserImpact),
                    Frequency = Math.Max(existing.Frequency, decision.Frequency),
                    Confidence = Math.Max(existing.Confidence, decision.Confidence),
                    PriorityScore = Math.Max(existing.PriorityScore, decision.PriorityScore),
                    PriorityClass = MoreUrgent(existing.PriorityClass, decision.PriorityClass),
                    State = state,
                    AssignedRole = preserveWorkflowRole ? existing.AssignedRole : decision.AssignedRole,
                    ModelTier = preserveWorkflowRole ? existing.ModelTier : decision.ModelTier,
                    LeaseOwner = state == existing.State ? existing.LeaseOwner : null,
                    LeaseIdentity = state == existing.State ? existing.LeaseIdentity : null,
                    LeaseExpiresUtc = state == existing.State ? existing.LeaseExpiresUtc : null,
                    ValidationState = state == existing.State ? existing.ValidationState : "NOT_STARTED",
                    ReleaseCohort = decision.ReleaseCohort,
                    UpdatedUtc = now
                };
                await UpdateWorkItemAsync(connection, transaction, updated, cancellationToken);
                return updated;
            }

            var snapshot = new EngineeringWorkItemSnapshot(
                Guid.NewGuid(),
                [incident.Id],
                workKey,
                decision.CanonicalAuthorityKey,
                decision.AffectedProjects,
                decision.AffectedApplications,
                decision.ImpactSet,
                liveSha,
                LegendEngineeringPolicies.EvidenceRevision(incident),
                decision.FailureClass,
                decision.Severity,
                decision.RevenueImpact,
                decision.UserImpact,
                decision.Frequency,
                decision.Confidence,
                RiskScore(decision.RiskClass),
                decision.RiskClass,
                decision.ComplexityScore,
                decision.PriorityScore,
                decision.PriorityClass,
                InitialState(decision, recurring: false),
                decision.AssignedRole,
                decision.ModelTier,
                null,
                null,
                null,
                0,
                null,
                null,
                null,
                "NOT_STARTED",
                decision.ReleaseCohort,
                now,
                now);

            await InsertWorkItemAsync(connection, transaction, snapshot, cancellationToken);
            return snapshot;
        }, cancellationToken);
    }

    internal async Task<EngineeringLeaseReceipt> TryAcquireLeaseAsync(
        Guid workItemId,
        string leaseOwner,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner) || leaseOwner.Length > 128)
            return new(false, "lease_owner_invalid", workItemId, null, null, null, null);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(30))
            return new(false, "lease_duration_invalid", workItemId, null, null, null, null);

        return await WithSerializedLeaseAuthorityAsync(async (connection, transaction) =>
        {
            var item = await ReadWorkItemAsync(connection, transaction, workItemId, cancellationToken);
            if (item is null)
                return new EngineeringLeaseReceipt(false, "work_item_not_found", workItemId, null, null, null, null);
            if (item.State is "COMPLETED" or "CLOSED" or "SECURITY_REVIEW" or "OBSERVATION_ONLY")
                return new(false, "work_item_not_leaseable", workItemId, null, null, null, null);

            var now = DateTime.UtcNow;
            if (item.LeaseExpiresUtc > now)
            {
                if (string.Equals(item.LeaseOwner, leaseOwner, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(item.LeaseIdentity))
                    return new(true, "lease_replayed", workItemId, item.LeaseOwner, item.LeaseIdentity, item.LeaseExpiresUtc, null);
                return new(false, "work_item_already_leased", workItemId, item.LeaseOwner, item.LeaseIdentity, item.LeaseExpiresUtc, workItemId);
            }

            var active = await ReadActiveLeasesAsync(connection, transaction, workItemId, now, cancellationToken);
            foreach (var other in active)
            {
                if (!LegendEngineeringPolicies.ImpactSetsOverlap(item.ImpactSet, other.ImpactSet)) continue;
                return new(false, "overlapping_impact_set_leased", workItemId, other.LeaseOwner, other.LeaseIdentity, other.LeaseExpiresUtc, other.WorkItemId);
            }

            var identity = Guid.NewGuid().ToString("N");
            var expires = now.Add(duration);
            var leased = item with
            {
                State = "LEASED",
                LeaseOwner = leaseOwner,
                LeaseIdentity = identity,
                LeaseExpiresUtc = expires,
                UpdatedUtc = now
            };
            await UpdateWorkItemAsync(connection, transaction, leased, cancellationToken);
            return new(true, "lease_acquired", workItemId, leaseOwner, identity, expires, null);
        }, cancellationToken);
    }

    internal async Task<EngineeringContextSnapshot> SaveContextAsync(
        EngineeringContextSnapshot context,
        CancellationToken cancellationToken)
    {
        var valid = await ValidateLeaseBindingAsync(context.WorkItemId, context.LeaseIdentity, context.LiveSha,
            context.EvidenceRevision, context.RiskClass, cancellationToken);
        if (!valid.Valid)
            throw new InvalidOperationException(valid.Code);

        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO [LegendEngineeringContexts]
                ([EngineeringContextId],[WorkItemId],[ContractRevision],[PolicyRevision],[Role],[LiveSha],
                 [EvidenceRevision],[LeaseIdentity],[SnapshotJson],[CreatedUtc],[ExpiresUtc])
                VALUES (@id,@work,@contract,@policy,@role,@live,@evidence,@lease,@snapshot,@created,@expires)
                """;
            Add(command, "@id", context.EngineeringContextId);
            Add(command, "@work", context.WorkItemId);
            Add(command, "@contract", context.ContractRevision);
            Add(command, "@policy", context.PolicyRevision);
            Add(command, "@role", context.Role);
            Add(command, "@live", context.LiveSha);
            Add(command, "@evidence", context.EvidenceRevision);
            Add(command, "@lease", context.LeaseIdentity);
            Add(command, "@snapshot", JsonSerializer.Serialize(context, JsonOptions));
            Add(command, "@created", context.CreatedUtc);
            Add(command, "@expires", context.ExpiresUtc);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("engineering_context_not_persisted");
            return context;
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    internal async Task<(bool Valid, string Code, EngineeringContextSnapshot? Context)> ValidateContextAsync(
        Guid contextId,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT [SnapshotJson] FROM [LegendEngineeringContexts] WHERE [EngineeringContextId]=@id";
            Add(command, "@id", contextId);
            var raw = await command.ExecuteScalarAsync(cancellationToken) as string;
            if (string.IsNullOrWhiteSpace(raw)) return (false, "engineering_context_not_found", null);
            var context = JsonSerializer.Deserialize<EngineeringContextSnapshot>(raw, JsonOptions);
            if (context is null) return (false, "engineering_context_invalid", null);
            if (context.ExpiresUtc <= DateTime.UtcNow) return (false, "engineering_context_expired", context);
            if (!string.Equals(context.ContractRevision, LegendEngineeringContract.ContractRevision, StringComparison.Ordinal) ||
                !string.Equals(context.PolicyRevision, LegendEngineeringContract.PolicyRevision, StringComparison.Ordinal))
                return (false, "engineering_context_revision_changed", context);

            var binding = await ValidateLeaseBindingOnConnectionAsync(connection, null, context.WorkItemId, context.LeaseIdentity,
                context.LiveSha, context.EvidenceRevision, context.RiskClass, cancellationToken);
            return binding.Valid ? (true, "engineering_context_valid", context) : (false, binding.Code, context);
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    internal async Task<EngineeringWorkItemSnapshot?> GetWorkItemAsync(Guid workItemId, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try { return await ReadWorkItemAsync(connection, null, workItemId, cancellationToken); }
        finally { if (opened) await connection.CloseAsync(); }
    }

    internal async Task<IReadOnlyList<EngineeringWorkItemSnapshot>> GetOpenWorkItemsAsync(int maximum, CancellationToken cancellationToken)
    {
        maximum = Math.Clamp(maximum, 1, 500);
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT [SnapshotJson] FROM [LegendEngineeringWorkItems]
                WHERE [State] NOT IN ('COMPLETED','CLOSED')
                ORDER BY [PriorityScore] DESC, [UpdatedUtc] ASC
                """;
            var result = new List<EngineeringWorkItemSnapshot>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (result.Count < maximum && await reader.ReadAsync(cancellationToken))
            {
                var item = JsonSerializer.Deserialize<EngineeringWorkItemSnapshot>(reader.GetString(0), JsonOptions);
                if (item is not null) result.Add(item);
            }
            return result;
        }
        finally { if (opened) await connection.CloseAsync(); }
    }

    internal async Task RecordUsageAsync(EngineeringUsageObservation usage, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO [LegendEngineeringUsage]
                ([UsageId],[WorkItemId],[ModelTier],[Role],[Provider],[SessionId],[InputTokens],[OutputTokens],
                 [TotalTokens],[CostMicrousd],[UsageObserved],[CreatedUtc])
                VALUES (@id,@work,@tier,@role,@provider,@session,@input,@output,@total,@cost,@observed,@created)
                """;
            Add(command, "@id", usage.UsageId);
            Add(command, "@work", usage.WorkItemId);
            Add(command, "@tier", usage.ModelTier);
            Add(command, "@role", usage.Role);
            Add(command, "@provider", usage.Provider);
            Add(command, "@session", usage.SessionId);
            Add(command, "@input", usage.InputTokens);
            Add(command, "@output", usage.OutputTokens);
            Add(command, "@total", usage.TotalTokens);
            Add(command, "@cost", usage.CostMicrousd);
            Add(command, "@observed", usage.UsageObserved);
            Add(command, "@created", usage.CreatedUtc);
            try { await command.ExecuteNonQueryAsync(cancellationToken); }
            catch (DbException)
            {
                // UsageId is the durable engineering-context attempt identity.
                // A later terminal observation updates the same attempt instead
                // of counting another model start.
                await using var update = connection.CreateCommand();
                update.CommandText = """
                    UPDATE [LegendEngineeringUsage] SET
                      [SessionId]=COALESCE(@session,[SessionId]),
                      [InputTokens]=COALESCE(@input,[InputTokens]),
                      [OutputTokens]=COALESCE(@output,[OutputTokens]),
                      [TotalTokens]=COALESCE(@total,[TotalTokens]),
                      [CostMicrousd]=COALESCE(@cost,[CostMicrousd]),
                      [UsageObserved]=CASE WHEN @observed=1 THEN 1 ELSE [UsageObserved] END
                    WHERE [UsageId]=@id
                    """;
                Add(update, "@id", usage.UsageId);
                Add(update, "@session", usage.SessionId);
                Add(update, "@input", usage.InputTokens);
                Add(update, "@output", usage.OutputTokens);
                Add(update, "@total", usage.TotalTokens);
                Add(update, "@cost", usage.CostMicrousd);
                Add(update, "@observed", usage.UsageObserved);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally { if (opened) await connection.CloseAsync(); }
    }

    internal async Task<int> CountModelAttemptsAsync(
        Guid workItemId,
        string role,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM [LegendEngineeringUsage]
                WHERE [WorkItemId]=@work AND [Role]=@role
                """;
            Add(command, "@work", workItemId);
            Add(command, "@role", role);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }
        finally { if (opened) await connection.CloseAsync(); }
    }

    internal async Task<(long DailyTokens, long MonthlyTokens, bool UsageEvidenceComplete)> ReadUsageTotalsAsync(
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var day = nowUtc.Date;
        var month = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            async Task<(long Tokens, bool Complete)> ReadAsync(DateTime since)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT COALESCE(SUM([TotalTokens]),0),
                           CASE WHEN SUM(CASE WHEN [UsageObserved]=0 OR [TotalTokens] IS NULL THEN 1 ELSE 0 END) > 0 THEN 0 ELSE 1 END
                    FROM [LegendEngineeringUsage] WHERE [CreatedUtc] >= @since
                    """;
                Add(command, "@since", since);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) return (0, true);
                return (Convert.ToInt64(reader.GetValue(0)), Convert.ToInt32(reader.GetValue(1)) == 1);
            }
            var daily = await ReadAsync(day);
            var monthly = await ReadAsync(month);
            return (daily.Tokens, monthly.Tokens, daily.Complete && monthly.Complete);
        }
        finally { if (opened) await connection.CloseAsync(); }
    }

    internal async Task UpdateWorkItemAsync(
        EngineeringWorkItemSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await WithSerializedLeaseAuthorityAsync(async (connection, transaction) =>
        {
            var current = await ReadWorkItemAsync(connection, transaction, snapshot.WorkItemId, cancellationToken)
                ?? throw new InvalidOperationException("work_item_not_found");
            if (!string.Equals(current.EvidenceRevision, snapshot.EvidenceRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("work_item_evidence_changed");
            if (current.UpdatedUtc != snapshot.UpdatedUtc)
                throw new InvalidOperationException("work_item_state_changed");
            await UpdateWorkItemAsync(connection, transaction, snapshot with { UpdatedUtc = DateTime.UtcNow }, cancellationToken);
            return true;
        }, cancellationToken);
    }

    private async Task<(bool Valid, string Code)> ValidateLeaseBindingAsync(
        Guid workItemId, string leaseIdentity, string liveSha, string evidenceRevision, string riskClass,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            return await ValidateLeaseBindingOnConnectionAsync(connection, null, workItemId, leaseIdentity,
                liveSha, evidenceRevision, riskClass, cancellationToken);
        }
        finally { if (opened) await connection.CloseAsync(); }
    }

    private static async Task<(bool Valid, string Code)> ValidateLeaseBindingOnConnectionAsync(
        DbConnection connection, DbTransaction? transaction, Guid workItemId, string leaseIdentity, string liveSha,
        string evidenceRevision, string riskClass, CancellationToken cancellationToken)
    {
        var item = await ReadWorkItemAsync(connection, transaction, workItemId, cancellationToken);
        if (item is null) return (false, "work_item_not_found");
        if (item.State is "COMPLETED" or "CLOSED") return (false, "work_item_completed");
        if (item.LeaseExpiresUtc <= DateTime.UtcNow || string.IsNullOrWhiteSpace(item.LeaseIdentity))
            return (false, "engineering_lease_expired");
        if (!string.Equals(item.LeaseIdentity, leaseIdentity, StringComparison.Ordinal))
            return (false, "engineering_lease_changed");
        if (!string.Equals(item.LiveSha, liveSha, StringComparison.OrdinalIgnoreCase))
            return (false, "live_sha_changed");
        if (!string.Equals(item.EvidenceRevision, evidenceRevision, StringComparison.Ordinal))
            return (false, "evidence_revision_changed");
        if (!string.Equals(item.RiskClass, riskClass, StringComparison.Ordinal))
            return (false, "risk_class_changed");
        return (true, "lease_binding_valid");
    }

    private async Task<T> WithSerializedLeaseAuthorityAsync<T>(
        Func<DbConnection, DbTransaction, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            try
            {
                await using (var lockCommand = connection.CreateCommand())
                {
                    lockCommand.Transaction = transaction;
                    lockCommand.CommandText = """
                        UPDATE [LegendEngineeringControlLocks]
                        SET [Revision]=[Revision]+1
                        WHERE [LockKey]='lease-authority'
                        """;
                    if (await lockCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
                        throw new InvalidOperationException("engineering_lease_authority_unavailable");
                }

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
        finally { if (opened) await connection.CloseAsync(); }
    }

    private static async Task<EngineeringWorkItemSnapshot?> ReadWorkItemByKeyAsync(
        DbConnection connection, DbTransaction? transaction, string workKey, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT [SnapshotJson] FROM [LegendEngineeringWorkItems] WHERE [WorkKey]=@key";
        Add(command, "@key", workKey);
        return Deserialize(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<EngineeringWorkItemSnapshot?> ReadWorkItemAsync(
        DbConnection connection, DbTransaction? transaction, Guid workItemId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT [SnapshotJson] FROM [LegendEngineeringWorkItems] WHERE [WorkItemId]=@id";
        Add(command, "@id", workItemId);
        return Deserialize(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<IReadOnlyList<EngineeringWorkItemSnapshot>> ReadActiveLeasesAsync(
        DbConnection connection, DbTransaction transaction, Guid excluding, DateTime now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT [SnapshotJson] FROM [LegendEngineeringWorkItems]
            WHERE [WorkItemId]<>@id AND [LeaseExpiresUtc] IS NOT NULL AND [LeaseExpiresUtc]>@now
              AND [State] NOT IN ('COMPLETED','CLOSED')
            """;
        Add(command, "@id", excluding);
        Add(command, "@now", now);
        var result = new List<EngineeringWorkItemSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = JsonSerializer.Deserialize<EngineeringWorkItemSnapshot>(reader.GetString(0), JsonOptions);
            if (item is not null) result.Add(item);
        }
        return result;
    }

    private static async Task InsertWorkItemAsync(
        DbConnection connection, DbTransaction transaction, EngineeringWorkItemSnapshot item,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO [LegendEngineeringWorkItems]
            ([WorkItemId],[WorkKey],[CanonicalAuthorityKey],[ImpactSetJson],[LiveSha],[EvidenceRevision],
             [FailureClass],[RiskClass],[ComplexityScore],[PriorityScore],[State],[AssignedRole],
             [LeaseOwner],[LeaseIdentity],[LeaseExpiresUtc],[AttemptCount],[SnapshotJson],[CreatedUtc],[UpdatedUtc])
            VALUES (@id,@key,@authority,@impact,@live,@evidence,@failure,@risk,@complexity,@priority,@state,@role,
                    @owner,@lease,@expires,@attempts,@snapshot,@created,@updated)
            """;
        BindWorkItem(command, item);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("engineering_work_item_not_created");
    }

    private static async Task UpdateWorkItemAsync(
        DbConnection connection, DbTransaction transaction, EngineeringWorkItemSnapshot item,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE [LegendEngineeringWorkItems] SET
              [CanonicalAuthorityKey]=@authority,[ImpactSetJson]=@impact,[LiveSha]=@live,[EvidenceRevision]=@evidence,
              [FailureClass]=@failure,[RiskClass]=@risk,[ComplexityScore]=@complexity,[PriorityScore]=@priority,
              [State]=@state,[AssignedRole]=@role,[LeaseOwner]=@owner,[LeaseIdentity]=@lease,
              [LeaseExpiresUtc]=@expires,[AttemptCount]=@attempts,[SnapshotJson]=@snapshot,[UpdatedUtc]=@updated
            WHERE [WorkItemId]=@id
            """;
        BindWorkItem(command, item);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("engineering_work_item_update_lost");
    }

    private static void BindWorkItem(DbCommand command, EngineeringWorkItemSnapshot item)
    {
        Add(command, "@id", item.WorkItemId);
        Add(command, "@key", item.WorkKey);
        Add(command, "@authority", item.CanonicalAuthorityKey);
        Add(command, "@impact", JsonSerializer.Serialize(item.ImpactSet, JsonOptions));
        Add(command, "@live", item.LiveSha);
        Add(command, "@evidence", item.EvidenceRevision);
        Add(command, "@failure", item.FailureClass);
        Add(command, "@risk", item.RiskClass);
        Add(command, "@complexity", item.ComplexityScore);
        Add(command, "@priority", item.PriorityScore);
        Add(command, "@state", item.State);
        Add(command, "@role", item.AssignedRole);
        Add(command, "@owner", item.LeaseOwner);
        Add(command, "@lease", item.LeaseIdentity);
        Add(command, "@expires", item.LeaseExpiresUtc);
        Add(command, "@attempts", item.AttemptCount);
        Add(command, "@snapshot", JsonSerializer.Serialize(item, JsonOptions));
        Add(command, "@created", item.CreatedUtc);
        Add(command, "@updated", item.UpdatedUtc);
    }

    private static EngineeringWorkItemSnapshot? Deserialize(object? raw)
        => raw is string json && !string.IsNullOrWhiteSpace(json)
            ? JsonSerializer.Deserialize<EngineeringWorkItemSnapshot>(json, JsonOptions)
            : null;

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string InitialState(EngineeringPolicyDecision decision, bool recurring)
    {
        if (decision.RiskClass == EngineeringRiskClass.TierC) return "SECURITY_REVIEW";
        if (decision.FailureClass == EngineeringFailureClass.Unknown) return recurring ? "RECURRED_NEEDS_SUPERVISOR" : "NEEDS_SUPERVISOR";
        if (!decision.CodeRepairEligible) return "OBSERVATION_ONLY";
        return recurring ? "RECURRED" : "QUEUED";
    }

    private static int RiskScore(string riskClass) => riskClass switch
    {
        EngineeringRiskClass.TierA => 25,
        EngineeringRiskClass.TierB => 70,
        _ => 100
    };

    private static string MoreUrgent(string left, string right)
    {
        static int Rank(string value) => value switch { "P1" => 1, "P2" => 2, "P3" => 3, _ => 4 };
        return Rank(left) <= Rank(right) ? left : right;
    }
}
