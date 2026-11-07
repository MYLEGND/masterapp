using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;
namespace AgentPortal.Tests;
public sealed class StoreCartBrowserTests
{
    [Fact]
    public async Task ActualCartOwnerPreservesLineageAndSerializesTabs()
    {
        var root = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (string.IsNullOrEmpty(root)) { var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MASTERAPP.sln"))) directory = directory.Parent; root = directory?.FullName; }
        Assert.False(string.IsNullOrEmpty(root));
        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(root!, "AgentPortal.Tests/Browser/storefront-cart-tests.cjs"));
        start.ArgumentList.Add(Path.Combine(root!, "ParfaitApp/wwwroot/js/storefront.js"));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await output + await error);
    }
}
