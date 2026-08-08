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
                  "mobileWeekProjection": {
                    "schemaVersion": 1,
                    "weekId": "2026-07-27_2026-08-02",
                    "weekLabel": "Jul 27 – Aug 2",
                    "startDate": "2026-07-27",
                    "endDate": "2026-08-02",
                    "status": "current",
                    "openingCashCents": 100000,
                    "incomeCents": 240000,
                    "debitBillsCents": 60000,
                    "creditBillsCents": 25000,
                    "requiredDebtMinimumCents": 10000,
                    "extraDebtPaymentCents": 5000,
                    "closingCashCents": 150000,
                    "openingDebtCents": 500000,
                    "closingDebtCents": 485000,
                    "events": []
                  }
                }
                """
        });

        Assert.IsType<OkObjectResult>(result);
        var persisted = Assert.Single(db.AgentFinanceToolStates);
        Assert.Equal(agentOid, persisted.AgentUserId);
        Assert.Equal("ExpenseLens", persisted.ToolId);

        var mobile = await new MobileFinancialOperatingSystemProjectionService(db)
            .ProjectAgentAsync(agentOid);

        Assert.Equal("Available", mobile.Projection.Status);
        var week = Assert.IsType<MobileFinancialWeekAtGlance>(mobile.WeekAtGlance);
        Assert.Equal("2026-07-27_2026-08-02", week.WeekKey);
        Assert.Equal(150000, week.EndingCashCents);
    }
}
