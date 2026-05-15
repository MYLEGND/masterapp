using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Infrastructure.Data;

public sealed class MasterAppDbContextFactory : IDesignTimeDbContextFactory<MasterAppDbContext>
{
    public MasterAppDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("SQLCONNSTR_MasterAppDb")
              ?? Environment.GetEnvironmentVariable("ConnectionStrings__MasterAppDb")
              ?? Environment.GetEnvironmentVariable("MasterAppDb");

        if (string.IsNullOrWhiteSpace(cs))
        {
            throw new InvalidOperationException("Missing MasterAppDb connection string for EF design-time factory.");
        }

        var opts = new DbContextOptionsBuilder<MasterAppDbContext>();
        opts.UseSqlServer(cs);

        return new MasterAppDbContext(opts.Options);
    }
}
