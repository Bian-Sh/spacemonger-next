using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using SpaceMonger.Core.Models;
using SpaceMonger.Core.Services.Agent;
using SpaceMonger.Core.Services.Llm;

namespace SpaceMonger.Core.Tests;

public class AgentRuntimeTests
{
    [Fact]
    public async Task RunAsync_DoesNotExecuteToolsFromHostKeywordHeuristics()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.SendChatAsync(
                Arg.Any<string>(),
                Arg.Any<List<(string role, string content)>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns("The model must emit tool_calls before tools run.");
        var tool = new CountingTool("find_large_files");
        var runtime = new AgentRuntime(llm, [tool]);
        var root = new FileEntry { Name = "C:", Path = @"C:\", IsDirectory = true };
        var context = new AgentContext(new ScanSession { TargetPath = @"C:\", RootEntry = root }, root, root, null, false);

        var response = await runtime.RunAsync(
            context,
            [],
            "What are the largest files?",
            [],
            "en",
            "key",
            null,
            enableThinking: false,
            onThinkingToken: null,
            CancellationToken.None);

        response.ToolResults.Should().BeEmpty();
        tool.ExecuteCount.Should().Be(0);
        response.Content.Should().Contain("tool_calls");
    }

    [Fact]
    public async Task RunAsync_ExecutesToolOnlyWhenModelEmitsToolCalls()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.SendChatAsync(
                Arg.Any<string>(),
                Arg.Any<List<(string role, string content)>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                _ => "{\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"find_large_files\",\"arguments\":{}}]}",
                _ => "Done after tool observations.");
        var tool = new CountingTool("find_large_files");
        var runtime = new AgentRuntime(llm, [tool]);
        var root = new FileEntry { Name = "C:", Path = @"C:\", IsDirectory = true };
        var context = new AgentContext(new ScanSession { TargetPath = @"C:\", RootEntry = root }, root, root, null, false);

        var response = await runtime.RunAsync(
            context,
            [],
            "What are the largest files?",
            [],
            "en",
            "key",
            null,
            enableThinking: false,
            onThinkingToken: null,
            CancellationToken.None);

        tool.ExecuteCount.Should().Be(1);
        response.ToolResults.Should().ContainSingle(result => result.ToolName == "find_large_files" && !result.IsError);
        response.Content.Should().Be("Done after tool observations.");
        await llm.Received(2).SendChatAsync(
            Arg.Any<string>(),
            Arg.Any<List<(string role, string content)>>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_AppOnlyPromptAllowsProposalToolsWithoutPretendingAllToolsAreReadOnly()
    {
        var llm = new CapturingLlmClient("No tool call needed.");
        var runtime = new AgentRuntime(llm, [new ProposeCopilotActionTool()]);

        await runtime.RunAsync(
            null,
            [],
            "scan D downloads",
            [],
            "en",
            "key",
            null,
            enableThinking: false,
            onThinkingToken: null,
            CancellationToken.None);

        llm.LastSystemPrompt.Should().Contain("Tool calls can return observations or user-confirmed proposals");
        llm.LastSystemPrompt.Should().NotContain("Tools are read-only and query only the in-memory scan tree");
        llm.LastMessages.Should().ContainSingle(message =>
            message.role == "user" &&
            message.content.Contains("App-level proposal tools may be used for directly executable scan proposals or confirmation-required discovery cards") &&
            !message.content.Contains("Answer explanatory app-guide or identity questions only"));
    }

    [Fact]
    public async Task RunAsync_SystemPromptExplainsPathToolBoundary()
    {
        var llm = new CapturingLlmClient("ok");
        var runtime = new AgentRuntime(llm, [new ResolvePathTool(), new ProposeCopilotActionTool()]);

        await runtime.RunAsync(
            null,
            [],
            "scan %USERPROFILE%",
            [],
            "en",
            "key",
            null,
            enableThinking: false,
            onThinkingToken: null,
            CancellationToken.None);

        llm.LastSystemPrompt.Should().Contain("Capability boundary");
        llm.LastSystemPrompt.Should().Contain("call resolve_path first");
        llm.LastSystemPrompt.Should().Contain("Use resolved_path in later tool calls");
        llm.LastSystemPrompt.Should().Contain("can_scan=true");
        llm.LastSystemPrompt.Should().Contain("do not propose scanning");
    }

    [Fact]
    public async Task RunAsync_SystemPromptAllowsAgentSkillDiscoveryWithoutKeywordRouting()
    {
        var llm = new CapturingLlmClient("ok");
        var runtime = new AgentRuntime(llm, [new ManageDiskSkillsTool(new SpaceMonger.Core.Services.Copilot.FileSkillPromptProvider())]);

        await runtime.RunAsync(
            null,
            [],
            @"推荐清理 D:\Downloads",
            [],
            "zh-CN",
            "key",
            null,
            enableThinking: false,
            onThinkingToken: null,
            CancellationToken.None);

        llm.LastSystemPrompt.Should().Contain("use manage_disk_skills list/read to discover and read relevant built-in or user skills");
        llm.LastSystemPrompt.Should().Contain("create/update/delete only when the user explicitly asks to manage skills");
        llm.LastSystemPrompt.Should().Contain("will_overwrite_existing_data=true");
    }

    [Fact]
    public async Task RunAsync_SystemPromptTreatsConfiguredLanguageAsAppPolicy()
    {
        var llm = new CapturingLlmClient("ok");
        var runtime = new AgentRuntime(llm, []);

        await runtime.RunAsync(
            null,
            [],
            "帮我看看 C 盘能清啥",
            [],
            "en",
            "key",
            null,
            enableThinking: false,
            onThinkingToken: null,
            CancellationToken.None);

        llm.LastSystemPrompt.Should().Contain("Answer language: English.");
        llm.LastSystemPrompt.Should().Contain("Follow it even when the user's message is in another language.");
    }

    [Fact]
    public async Task RunAsync_AllowsAppProposalToolWithoutScanContext()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.SendChatAsync(
                Arg.Any<string>(),
                Arg.Any<List<(string role, string content)>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                _ => "{\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"propose_copilot_action\",\"arguments\":{\"kind\":\"scan\",\"path\":\"D:\\\\Downloads\",\"title\":\"Scan Downloads\",\"description\":\"Scan before analysis.\"}}]}",
                _ => "Please confirm the scan card.");
        var runtime = new AgentRuntime(llm, [new ProposeCopilotActionTool()]);

        var response = await runtime.RunAsync(
            null,
            [],
            "scan D downloads",
            [],
            "en",
            "key",
            null,
            enableThinking: false,
            onThinkingToken: null,
            CancellationToken.None);

        response.ToolResults.Should().ContainSingle(result => result.ToolName == "propose_copilot_action" && !result.IsError);
        response.Proposal.Should().NotBeNull();
        response.Proposal!.Value.GetProperty("action").GetProperty("kind").GetString().Should().Be("StartScan");
        response.Proposal.Value.GetProperty("action").GetProperty("path").GetString().Should().Be(@"D:\Downloads");
    }

    [Fact]
    public async Task RunAsync_AllowsSkillDefinedAppActionsWithoutScanContext()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.SendChatAsync(
                Arg.Any<string>(),
                Arg.Any<List<(string role, string content)>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                _ => "{\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"propose_copilot_action\",\"arguments\":{\"kind\":\"DiscoverUnityLibraries\",\"title\":\"Find Unity cleanup candidates\"}},{\"id\":\"call_2\",\"name\":\"propose_copilot_action\",\"arguments\":{\"kind\":\"clear_conversation\",\"title\":\"Clear chat\"}}]}",
                _ => "Please confirm the proposed app actions.");
        var runtime = new AgentRuntime(llm, [new ProposeCopilotActionTool()]);

        var response = await runtime.RunAsync(
            null,
            [],
            "use the skill to propose app-level actions",
            [],
            "en",
            "key",
            null,
            enableThinking: false,
            onThinkingToken: null,
            CancellationToken.None);

        response.ToolResults.Should().HaveCount(2);
        response.ToolResults.Should().OnlyContain(result => !result.IsError);
        response.ToolResults[0].Content.GetProperty("proposal").GetProperty("action").GetProperty("kind").GetString().Should().Be("DiscoverUnityLibraries");
        response.ToolResults[1].Content.GetProperty("proposal").GetProperty("action").GetProperty("kind").GetString().Should().Be("ClearConversation");
        response.Proposal!.Value.GetProperty("action").GetProperty("kind").GetString().Should().Be("ClearConversation");
    }

    [Fact]
    public async Task RunAsync_RejectsScanTreeToolWithIncompleteScanContext()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.SendChatAsync(
                Arg.Any<string>(),
                Arg.Any<List<(string role, string content)>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                _ => """{"tool_calls":[{"id":"call_1","name":"find_large_files","arguments":{}}]}""",
                _ => "Scan first.");
        var tool = new CountingTool("find_large_files");
        var runtime = new AgentRuntime(llm, [tool]);
        var context = new AgentContext(null, null, null, null, false);

        var response = await runtime.RunAsync(
            context,
            [],
            "largest files?",
            [],
            "en",
            "key",
            null,
            enableThinking: false,
            onThinkingToken: null,
            CancellationToken.None);

        tool.ExecuteCount.Should().Be(0);
        response.ToolResults.Should().ContainSingle(result =>
            result.ToolName == "find_large_files" &&
            result.IsError &&
            result.Content.GetProperty("error").GetProperty("code").GetString() == "scan_context_unavailable");
    }

    [Fact]
    public async Task RunAsync_RejectsScanTreeToolWithoutScanContext()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.SendChatAsync(
                Arg.Any<string>(),
                Arg.Any<List<(string role, string content)>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                _ => "{\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"find_large_files\",\"arguments\":{}}]}",
                _ => "Scan first.");
        var tool = new CountingTool("find_large_files");
        var runtime = new AgentRuntime(llm, [tool]);

        var response = await runtime.RunAsync(
            null,
            [],
            "largest files?",
            [],
            "en",
            "key",
            null,
            enableThinking: false,
            onThinkingToken: null,
            CancellationToken.None);

        tool.ExecuteCount.Should().Be(0);
        response.ToolResults.Should().ContainSingle(result =>
            result.ToolName == "find_large_files" &&
            result.IsError &&
            result.Content.GetProperty("error").GetProperty("code").GetString() == "scan_context_unavailable");
    }

    private sealed class CapturingLlmClient(string response) : ILlmClient
    {
        public string LastSystemPrompt { get; private set; } = string.Empty;
        public List<(string role, string content)> LastMessages { get; private set; } = [];

        public Task<string> SendChatAsync(
            string systemPrompt,
            List<(string role, string content)> messages,
            string apiKey,
            string? baseUrl,
            CancellationToken cancellationToken)
        {
            LastSystemPrompt = systemPrompt;
            LastMessages = messages;
            return Task.FromResult(response);
        }

        public Task<string> SendAnalysisAsync(string systemPrompt, string fileMetadataJson, string apiKey, string? baseUrl, string? modelName, bool enableThinking, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string> StreamChatAsync(string systemPrompt, List<(string role, string content)> messages, string apiKey, string? baseUrl, Action<string> onToken, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ChatResponse> StreamChatWithThinkingAsync(string systemPrompt, List<(string role, string content)> messages, string apiKey, string? baseUrl, bool enableThinking, Action<string>? onThinkingToken, Action<string>? onTextToken, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> ValidateApiKeyAsync(string apiKey, string? baseUrl)
            => throw new NotSupportedException();
    }

    private sealed class CountingTool(string name) : IAgentTool
    {
        public int ExecuteCount { get; private set; }
        public string Name { get; } = name;
        public string Description => "Test tool that must only run when the model explicitly calls it.";
        public ToolRiskLevel RiskLevel => ToolRiskLevel.ReadOnly;
        public JsonElement Schema { get; } = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });

        public Task<JsonElement> ExecuteAsync(AgentContext context, JsonElement arguments, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));
        }
    }
}

