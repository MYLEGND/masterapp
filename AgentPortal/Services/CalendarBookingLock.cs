using System.Collections.Concurrent;
using System.Data;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

/// Serializes portal/mobile booking writes for an agent, including across SQL Server instances.
internal sealed class CalendarBookingLock : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private readonly MasterAppDbContext db;
    private readonly SemaphoreSlim gate;
    private readonly string resource;
    private bool sqlLocked;
    private bool openedConnection;
    private CalendarBookingLock(MasterAppDbContext db, SemaphoreSlim gate, string resource)
    { this.db = db; this.gate = gate; this.resource = resource; }

    public static async Task<CalendarBookingLock> AcquireAsync(MasterAppDbContext db, string agentId, CancellationToken ct)
    {
        var resource = "legend-calendar:" + agentId.Trim().ToLowerInvariant();
        var gate = Gates.GetOrAdd(resource, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        var lease = new CalendarBookingLock(db, gate, resource);
        try
        {
            if (db.Database.IsSqlServer())
            {
                lease.openedConnection = db.Database.GetDbConnection().State != ConnectionState.Open;
                if (lease.openedConnection) await db.Database.OpenConnectionAsync(ct);
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "DECLARE @result int; EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 15000; SELECT @result;";
                var parameter = command.CreateParameter(); parameter.ParameterName = "@resource"; parameter.Value = resource; command.Parameters.Add(parameter);
                if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) < 0) throw new TimeoutException("The calendar is processing another booking. Refresh available times and try again.");
                lease.sqlLocked = true;
            }
            return lease;
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (sqlLocked)
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';";
                var parameter = command.CreateParameter(); parameter.ParameterName = "@resource"; parameter.Value = resource; command.Parameters.Add(parameter);
                await command.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            try { if (openedConnection) await db.Database.CloseConnectionAsync(); }
            finally { gate.Release(); }
        }
    }
}
