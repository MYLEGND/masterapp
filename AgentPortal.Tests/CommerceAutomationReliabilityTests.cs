using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using ParfaitApp.Models;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommerceAutomationReliabilityTests
{
    [Fact]
    public void AcceptedDeliveryCannotReplayAfterActivityWindowExpires()
    {
        using var f = new Fixture();
        var candidate = Assert.Single(f.Service.GetDueDispatchCandidates(f.Business));
        Assert.True(f.Service.TryBeginDispatch(candidate));
        f.Service.MarkDispatchSent(candidate);
        var receipt = f.Db.MarketingDestinationDeliveries.Single();
        receipt.UpdatedUtc = DateTime.UtcNow.AddYears(-1);
        receipt.SentUtc = receipt.UpdatedUtc;
        f.Db.SaveChanges();
        Assert.Empty(f.Service.GetDueDispatchCandidates(f.Business));
        Assert.False(f.Service.TryBeginDispatch(candidate));
        Assert.Single(f.Db.MarketingDestinationDeliveries);
    }

    [Fact]
    public void SecondClaimAndExpiredSendingClaimCannotSendAgain()
    {
        using var f = new Fixture();
        var candidate = Assert.Single(f.Service.GetDueDispatchCandidates(f.Business));
        Assert.True(f.Service.TryBeginDispatch(candidate));
        Assert.False(f.Service.TryBeginDispatch(candidate));
        f.Db.MarketingDestinationDeliveries.Single().ClaimExpiresUtc = DateTime.UtcNow.AddHours(-1);
        f.Db.SaveChanges();
        Assert.Empty(f.Service.GetDueDispatchCandidates(f.Business));
        Assert.Equal("Uncertain", Assert.Single(f.Service.GetWorkspaceViewModel(f.Business).Activity).Status);
    }

    [Fact]
    public void UnknownProviderOutcomeIsHeldWithoutAutomaticRetry()
    {
        using var f = new Fixture();
        var candidate = Assert.Single(f.Service.GetDueDispatchCandidates(f.Business));
        Assert.True(f.Service.TryBeginDispatch(candidate));
        f.Service.MarkDispatchFailed(candidate, "Timeout");
        Assert.Empty(f.Service.GetDueDispatchCandidates(f.Business));
        Assert.Equal("uncertain", f.Db.MarketingDestinationDeliveries.Single().Status);
    }

    [Fact]
    public void ExplicitThrottleUsesBackoffAndStopsAfterThreeAttempts()
    {
        using var f = new Fixture();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var candidate = Assert.Single(f.Service.GetDueDispatchCandidates(f.Business));
            Assert.True(f.Service.TryBeginDispatch(candidate));
            f.Service.MarkDispatchRejected(candidate, 429);
            Assert.Empty(f.Service.GetDueDispatchCandidates(f.Business));
            var receipt = f.Db.MarketingDestinationDeliveries.Single();
            Assert.Equal(attempt, receipt.AttemptCount);
            if (attempt < 3)
            {
                Assert.True(receipt.NextAttemptUtc > DateTime.UtcNow);
                receipt.NextAttemptUtc = DateTime.UtcNow.AddMinutes(-1);
                f.Db.SaveChanges();
            }
        }
        Assert.Equal("rejected", f.Db.MarketingDestinationDeliveries.Single().Status);
    }

    [Fact]
    public void PurchaseAndForeignStoreConsentDoNotAuthorizePromotions()
    {
        using var f = new Fixture();
        f.Db.WebsiteLeads.Single().CommerceBusinessId = Guid.NewGuid();
        f.Db.SaveChanges();
        Assert.Empty(f.Service.GetDueDispatchCandidates(f.Business));
    }

    [Fact]
    public void MalformedConsentFailsClosedWithoutThrowing()
    {
        using var f = new Fixture();
        f.Db.WebsiteLeads.Single().MetadataJson = "broken json";
        f.Db.SaveChanges();
        Assert.Empty(f.Service.GetDueDispatchCandidates(f.Business));
    }

    [Fact]
    public void WithdrawalAfterCandidateSelectionStopsClaimAndDoesNotAffectAnotherStore()
    {
        using var f = new Fixture();
        var candidate = Assert.Single(f.Service.GetDueDispatchCandidates(f.Business));
        Assert.False(f.Service.Unsubscribe(Guid.NewGuid(), f.Lead.LeadId, Fixture.Token));
        Assert.True(f.Service.Unsubscribe(f.Business, f.Lead.LeadId, Fixture.Token));
        Assert.False(f.Service.TryBeginDispatch(candidate));
        Assert.Empty(f.Db.MarketingDestinationDeliveries);
    }

    [Fact]
    public void RefundedOrderAfterCandidateSelectionCannotBeClaimed()
    {
        using var f = new Fixture();
        var candidate = Assert.Single(f.Service.GetDueDispatchCandidates(f.Business));
        f.Order.PaymentStatus = "Refunded";
        f.Db.SaveChanges();
        Assert.False(f.Service.TryBeginDispatch(candidate));
        Assert.Empty(f.Db.MarketingDestinationDeliveries);
    }

    [Fact]
    public void ActivationWatermarkExcludesHistoricalOrders()
    {
        using var f = new Fixture();
        var state = f.State;
        state.Workflows[0].EligibleFromUtc = DateTime.UtcNow;
        f.SaveState(state);
        Assert.Empty(f.Service.GetDueDispatchCandidates(f.Business));
    }

    [Fact]
    public void StaleWorkflowEditorCannotOverwriteNewerChanges()
    {
        using var f = new Fixture();
        var input = new ParfaitAutomationWorkflowEditorInput { Id = f.Workflow.Id, ExpectedUpdatedTicks = f.Workflow.UpdatedUtc.Ticks, Name = "First change" };
        f.Service.SaveWorkflow(f.Business, input);
        input.Name = "Stale overwrite";
        Assert.Throws<InvalidOperationException>(() => f.Service.SaveWorkflow(f.Business, input));
        Assert.Equal("First change", f.Service.GetWorkspaceViewModel(f.Business).Workflows.Single().Name);
    }

    [Fact]
    public void CanonicalLinksDoNotDuplicateStorePathAndIncludeWithdrawal()
    {
        using var f = new Fixture();
        var candidate = Assert.Single(f.Service.GetDueDispatchCandidates(f.Business));
        Assert.Contains("https://store.example.test/store\"", candidate.HtmlBody);
        Assert.DoesNotContain("/store/store", candidate.HtmlBody);
        Assert.Contains("/store/email-preferences?", candidate.HtmlBody);
        Assert.Contains("Unsubscribe from promotional emails", candidate.HtmlBody);
    }

    [Fact]
    public void InternalConsentAndMissingMailingAddressKeepSendingOnHold()
    {
        using var f = new Fixture();
        f.Lead.IsInternal = true;
        f.Db.SaveChanges();
        Assert.Empty(f.Service.GetDueDispatchCandidates(f.Business));
        f.Lead.IsInternal = false;
        f.Db.SaveChanges();
        var state = f.State;
        state.EmailPostalAddress = "";
        f.SaveState(state);
        Assert.Empty(f.Service.GetDueDispatchCandidates(f.Business));
    }

    [Fact]
    public void StaleDeletePreservesEditedWorkflow()
    {
        using var f = new Fixture();
        var previousTicks = f.Workflow.UpdatedUtc.Ticks;
        f.Service.SaveWorkflow(f.Business, new() { Id = f.Workflow.Id, ExpectedUpdatedTicks = previousTicks, Name = "Updated" });
        Assert.Throws<InvalidOperationException>(() => f.Service.DeleteWorkflow(f.Business, f.Workflow.Id, previousTicks));
        var current = Assert.Single(f.Service.GetWorkspaceViewModel(f.Business).Workflows);
        Assert.Equal("Updated", current.Name);
        f.Service.DeleteWorkflow(f.Business, current.Id, current.UpdatedTicks);
        Assert.Empty(f.Service.GetWorkspaceViewModel(f.Business).Workflows);
    }

    [Fact]
    public void CheckoutRetryDoesNotReverseUnsubscribeButNewOptInCanResubscribe()
    {
        using var f = new Fixture();
        var order = f.Orders.GetOrder(f.Business, f.Order.OrderNumber)!;
        f.Service.RecordMarketingOptIn(f.Business, order, false);
        Assert.True(f.Service.Unsubscribe(f.Business, f.Lead.LeadId, Fixture.Token));
        f.Service.RecordMarketingOptIn(f.Business, order, false);
        Assert.True(f.Service.GetEmailPreferences(f.Business, f.Lead.LeadId, Fixture.Token)!.Unsubscribed);
        order.CommerceOrderId = Guid.NewGuid();
        f.Service.RecordMarketingOptIn(f.Business, order, false);
        Assert.False(f.Service.GetEmailPreferences(f.Business, f.Lead.LeadId, Fixture.Token)!.Unsubscribed);
        Assert.True(f.Service.Unsubscribe(f.Business, f.Lead.LeadId, Fixture.Token));
        Assert.All(f.Db.WebsiteLeads, lead => Assert.False(lead.MarketingEmailConsent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparationFailureRollsBackOrderAndConsentBeforePayment(bool failAfterConsent)
    {
        using var f = new Fixture();
        var originalLeadCount = f.Db.WebsiteLeads.Count();
        Assert.Throws<InvalidOperationException>(() => f.Orders.BeginCheckoutPayment(f.Business,
            "failure-test", new() { FirstName = "Test", Email = "new@example.test" }, [], 1000,
            null, null, 0, 0, 0, new Microsoft.AspNetCore.Http.DefaultHttpContext(), order => {
                if (failAfterConsent) f.Service.RecordMarketingOptIn(f.Business, order, false);
                throw new InvalidOperationException("Injected optional write failure");
            }));
        Assert.DoesNotContain(f.Db.CommerceOrders, order => order.CheckoutAttemptId == "failure-test");
        Assert.Equal(originalLeadCount, f.Db.WebsiteLeads.Count());
        var retry = f.Orders.BeginCheckoutPayment(f.Business, "failure-test",
            new() { FirstName = "Test", Email = "new@example.test" }, [], 1000, null, null, 0, 0, 0,
            new Microsoft.AspNetCore.Http.DefaultHttpContext());
        Assert.Equal(CheckoutPaymentStartState.Ready, retry.State);
    }

    [Theory]
    [InlineData(1205)]
    [InlineData(2601)]
    [InlineData(2627)]
    public void WrappedSqlClaimCollisionDeclinesAndDoesNotPoisonNextClaim(int number)
    {
        var fault = new ClaimSaveFault(new InvalidOperationException("Execution strategy wrapper",
            new DbUpdateException("Update failed", ClaimSqlException(number))));
        using var f = new Fixture(fault);
        var candidate = Assert.Single(f.Service.GetDueDispatchCandidates(f.Business));
        Assert.False(f.Service.TryBeginDispatch(candidate));
        Assert.Empty(f.Db.ChangeTracker.Entries<MarketingDestinationDelivery>());
        Assert.Empty(f.Db.MarketingDestinationDeliveries);
        fault.Enabled = false;
        Assert.True(f.Service.TryBeginDispatch(candidate));
        Assert.Equal("sending", Assert.Single(f.Db.MarketingDestinationDeliveries).Status);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(19)]
    public void WrappedSqliteClaimCollisionDeclines(int code)
    {
        var fault = new ClaimSaveFault(new InvalidOperationException("Wrapper",
            new DbUpdateException("Update failed", new SqliteException("Synthetic contention", code))));
        using var f = new Fixture(fault);
        Assert.False(f.Service.TryBeginDispatch(Assert.Single(f.Service.GetDueDispatchCandidates(f.Business))));
        Assert.Empty(f.Db.ChangeTracker.Entries<MarketingDestinationDelivery>());
    }

    [Fact]
    public void UnexpectedWrappedSqlErrorStillPropagates()
    {
        var error = new InvalidOperationException("Wrapper", new DbUpdateException("Update failed", ClaimSqlException(229)));
        using var f = new Fixture(new ClaimSaveFault(error));
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() =>
            f.Service.TryBeginDispatch(Assert.Single(f.Service.GetDueDispatchCandidates(f.Business)))));
    }

    private sealed class ClaimSaveFault(Exception error) : SaveChangesInterceptor
    {
        public bool Enabled { get; set; } = true;
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<MarketingDestinationDelivery>().Any(e => e.State == EntityState.Added))
                throw error;
            return result;
        }
    }

    private static SqlException ClaimSqlException(int number)
    {
        var constructor = typeof(SqlError).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .First(item => item.GetParameters().Length >= 8 && item.GetParameters()[0].ParameterType == typeof(int));
        var values = constructor.GetParameters().Select((parameter, index) => index == 0 ? (object)number :
            parameter.ParameterType == typeof(string) ? "test" : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null).ToArray();
        var error = constructor.Invoke(values);
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(errors, [error]);
        var factory = typeof(SqlException).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .First(method => method.Name == "CreateException" && method.GetParameters().Length == 2 && method.GetParameters()[0].ParameterType == typeof(SqlErrorCollection));
        return (SqlException)factory.Invoke(null, [errors, "test"])!;
    }

    private sealed class Fixture : IDisposable
    {
        public const string Token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=False");
        public readonly Guid Business = Guid.NewGuid();
        public readonly MasterAppDbContext Db;
        public readonly ParfaitOrderService Orders;
        public readonly ParfaitCustomerAutomationService Service;
        public readonly ParfaitAutomationWorkflowRecord Workflow;
        public readonly CommerceOrder Order;
        public readonly WebsiteLead Lead;
        public ParfaitAutomationStoreRecord State => JsonSerializer.Deserialize<ParfaitAutomationStoreRecord>(Db.CommerceBusinessSettings.Single().CustomerAutomationStateJson!)!;
        public Fixture(SaveChangesInterceptor? interceptor = null)
        {
            connection.Open();
            var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            Db = new(options.Options);
            Db.Database.EnsureCreated();
            Db.CommerceBusinesses.Add(new() { Id = Business, Key = "parfait", DisplayName = "Test Store", IsActive = true, Status = "Active" });
            Workflow = new() { Name = "Follow up", Subject = "Thank you", Headline = "Your next visit", Body = "See the store", CtaUrl = "/store", DelayAmount = 1, DelayUnit = "Hours", EligibleFromUtc = DateTime.UtcNow.AddDays(-5) };
            Db.CommerceBusinessSettings.Add(new() { CommerceBusinessId = Business, CustomerAutomationStateJson = JsonSerializer.Serialize(new ParfaitAutomationStoreRecord { EmailPostalAddress = "123 Test Street, Example, AZ 85001", Workflows = [Workflow] }) });
            Order = new() { CommerceBusinessId = Business, OrderNumber = "TEST-001", PaymentStatus = "Paid", PaidUtc = DateTime.UtcNow.AddDays(-2), CreatedUtc = DateTime.UtcNow.AddDays(-2), Email = "buyer@example.test", FirstName = "Test", TotalCents = 1000 };
            Db.CommerceOrders.Add(Order);
            Lead = new() { LeadId = Guid.NewGuid(), CommerceBusinessId = Business, FirstName = "Test", Email = Order.Email, MarketingEmailConsent = true, SourceCtaKey = "commerce-marketing-opt-in-v1", CreatedUtc = DateTime.UtcNow.AddDays(-3), MetadataJson = JsonSerializer.Serialize(new { unsubscribeToken = Token }) };
            Db.WebsiteLeads.Add(Lead);
            Db.SaveChanges();
            var environment = new Mock<IWebHostEnvironment>();
            environment.SetupGet(x => x.ContentRootPath).Returns(Path.GetTempPath());
            environment.SetupGet(x => x.WebRootPath).Returns(Path.GetTempPath());
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Commerce:PublicBaseUrl"] = "https://store.example.test" }).Build();
            var paths = new ParfaitStoragePaths(environment.Object, config);
            Orders = new(Db);
            var products = new ParfaitProductService(paths, Db);
            var stores = new CommerceStoreContextService(Db, new CommerceBusinessScopeResolver(Db), new ParfaitBusinessScopeService(Db), new WebsiteDomainService(Db, Mock.Of<IHttpClientFactory>(), config), config);
            Service = new(paths, config, Orders, products, Db, stores);
        }
        public void SaveState(ParfaitAutomationStoreRecord state) { Db.CommerceBusinessSettings.Single().CustomerAutomationStateJson = JsonSerializer.Serialize(state); Db.SaveChanges(); }
        public void Dispose() { Db.Dispose(); connection.Dispose(); }
    }
}
