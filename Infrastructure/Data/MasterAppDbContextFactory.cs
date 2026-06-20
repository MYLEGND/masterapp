using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Data;

public sealed class MasterAppDbContextFactory : IDesignTimeDbContextFactory<MasterAppDbContext>
{
    public MasterAppDbContext CreateDbContext(string[] args)
    {
        var environmentName = ResolveEnvironmentName(args);
        var (cs, basePath) = ResolveConnectionString(args, environmentName);

        if (string.IsNullOrWhiteSpace(cs))
        {
            throw new InvalidOperationException(
                "Missing MasterAppDb connection string for EF design-time factory. " +
                "Provide one via --connection, SQLCONNSTR_MasterAppDb, ConnectionStrings__MasterAppDb, " +
                "MasterAppDb, or AgentPortal appsettings.");
        }

        cs = NormalizeSqliteConnectionString(cs, basePath);

        var opts = new DbContextOptionsBuilder<MasterAppDbContext>();

        var forceSqlServer =
            string.Equals(
                Environment.GetEnvironmentVariable("EF_FORCE_SQLSERVER"),
                "true",
                StringComparison.OrdinalIgnoreCase);

        if (forceSqlServer)
        {
            opts.UseSqlServer(cs, sql =>
            {
                sql.CommandTimeout(120);
            });
        }
        else if (IsSqliteConnectionString(cs))
        {
            // SQLite remains supported for lightweight local runtime only.
            // EF migration authority and production lineage are SQL Server-first.
            opts.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
            opts.UseSqlServer(cs, sql =>
            {
                sql.CommandTimeout(120);
            });
        }
        else
        {
            opts.UseSqlServer(cs, sql =>
            {
                sql.CommandTimeout(120);
            });
        }

        return new MasterAppDbContext(opts.Options);
    }

    private static (string? ConnectionString, string? BasePath) ResolveConnectionString(string[] args, string? environmentName)
    {
        var cliConnection = ResolveArgumentValue(args, "--connection");
        if (!string.IsNullOrWhiteSpace(cliConnection))
            return (cliConnection, Directory.GetCurrentDirectory());

        var envConnection = Environment.GetEnvironmentVariable("SQLCONNSTR_MasterAppDb")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__MasterAppDb")
            ?? Environment.GetEnvironmentVariable("MasterAppDb");
        if (!string.IsNullOrWhiteSpace(envConnection))
            return (envConnection, Directory.GetCurrentDirectory());

        foreach (var root in ResolveConfigurationRoots())
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(root)
                .AddJsonFile("appsettings.json", optional: true)
                .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
                .AddEnvironmentVariables()
                .Build();

            var fromConfig = config.GetConnectionString("MasterAppDb") ?? config["ConnectionStrings:MasterAppDb"];
            if (!string.IsNullOrWhiteSpace(fromConfig))
                return (fromConfig, root);
        }

        return (null, null);
    }

    private static IEnumerable<string> ResolveConfigurationRoots()
    {
        var current = Directory.GetCurrentDirectory();
        var candidates = new[]
        {
            current,
            Path.Combine(current, "AgentPortal"),
            AppContext.BaseDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AgentPortal"))
        };

        return candidates
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveEnvironmentName(string[] args)
    {
        var fromArgs = ResolveArgumentValue(args, "--environment")
            ?? ResolveArgumentValue(args, "-e");

        return !string.IsNullOrWhiteSpace(fromArgs)
            ? fromArgs
            : Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                ?? "Development";
    }

    private static string? ResolveArgumentValue(string[] args, string optionName)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                    return args[i + 1];

                continue;
            }

            var prefix = optionName + "=";
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return arg[prefix.Length..];
        }

        return null;
    }

    private static bool IsSqliteConnectionString(string connectionString)
    {
        var trimmed = connectionString.Trim();
        return trimmed.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Filename=", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSqliteConnectionString(string connectionString, string? basePath)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(basePath) || !IsSqliteConnectionString(connectionString))
            return connectionString;

        var parts = connectionString.Split(';', StringSplitOptions.None);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim();
            if (part.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
            {
                parts[i] = RewriteSqlitePath(part, "Data Source=", basePath);
                break;
            }

            if (part.StartsWith("Filename=", StringComparison.OrdinalIgnoreCase))
            {
                parts[i] = RewriteSqlitePath(part, "Filename=", basePath);
                break;
            }
        }

        return string.Join(";", parts);
    }

    private static string RewriteSqlitePath(string part, string key, string basePath)
    {
        var rawPath = part[key.Length..].Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(rawPath) ||
            Path.IsPathRooted(rawPath) ||
            rawPath == ":memory:" ||
            rawPath.StartsWith("|DataDirectory|", StringComparison.OrdinalIgnoreCase))
        {
            return part;
        }

        var fullPath = Path.GetFullPath(Path.Combine(basePath, rawPath));
        return $"{key}{fullPath}";
    }
}
