using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shared.Diagnostics;

namespace Infrastructure.Diagnostics;

public enum RuntimeDiagnosticAdmissionResult { Recorded, RateLimited }

// The singleton owns only bounded, transient admission counters. Every durable
// operation uses a fresh DbContext so diagnostics cannot save failed app writes.
public sealed class RuntimeDiagnosticStore(IServiceScopeFactory scopes, IHostEnvironment environment,
    IHttpContextAccessor contexts, IEnumerable<EndpointDataSource> endpoints) : IRuntimeDiagnosticSink
{
    private readonly object _admissionLock = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(32);
    private DateTime _window;
    private DateTime _nextRetention;
    private int _globalCount;

    public async Task RecordAsync(RuntimeDiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default)
    {
        if (!RuntimeDiagnosticSanitizer.IsBounded(diagnosticEvent)) return;
        if (!Admit("server")) return;
        await PersistAsync(diagnosticEvent, server: true, cancellationToken);
    }

    public async Task<RuntimeDiagnosticAdmissionResult> RecordClientAsync(RuntimeDiagnosticEvent diagnosticEvent,
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        if (!RuntimeDiagnosticSanitizer.IsBounded(diagnosticEvent))
            throw new ArgumentException("Diagnostic payload exceeds its contract.", nameof(diagnosticEvent));
        var subject = user.Identity?.IsAuthenticated == true
            ? "actor:" + (user.FindFirst("oid")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value ?? "authenticated")
            : "network:" + (contexts.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        var key = Convert.ToHexString(HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(subject)));
        if (!Admit(key)) return RuntimeDiagnosticAdmissionResult.RateLimited;
        await PersistAsync(diagnosticEvent, server: false, cancellationToken);
        return RuntimeDiagnosticAdmissionResult.Recorded;
    }

    // GitHub identity and merged ancestry are authenticated by the existing
    // Founder remediation reader before this call. The diagnostics store remains
    // the sole writer of all RuntimeDiagnosticIncidents. This is observation only.
    public async Task<bool> RecordAuthenticatedReleaseFailureAsync(
        long runId, int sourcePullRequest, string candidateSha, string authoritySha,
        string stage, CancellationToken cancellationToken = default)
    {
        var incident = BuildAuthenticatedReleaseFailure(
            runId, sourcePullRequest, candidateSha, authoritySha, stage, DateTime.UtcNow);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        if (await db.RuntimeDiagnosticIncidents.AsNoTracking()
            .AnyAsync(row => row.DeduplicationKey == incident.DeduplicationKey, cancellationToken))
            return false;

        db.RuntimeDiagnosticIncidents.Add(incident);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            db.Entry(incident).State = EntityState.Detached;
            // Only a proven concurrent insertion of the identical fingerprint
            // is a duplicate. All unrelated write failures remain failures.
            if (await db.RuntimeDiagnosticIncidents.AsNoTracking()
                .AnyAsync(row => row.DeduplicationKey == incident.DeduplicationKey, cancellationToken))
                return false;
            throw;
        }
    }

    public static RuntimeDiagnosticIncident BuildAuthenticatedReleaseFailure(
        long runId, int sourcePullRequest, string candidateSha, string authoritySha,
        string stage, DateTime observedUtc)
    {
        static bool Sha(string value) =>
            value is { Length: 40 } &&
            value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
        if (runId <= 0 || sourcePullRequest <= 0 || !Sha(candidateSha) ||
            !Sha(authoritySha) || stage is not (
                "PREPUBLICATION" or "LIVE_BASE" or "TRANSACTION_PREPARE" or
                "TRANSACTION_RECONCILE" or "LIVE_PROOF" or "UNCLASSIFIED"))
            throw new ArgumentException("Invalid authenticated release observation identity.");

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"release-observation:{runId}:{candidateSha}:{authoritySha}:{stage}"))).ToLowerInvariant();
        return new RuntimeDiagnosticIncident
        {
            Id = Guid.NewGuid(),
            DeduplicationKey = fingerprint,
            AppIdentifier = "FounderRelease",
            Platform = "Release",
            Route = $"/founder/release/{sourcePullRequest}",
            ErrorName = "RELEASE_" + stage,
            Category = "ReleaseObservation",
            Summary = $"Verified protected release run {runId} failed at {stage}. Candidate not verified live.",
            CorrelationId = runId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            GitCommitHash = candidateSha,
            ReleaseVerified = false,
            SourceFilePath = null,
            FirstSeenUtc = observedUtc,
            LastSeenUtc = observedUtc,
            ExpiresUtc = observedUtc.AddDays(30)
        };
    }

    private bool Admit(string key)
    {
        lock (_admissionLock)
        {
            var now = DateTime.UtcNow;
            if (now >= _window)
            {
                _counts.Clear(); _globalCount = 0; _window = now.AddMinutes(1);
            }
            if (_globalCount >= 300 || _counts.GetValueOrDefault(key) >= (key == "server" ? 100 : 20)) return false;
            _counts[key] = _counts.GetValueOrDefault(key) + 1;
            _globalCount++;
            return true;
        }
    }

    private async Task PersistAsync(RuntimeDiagnosticEvent value, bool server, CancellationToken callerToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var token = deadline.Token;
        var now = DateTime.UtcNow;
        var incident = RuntimeDiagnosticSanitizer.Sanitize(value, server, environment, contexts.HttpContext,
            endpoints.ToArray(), now);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        if (db.Database.IsRelational())
        {
            var updated = await db.RuntimeDiagnosticIncidents.Where(row => row.DeduplicationKey == incident.DeduplicationKey)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Occurrences, row => row.Occurrences + 1)
                    .SetProperty(row => row.ReviewVersion, row => row.ReviewVersion + 1)
                    .SetProperty(row => row.Recurred, row => row.Recurred || row.Disposition == "ManuallyClosed")
                    .SetProperty(row => row.ExpiresUtc, now.AddDays(30))
                    .SetProperty(row => row.LastSeenUtc, now), token);
            if (updated == 0)
            {
                db.RuntimeDiagnosticIncidents.Add(incident);
                try { await db.SaveChangesAsync(token); }
                catch (DbUpdateException)
                {
                    db.Entry(incident).State = EntityState.Detached;
                    // A concurrent insert may win the unique release fingerprint.
                    // A missing winner keeps the original database failure explicit.
                    if (await db.RuntimeDiagnosticIncidents.Where(row => row.DeduplicationKey == incident.DeduplicationKey)
                        .ExecuteUpdateAsync(set => set.SetProperty(row => row.Occurrences, row => row.Occurrences + 1)
                            .SetProperty(row => row.ReviewVersion, row => row.ReviewVersion + 1)
                            .SetProperty(row => row.Recurred, row => row.Recurred || row.Disposition == "ManuallyClosed")
                            .SetProperty(row => row.ExpiresUtc, now.AddDays(30))
                            .SetProperty(row => row.LastSeenUtc, now), token) == 0) throw;
                }
            }
        }
        else
        {
            var existing = await db.RuntimeDiagnosticIncidents.SingleOrDefaultAsync(row => row.DeduplicationKey == incident.DeduplicationKey, token);
            if (existing is null) db.RuntimeDiagnosticIncidents.Add(incident);
            else { existing.Occurrences++; existing.ReviewVersion++; existing.LastSeenUtc = now; existing.ExpiresUtc = now.AddDays(30); existing.Recurred |= existing.Disposition == "ManuallyClosed"; }
            await db.SaveChangesAsync(token);
        }
        bool prune;
        lock (_admissionLock)
        {
            prune = now >= _nextRetention;
            if (prune) _nextRetention = now.AddMinutes(1);
        }
        if (prune)
        {
            var expired = db.RuntimeDiagnosticIncidents.Where(row => row.ExpiresUtc <= now)
                .OrderBy(row => row.ExpiresUtc).Take(500);
            if (db.Database.IsRelational()) await expired.ExecuteDeleteAsync(token);
            else { db.RemoveRange(await expired.ToListAsync(token)); await db.SaveChangesAsync(token); }
        }
    }
}
