using System;
using System.IO;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LocalDevelopmentDatabaseTests
{
    [Theory]
    [InlineData("Server=tcp:masterapp-sql-prod-1221.database.windows.net;Initial Catalog=MasterAppDb;Integrated Security=true")]
    [InlineData("Server=127.0.0.1;Initial Catalog=OtherDatabase;Integrated Security=true")]
    [InlineData("Data Source=:memory:")]
    [InlineData("")]
    public void DevelopmentRejectsProductionFallbackAndWrongIdentity(string connection)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        Assert.Throws<InvalidOperationException>(() => LocalDevelopmentDatabase.ValidateTarget(builder.Environment, connection));
    }

    [Fact]
    public void DevelopmentAcceptsOnlyPersistentLoopbackSql()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        LocalDevelopmentDatabase.ValidateTarget(builder.Environment,
            "Server=127.0.0.1,14339;Initial Catalog=MasterAppLocalTest;Integrated Security=true");
    }

    [Fact]
    public void MissingSqliteIsNotCreated()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        Assert.Throws<InvalidOperationException>(() => LocalDevelopmentDatabase.ExistingSqliteConnection("Data Source=" + path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ExistingSqliteUsesNonCreatingMode()
    {
        var path = Path.GetTempFileName();
        try
        {
            var value = LocalDevelopmentDatabase.ExistingSqliteConnection("Data Source=" + path);
            Assert.Equal(SqliteOpenMode.ReadWrite, new SqliteConnectionStringBuilder(value).Mode);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void LocalTestingRemovesOperatorSecretsAndApplicationWorkers()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "secrets.json"), "{\"SyntheticProviderSecret\":\"must-not-load\"}");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
            builder.Configuration.AddJsonFile(Path.Combine(directory, "secrets.json"));
            builder.Configuration["LocalTesting:Enabled"] = "true";
            LocalTestingHost.Configure(builder);
            builder.Services.AddHostedService<SyntheticWorker>();
            LocalTestingHost.Prepare(builder);
            Assert.Null(builder.Configuration["SyntheticProviderSecret"]);
            Assert.DoesNotContain(builder.Services, service => service.ImplementationType == typeof(SyntheticWorker));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void LocalTestingCannotBeEnabledInProduction()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Configuration["LocalTesting:Enabled"] = "true";
        Assert.Throws<InvalidOperationException>(() => LocalTestingHost.Configure(builder));
    }

    private sealed class SyntheticWorker : IHostedService
    {
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token) => Task.CompletedTask;
    }
}
