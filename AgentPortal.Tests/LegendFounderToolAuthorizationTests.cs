using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
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
    [InlineData("legend_calculate", "{\"operation\":\"add\",\"left\":\"1\",\"right\":\"2\"}")]
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
    [Theory]
    [InlineData("{\"operation\":\"divide\",\"left\":\"17\",\"right\":\"6\"}", true)]
    [InlineData("{\"operation\":\"add\",\"left\":\"1\",\"left\":\"2\",\"right\":\"3\"}", false)]
    [InlineData("{\"operation\":\"divide\",\"left\":\"1\",\"right\":\"0\"}", false)]
    [InlineData("{\"operation\":\"execute\",\"left\":\"1\",\"right\":\"2\"}", false)]
    [InlineData("{", false)]
    public async Task CalculationUsesOneExecutorWithoutOrganizationalEvidenceOrProviderCalls(string arguments, bool accepted)
    {
        var prior = Environment.GetEnvironmentVariable("FOUNDER_OID");
        const string founderId = "587d1166-e29b-41d4-a716-446655440099";
        try
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
            await using var db = ControllerTestHelpers.BuildDb();
            var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
            var authority = new LegendFounderToolAuthority(
                new FounderLegendConnectService(operations.Object, new AgentProfileAccessResolver(db)), null);
            var result = await authority.ExecuteAsync(ControllerTestHelpers.BuildUser(founderId),
                new FounderAiToolCall("calculation-check", "legend_calculate", arguments), "legend", CancellationToken.None,
                LegendConnectExternalProviderPolicy.NativeOnly);
            using var receipt = JsonDocument.Parse(result);
            Assert.Equal(accepted, receipt.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(authority.IsReadOnly("legend_calculate"));
            Assert.False(authority.IsGovernedEvidence("legend_calculate"));
            Assert.False(authority.IsNativeContentBindingRead("legend_calculate"));
            if (accepted)
            {
                Assert.Equal("17/6", receipt.RootElement.GetProperty("result").GetString());
                Assert.Equal("supplied_operands_only", receipt.RootElement.GetProperty("scope").GetString());
            }
            operations.VerifyNoOtherCalls();
        }
        finally { Environment.SetEnvironmentVariable("FOUNDER_OID", prior); }
    }


    [Fact]
    public async Task SiteReadProjection_IncludesSanitizedRepairLifecycleReads_ButNoMutationTools()
    {
        var prior = Environment.GetEnvironmentVariable("FOUNDER_OID");
        const string founderId = "587d1166-e29b-41d4-a716-446655440099";
        try
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
            await using var db = ControllerTestHelpers.BuildDb();
            var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
            var remediation = new Mock<IFounderSoftwareRemediationService>(MockBehavior.Strict);
            var authority = new LegendFounderToolAuthority(
                new FounderLegendConnectService(operations.Object, new AgentProfileAccessResolver(db)),
                remediation.Object);

            var names = authority.GetAvailableSiteReadTools()
                .Select(tool => JsonSerializer.SerializeToElement(tool).GetProperty("name").GetString())
                .ToArray();

            Assert.Contains("legend_prepare_repair_packet", names);
            Assert.Contains("legend_inspect_repair_validation", names);
            Assert.Contains("legend_request_repair_release", names);
            Assert.Contains("legend_verify_repair_deployment", names);
            Assert.DoesNotContain("legend_prepare_software_repair", names);
            Assert.DoesNotContain("legend_release_approved_repair", names);
            Assert.All(names, name => Assert.True(authority.IsReadOnly(name!)));
            remediation.VerifyNoOtherCalls();
            operations.VerifyNoOtherCalls();
        }
        finally { Environment.SetEnvironmentVariable("FOUNDER_OID", prior); }
    }

    [Theory]
    [InlineData(
        """{"application":"AgentPortal","route":"/Clients/Index","source_revision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","failure_class":"CODE_DEFECT","expected_behavior":"Modal opens and remains interactive.","observed_behavior":"Modal shield blocks interaction.","component_ids":["website.modal"],"issue_codes":["TypeError"],"suspected_paths":["AgentPortal/Controllers/ClientsController.cs","AgentPortal/Program.cs"]}""",
        true,
        null)]
    [InlineData(
        """{"application":"AgentPortal","route":"/Clients/Index","source_revision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","failure_class":"CODE_DEFECT","expected_behavior":"Email customer@example.com after repair.","observed_behavior":"Modal shield blocks interaction.","component_ids":[],"issue_codes":[],"suspected_paths":[]}""",
        false,
        "repair_packet_arguments_invalid")]
    [InlineData(
        """{"application":"AgentPortal","route":"/Clients/Index","source_revision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","failure_class":"CODE_DEFECT","expected_behavior":"Modal opens.","observed_behavior":"Modal blocks.","component_ids":[],"issue_codes":[],"suspected_paths":["AgentPortal/uploads/customer-record.cs"]}""",
        false,
        "repair_packet_protected_path")]
    public async Task SanitizedRepairPacket_RejectsPrivacyMaterial_AndNeverDispatchesMutation(
        string arguments, bool accepted, string? expectedError)
    {
        var prior = Environment.GetEnvironmentVariable("FOUNDER_OID");
        const string founderId = "587d1166-e29b-41d4-a716-446655440099";
        try
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
            await using var db = ControllerTestHelpers.BuildDb();
            var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
            var remediation = new Mock<IFounderSoftwareRemediationService>(MockBehavior.Strict);
            var authority = new LegendFounderToolAuthority(
                new FounderLegendConnectService(operations.Object, new AgentProfileAccessResolver(db)),
                remediation.Object);

            var result = await authority.ExecuteAsync(
                ControllerTestHelpers.BuildUser(founderId),
                new FounderAiToolCall("repair-packet", "legend_prepare_repair_packet", arguments),
                "legend",
                CancellationToken.None,
                LegendConnectExternalProviderPolicy.CloudflareFoundation);
            using var receipt = JsonDocument.Parse(result);

            Assert.Equal(accepted, receipt.RootElement.GetProperty("ok").GetBoolean());
            if (accepted)
            {
                Assert.Equal("legend_sanitized_software_repair.v1", receipt.RootElement.GetProperty("packetType").GetString());
                Assert.False(receipt.RootElement.GetProperty("mutationAuthorized").GetBoolean());
                var paths = receipt.RootElement.GetProperty("suspectedSourcePaths").EnumerateArray().ToArray();
                Assert.Equal("SAFE_SOURCE", paths[0].GetProperty("disclosureClass").GetString());
                Assert.Equal("INTEGRITY_PROTECTED", paths[1].GetProperty("disclosureClass").GetString());
                Assert.True(paths[0].GetProperty("readable").GetBoolean());
                Assert.False(paths[1].GetProperty("readable").GetBoolean());
            }
            else
            {
                Assert.Equal(expectedError, receipt.RootElement.GetProperty("error").GetString());
            }

            remediation.VerifyNoOtherCalls();
            operations.VerifyNoOtherCalls();
        }
        finally { Environment.SetEnvironmentVariable("FOUNDER_OID", prior); }
    }

}
