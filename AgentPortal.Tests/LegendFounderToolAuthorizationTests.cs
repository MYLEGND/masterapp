using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Security;
using AgentPortal.Services;
using Domain.Messaging;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendFounderToolAuthorizationTests
{
    [Theory]
    [InlineData("legend_inspect_repository", "{\"path\":\".\",\"git_reference\":null}")]
    [InlineData("legend_software_remediation_status", "{}")]
    [InlineData("legend_capabilities", "{}")]
    public async Task ReadOnlyTool_DeniesNonFounderBeforeDispatch(string name, string arguments)
    {
        var prior = Environment.GetEnvironmentVariable("FOUNDER_OID");
        try
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", "587d1166-e29b-41d4-a716-446655440099");
            await using var db = ControllerTestHelpers.BuildDb();
            var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
            var remediation = new Mock<IFounderSoftwareRemediationService>(MockBehavior.Strict);
            var authority = new LegendFounderToolAuthority(
                new FounderLegendConnectService(operations.Object, new AgentProfileAccessResolver(db)),
                remediation.Object);
            var otherUser = ControllerTestHelpers.BuildUser("668d1166-e29b-41d4-a716-446655440088");

            await Assert.ThrowsAsync<ForbidResultException>(() => authority.ExecuteAsync(
                otherUser,
                new FounderAiToolCall("unauthorized-read", name, arguments),
                "legend",
                CancellationToken.None));

            remediation.VerifyNoOtherCalls();
            operations.VerifyNoOtherCalls();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", prior);
        }
    }
}
