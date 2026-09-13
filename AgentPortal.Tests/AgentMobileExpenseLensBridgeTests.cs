using System;
using System.Security.Claims;
using System.Threading.Tasks;
using AgentPortal.Controllers.API;
using AgentPortal.Services;
using AgentPortal.Services.Tracking;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class AgentMobileExpenseLensBridgeTests
{
    [Fact]
    public async Task AgentExpenseLens_SaveApiPublishesTheSameProjectionReadByMobile()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        const string agentOid = "agent-mobile-finance-oid";
        var http = new DefaultHttpContext
        {
            User = ControllerTestHelpers.BuildUser(agentOid, "agent@example.test")
        };
        var agentContext = new EffectiveAgentContext(
            new HttpContextAccessor { HttpContext = http },
            Mock.Of<IAgentTrackingService>(),
            NullLogger<EffectiveAgentContext>.Instance);
        var controller = new FinanceToolStatesController(
            db,
            agentContext,
            Mock.Of<Infrastructure.Households.IHouseholdMembershipService>())
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

        var result = await controller.Save(new FinanceToolStatesController.SaveFinanceStateRequest
        {
            ToolId = "ExpenseLens",
            JsonState =
                """
                {
                  "incomeStreams": { "primary": [{ "id": "salary", "amount": "2400", "frequency": "monthly", "anchorDate": "2026-07-29" }] },
                  "categories": [{ "id": "bill", "name": "Insurance", "amount": "600", "due": "2026-07-29", "frequency": "monthly", "paymentMethod": "Debit" }]
                }
                """
        });

        Assert.IsType<OkObjectResult>(result);
        var persisted = Assert.Single(db.AgentFinanceToolStates);
        Assert.Equal(agentOid, persisted.AgentUserId);
        Assert.Equal("ExpenseLens", persisted.ToolId);

        var mobile = await new MobileFinancialOperatingSystemProjectionService(db, Mock.Of<Infrastructure.Households.IHouseholdMembershipService>())
            .ProjectAgentAsync(
                agentOid,
                new DateOnly(2026, 7, 29));

        Assert.Equal("Available", mobile.Projection.Status);
        var week = Assert.IsType<MobileFinancialWeekAtGlance>(mobile.WeekAtGlance);
        Assert.Equal(240000, week.IncomeCents);
        Assert.Equal(60000, week.DebitExpenseCents);
        Assert.NotNull(mobile.MonthAtGlance);
    }
}
