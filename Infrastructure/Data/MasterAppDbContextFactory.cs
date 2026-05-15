using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Infrastructure.Data;

public sealed class MasterAppDbContextFactory : IDesignTimeDbContextFactory<MasterAppDbContext>
{
    public MasterAppDbContext CreateDbContext(string[] args)
    {
        // Design-time EF runs outside AgentPortal host, so we honor the same env var
        // and auto-detect provider (SQL Server vs SQLite).
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__MasterAppDb");
        if (string.IsNullOrWhiteSpace(cs))
        {
            Directory.CreateDirectory("App_Data");
            cs = "Data Source=App_Data/masterapp.db";
        }

        var opts = new DbContextOptionsBuilder<MasterAppDbContext>();
        opts.UseSqlServer(cs);

        opts.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));

        return new MasterAppDbContext(opts.Options);
    }

    private static bool IsSqlite(string? cs) =>
        !string.IsNullOrWhiteSpace(cs) && cs.Trim().StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase);
}
