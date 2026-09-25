using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Mobile;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Shared.Diagnostics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class RuntimeDiagnosticCaptureTests
{
    private const string PrivateValue = "private-person@example.test";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Middleware_PreservesOriginalException_WhenSinkResolutionOrPersistenceFails(bool resolutionFailure)
    {
        var logs = new RecordingLogs();
        var sink = new RecordingSink { Failure = new InvalidOperationException("capture-" + PrivateValue) };
        using var services = Services(logs, () => resolutionFailure
            ? throw new InvalidOperationException("resolution-" + PrivateValue) : sink);
        var context = Context(services);
        var original = new InvalidOperationException("original-" + PrivateValue);
        var app = new ApplicationBuilder(services);
        app.UseLegendFailureDiagnostics();
        app.Run(_ => Task.FromException(original));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => app.Build()(context));

        Assert.Same(original, thrown);
        Assert.Equal(resolutionFailure ? 0 : 1, sink.Events.Count);
        AssertSanitizedLogs(logs);
    }

    [Fact]
    public async Task MobileFilter_CapturesHandledFaultThroughExistingSink_AndPreservesGenericResponseAndPrincipal()
    {
        var logs = new RecordingLogs(); var sink = new RecordingSink();
        using var services = Services(logs, () => sink);
        var context = Context(services);
        var principal = context.User;
        var original = new InvalidOperationException(PrivateValue, new Exception("inner-" + PrivateValue));
        var exception = FilterContext(context, original);
        var filter = new MobileApiExceptionFilter(services.GetRequiredService<ILogger<MobileApiExceptionFilter>>());

        await filter.OnExceptionAsync(exception);

        var captured = Assert.Single(sink.Events);
        Assert.Equal("AgentPortal", captured.AppIdentifier);
        Assert.Equal("server", captured.Platform);
        Assert.Equal("messages/{id}", captured.Route);
        Assert.Equal("System.InvalidOperationException", captured.ErrorName);
        Assert.Equal("Application exception", captured.ErrorMessage);
        Assert.Equal(500, captured.StatusCode);
        Assert.True(sink.CancellableTokenReceived);
        Assert.DoesNotContain(PrivateValue, JsonSerializer.Serialize(captured), StringComparison.Ordinal);
        Assert.Same(original, exception.Exception);
        Assert.Same(principal, context.User);
        AssertMobileResponse(exception, context);
        AssertSanitizedLogs(logs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MobileFilter_StillHandlesOriginalFailure_WhenCaptureCannotResolveOrSave(bool resolutionFailure)
    {
        var logs = new RecordingLogs(); var sink = new RecordingSink { Failure = new InvalidOperationException(PrivateValue) };
        using var services = Services(logs, () => resolutionFailure
            ? throw new InvalidOperationException(PrivateValue) : sink);
        var context = Context(services); var original = new InvalidOperationException(PrivateValue);
        var exception = FilterContext(context, original);
        var filter = new MobileApiExceptionFilter(services.GetRequiredService<ILogger<MobileApiExceptionFilter>>());

        await filter.OnExceptionAsync(exception);

        Assert.Same(original, exception.Exception);
        AssertMobileResponse(exception, context);
        AssertSanitizedLogs(logs);
    }

    [Theory]
    [InlineData("/api/runtime-diagnostics")]
    [InlineData("/api/v1/mobile/runtime-diagnostics/")]
    public async Task IngestionFailure_DoesNotResolveItsOwnSink(string path)
    {
        var resolutions = 0; var logs = new RecordingLogs();
        using var services = Services(logs, () => { resolutions++; throw new InvalidOperationException(PrivateValue); });
        var context = Context(services); context.Request.Path = path;
        var original = new InvalidOperationException(PrivateValue);
        var app = new ApplicationBuilder(services); app.UseLegendFailureDiagnostics(); app.Run(_ => Task.FromException(original));

        Assert.Same(original, await Assert.ThrowsAsync<InvalidOperationException>(() => app.Build()(context)));
        var exception = FilterContext(context, original);
        await new MobileApiExceptionFilter(services.GetRequiredService<ILogger<MobileApiExceptionFilter>>()).OnExceptionAsync(exception);

        Assert.Equal(0, resolutions);
        AssertMobileResponse(exception, context);
        AssertSanitizedLogs(logs);
    }

    [Fact]
    public async Task CallerCancellation_PreservesOriginalOutcome_WithoutErrorReports()
    {
        var logs = new RecordingLogs(); var sink = new RecordingSink();
        using var services = Services(logs, () => sink);
        var context = Context(services);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); context.RequestAborted = cancellation.Token;
        var original = new OperationCanceledException("private cancellation-" + PrivateValue, cancellation.Token);
        var app = new ApplicationBuilder(services); app.UseLegendFailureDiagnostics(); app.Run(_ => Task.FromException(original));

        Assert.Same(original, await Assert.ThrowsAsync<OperationCanceledException>(() => app.Build()(context)));
        var exception = FilterContext(context, original);
        await new MobileApiExceptionFilter(services.GetRequiredService<ILogger<MobileApiExceptionFilter>>()).OnExceptionAsync(exception);

        Assert.Empty(sink.Events);
        Assert.Empty(logs.Entries);
        // The existing mobile response contract is unchanged; no error is relabeled as success.
        AssertMobileResponse(exception, context);
    }

    private static ServiceProvider Services(RecordingLogs logs, Func<IRuntimeDiagnosticSink> resolveSink) => new ServiceCollection()
        .AddLogging(builder => builder.AddProvider(logs))
        .AddSingleton<IHostEnvironment>(new TestEnvironment())
        .AddSingleton<IRuntimeDiagnosticSink>(_ => resolveSink()).BuildServiceProvider();

    private static DefaultHttpContext Context(IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services, TraceIdentifier = "diagnostic-fixture-1" };
        context.Request.Path = "/messages/" + PrivateValue;
        context.Request.QueryString = new QueryString("?token=" + PrivateValue);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, PrivateValue) }, "fixture"));
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("messages/{id}"),
            0, EndpointMetadataCollection.Empty, "Messages.Details"));
        return context;
    }

    private static ExceptionContext FilterContext(HttpContext context, Exception exception) =>
        new(new ActionContext(context, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>()) { Exception = exception };

    private static void AssertMobileResponse(ExceptionContext exception, HttpContext context)
    {
        Assert.True(exception.ExceptionHandled);
        var result = Assert.IsType<ObjectResult>(exception.Result);
        Assert.Equal(500, result.StatusCode);
        Assert.Contains("application/json", result.ContentTypes);
        Assert.Equal("diagnostic-fixture-1", context.Response.Headers["X-Correlation-ID"].ToString());
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("mobile_request_failed", json, StringComparison.Ordinal);
        Assert.Contains("The mobile service could not complete this request.", json, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateValue, json, StringComparison.Ordinal);
    }

    private static void AssertSanitizedLogs(RecordingLogs logs)
    {
        Assert.NotEmpty(logs.Entries);
        Assert.All(logs.Entries, item =>
        {
            Assert.Null(item.Exception);
            Assert.DoesNotContain(PrivateValue, item.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("?token=", item.Message, StringComparison.Ordinal);
        });
    }

    private sealed class RecordingSink : IRuntimeDiagnosticSink
    {
        public readonly List<RuntimeDiagnosticEvent> Events = new();
        public Exception? Failure { get; init; }
        public bool CancellableTokenReceived { get; private set; }
        public Task RecordAsync(RuntimeDiagnosticEvent value, CancellationToken cancellationToken = default)
        {
            CancellableTokenReceived = cancellationToken.CanBeCanceled;
            Events.Add(value);
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class RecordingLogs : ILoggerProvider
    {
        public readonly List<(string Message, Exception? Exception)> Entries = new();
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);
        public void Dispose() { }
        private sealed class RecordingLogger(RecordingLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => owner.Entries.Add((formatter(state, exception), exception));
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "AgentPortal";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
