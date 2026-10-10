using System.Data;
using System.Data.Common;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Legend.Commerce;

/// <summary>
/// A shared SQL Server session lock prevents two host instances sending the
/// same automation batch concurrently. No SQL schema or customer data changes.
/// A local lock never authorizes cross-host notification delivery.
/// </summary>
public interface ICommerceAutomationDispatchLease
{
    Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken = default);
}

public sealed class SqlCommerceAutomationDispatchLease(MasterAppDbContext db)
    : ICommerceAutomationDispatchLease
{
    private const string Resource = "LEGEND-commerce-automation-dispatch-v1";

    public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        // Never dispatch from in-memory or SQLite contexts: no cross-host lock.
        if (!db.Database.IsSqlServer())
            return null;

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Closed)
            throw new InvalidOperationException("Automation dispatcher requires a dedicated SQL session.");

        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock
                    @Resource = @resource, @LockMode = 'Exclusive',
                    @LockOwner = 'Session', @LockTimeout = 0;
                SELECT @result;
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@resource";
            parameter.Value = Resource;
            command.Parameters.Add(parameter);

            var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            if (result < 0)
            {
                await connection.CloseAsync();
                return null;
            }
            return new SessionLease(connection);
        }
        catch
        {
            await connection.CloseAsync();
            throw;
        }
    }

    private sealed class SessionLease(DbConnection connection) : IAsyncDisposable
    {
        private bool _disposed;

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (connection.State == ConnectionState.Open)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                        DECLARE @result int;
                        EXEC @result = sys.sp_releaseapplock
                            @Resource = @resource, @LockOwner = 'Session';
                        SELECT @result;
                        """;
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = "@resource";
                    parameter.Value = Resource;
                    command.Parameters.Add(parameter);
                    var code = Convert.ToInt32(await command.ExecuteScalarAsync(),
                        System.Globalization.CultureInfo.InvariantCulture);
                    if (code < 0)
                        throw new InvalidOperationException("Automation dispatcher SQL lock release failed.");
                }
            }
            finally
            {
                if (connection.State != ConnectionState.Closed)
                    await connection.CloseAsync();
            }
        }
    }
}
