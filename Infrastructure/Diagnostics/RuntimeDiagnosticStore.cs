using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private DateTime _nextPerformance;
    private long _recorded, _rateLimited, _failed, _samplesNotRetained;
    private DateTime? _lastRecorded;
    // Process-local pipeline health, not global/application-fleet completeness.
    public (long Recorded, long RateLimited, long Failed, DateTime? LastRecordedUtc, long SamplesNotRetained) CaptureStatus
    { get { lock (_admissionLock) return (_recorded, _rateLimited, _failed, _lastRecorded, _samplesNotRetained); } }


    public async Task RecordAsync(RuntimeDiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default)
    {
        if (!RuntimeDiagnosticSanitizer.IsBounded(diagnosticEvent)) return;
        if (diagnosticEvent.Performance is not null)
        {
            bool admitted;
            lock (_admissionLock)
            {
                admitted = DateTime.UtcNow >= _nextPerformance;
                if (admitted) _nextPerformance = DateTime.UtcNow.AddMinutes(1);
                else _rateLimited++;
            }
            if (!admitted)
            {
                // Existing error capture survives a rejected performance sample.
                if (diagnosticEvent.ErrorName == "SlowOperation") return;
                diagnosticEvent = diagnosticEvent with { Performance = null };
            }
        }
        if (!Admit("server")) { lock (_admissionLock) _rateLimited++; return; }
        try { await PersistAsync(diagnosticEvent, server: true, cancellationToken); }
        catch { lock (_admissionLock) _failed++; throw; }
        lock (_admissionLock) { _recorded++; _lastRecorded = DateTime.UtcNow; }
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

    private static DateTime? SampleTime(string? json)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > RuntimeDiagnosticSanitizer.MaximumPerformanceBytes) return null;
        try
        {
            using var sample = JsonDocument.Parse(json);
            return sample.RootElement.ValueKind == JsonValueKind.Object && sample.RootElement.TryGetProperty("sampledUtc", out var time) && time.ValueKind == JsonValueKind.String && time.TryGetDateTime(out var value)
                ? value.ToUniversalTime() : null;
        }
        catch (JsonException) { return null; }
    }

    // Compare-and-swap only the bounded sample. Never overwrite a concurrent
    // winner, and never replace newer source time with an older arrival. There
    // is no retry loop; an unretained contending sample is not called latest.
    internal static async Task<bool> RetainNewerPerformanceAsync(MasterAppDbContext db, string key,
        string sample, CancellationToken token = default)
    {
        if (SampleTime(sample) is not { } incoming) return false;
        var old = await db.RuntimeDiagnosticIncidents.Where(row => row.DeduplicationKey == key)
            .Select(row => row.PerformanceEvidenceJson).SingleAsync(token);
        if (old == sample) return true;
        if (SampleTime(old) is { } retained && incoming <= retained) return false;
        return await db.RuntimeDiagnosticIncidents.Where(row => row.DeduplicationKey == key && row.PerformanceEvidenceJson == old)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.PerformanceEvidenceJson, sample)
                .SetProperty(row => row.ReviewVersion, row => row.ReviewVersion + 1)
                .SetProperty(row => row.Recurred, row => row.Recurred || row.Disposition == "ManuallyClosed"), token) == 1;
    }

    private async Task PersistAsync(RuntimeDiagnosticEvent value, bool server, CancellationToken callerToken)
    {
        using var suppression = RuntimeDiagnosticScope.Suppress();
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
            else { existing.Occurrences++; existing.ReviewVersion++; existing.LastSeenUtc = now; existing.ExpiresUtc = now.AddDays(30); existing.Recurred |= existing.Disposition == "ManuallyClosed";
                if (SampleTime(incident.PerformanceEvidenceJson) is { } incoming &&
                    (SampleTime(existing.PerformanceEvidenceJson) is not { } retained || incoming > retained))
                    existing.PerformanceEvidenceJson = incident.PerformanceEvidenceJson; }
            await db.SaveChangesAsync(token);
        }
        if (db.Database.IsRelational() && incident.PerformanceEvidenceJson is not null &&
            !await RetainNewerPerformanceAsync(db, incident.DeduplicationKey, incident.PerformanceEvidenceJson, token))
            lock (_admissionLock) _samplesNotRetained++;
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
