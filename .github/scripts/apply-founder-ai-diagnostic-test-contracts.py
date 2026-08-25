from pathlib import Path

path = Path('AgentPortal.Tests/LegendFounderAiModeIsolationTests.cs')
text = path.read_text()

old = '''    [Fact]
    public async Task TeacherMode_GovernedToolReadFailureIsStructuredAndNeverBecomesHttp500()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("read transport failed"));

        var handler = new FounderAiScenarioHandler(
            ProviderTool("legend_search_retained_knowledge", "{\\\"query\\\":\\\"authority\\\"}"));
        var service = CreateService(db, operations.Object, handler);
        var controller = new LegendFounderAiController(
            service,
            new LegendFounderAiProgressBroker(),
            NullLogger<LegendFounderAiController>.Instance)
        {
            ControllerContext = ControllerContextFor(founder)
        };

        var result = await controller.Chat(
            Request("teacher", "Inspect the current authority."),
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        var response = Assert.IsType<LegendFounderAiChatResponse>(objectResult.Value);
        Assert.Equal(502, objectResult.StatusCode);
        Assert.False(response.Succeeded);
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal("governed_tool", response.Stage);
        Assert.Equal("tool_read_failed", response.Reason);
        Assert.Equal("governed_tool", response.FailureKind);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }
'''
new = '''    [Fact]
    public async Task TeacherMode_GovernedToolReadFailure_DoesNotAbortIndependentGovernedInspection()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("read transport failed"));

        var handler = new FounderAiScenarioHandler(
            ProviderTool("legend_search_retained_knowledge", "{\\\"query\\\":\\\"authority\\\"}"),
            ProviderTool("legend_capabilities", "{}"),
            ProviderText("The retained-knowledge read failed, but an independent governed capability read succeeded and the diagnosis continued."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            founder,
            Request("teacher", "Inspect the current authority."));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal("provider_response", response.Stage);
        Assert.Contains("diagnosis continued", response.Message, StringComparison.Ordinal);
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }
'''
if old not in text:
    raise SystemExit('read failure contract anchor changed')
text = text.replace(old, new, 1)

old = '''    [Fact]
    public async Task TeacherMode_GovernedToolCancellationIsStructuredTimeoutAndNeverBecomesHttp500()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var handler = new FounderAiScenarioHandler(
            ProviderTool("legend_search_retained_knowledge", "{\\\"query\\\":\\\"authority\\\"}"));
        var service = CreateService(db, operations.Object, handler);
        var controller = new LegendFounderAiController(
            service,
            new LegendFounderAiProgressBroker(),
            NullLogger<LegendFounderAiController>.Instance)
        {
            ControllerContext = ControllerContextFor(founder)
        };

        var result = await controller.Chat(
            Request("teacher", "Inspect the current authority."),
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        var response = Assert.IsType<LegendFounderAiChatResponse>(objectResult.Value);
        Assert.Equal(504, objectResult.StatusCode);
        Assert.False(response.Succeeded);
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal("governed_tool", response.Stage);
        Assert.Equal("tool_timeout", response.Reason);
        Assert.Equal("timeout", response.FailureKind);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }
'''
new = '''    [Fact]
    public async Task TeacherMode_GovernedToolTimeout_DoesNotAbortIndependentGovernedInspection()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var handler = new FounderAiScenarioHandler(
            ProviderTool("legend_search_retained_knowledge", "{\\\"query\\\":\\\"authority\\\"}"),
            ProviderTool("legend_capabilities", "{}"),
            ProviderText("The retained-knowledge read timed out, but an independent governed capability read succeeded and the diagnosis continued."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            founder,
            Request("teacher", "Inspect the current authority."));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal("provider_response", response.Stage);
        Assert.Contains("diagnosis continued", response.Message, StringComparison.Ordinal);
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }
'''
if old not in text:
    raise SystemExit('timeout contract anchor changed')
text = text.replace(old, new, 1)
path.write_text(text)

Path('.github/workflows/apply-founder-ai-diagnostic-test-contracts.yml').unlink(missing_ok=True)
Path('.github/scripts/apply-founder-ai-diagnostic-test-contracts.py').unlink(missing_ok=True)
