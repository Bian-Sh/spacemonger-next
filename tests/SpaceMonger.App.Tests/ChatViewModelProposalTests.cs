using System.Text.Json;
using NSubstitute;
using FluentAssertions;
using SpaceMonger.App.Services.Copilot;
using SpaceMonger.App.ViewModels;
using SpaceMonger.App.Localization;
using SpaceMonger.Core.Models;
using SpaceMonger.Core.Enums;
using SpaceMonger.Core.Services.Chat;
using SpaceMonger.Core.Services.Settings;
using SpaceMonger.Core.Services.Copilot;
using SpaceMonger.Core.Services.Llm;

namespace SpaceMonger.App.Tests;

public class ChatViewModelProposalTests
{
    [Fact]
    public void ApplyProposalIfAny_CreatesInteractionCard()
    {
        var message = new ChatMessage();
        var proposal = JsonSerializer.SerializeToElement(new
        {
            action = new
            {
                kind = nameof(AiActionKind.StartScan),
                path = @"D:\Downloads",
                scope_label = @"D:\Downloads",
                will_overwrite_existing_data = false
            },
            card = new
            {
                title = "Scan this path",
                description = "Scan this path before deeper analysis.",
                impact = "Creates a new scan result.",
                confirm_text = "Start scan",
                cancel_text = "Cancel"
            }
        });

        ChatViewModel.ApplyProposalIfAny(message, proposal);

        message.InteractionCard.Should().NotBeNull();
        message.InteractionCard!.Title.Should().Be("Scan this path");
        message.InteractionCard.Action.Kind.Should().Be(AiActionKind.StartScan);
        message.InteractionCard.Action.Path.Should().Be(@"D:\Downloads");
    }
    [Fact]
    public void ApplyProposalIfAny_CarriesAgentAuthoredWorkflowSteps()
    {
        var message = new ChatMessage();
        var proposal = JsonSerializer.SerializeToElement(new
        {
            action = new
            {
                kind = nameof(AiActionKind.StartScan),
                path = @"D:\Downloads",
                will_overwrite_existing_data = true
            },
            workflow_active_step_id = "scan_path",
            workflow_steps = new[]
            {
                new { step_id = "validate_path", title = "校验路径" },
                new { step_id = "scan_path", title = "Scan target folder" },
                new { step_id = "analyze_recommendations", title = "Analyze cleanup recommendations" }
            },
            card = new
            {
                title = "Scan this path",
                description = "Scan before analysis."
            }
        });

        ChatViewModel.ApplyProposalIfAny(message, proposal);

        message.InteractionCard.Should().NotBeNull();
        message.InteractionCard!.WorkflowActiveStepId.Should().Be("scan_path");
        message.InteractionCard.WorkflowSteps.Should().HaveCount(3);
        message.InteractionCard.WorkflowSteps[1].Title.Should().Be("Scan target folder");
    }

    [Fact]
    public void ApplyProposalIfAny_AcceptsToolResultWrappedProposalAndSnakeCaseKind()
    {
        var message = new ChatMessage();
        var proposal = JsonSerializer.SerializeToElement(new
        {
            ok = true,
            proposal = new
            {
                action = new
                {
                    kind = "discover_unity_libraries",
                    scope_label = "Unity projects",
                    will_overwrite_existing_data = true
                },
                card = new
                {
                    title = "Start Scan",
                    description = "Scan all ready drives for Unity cleanup candidates.",
                    confirm_text = "Start Scan",
                    cancel_text = "Cancel"
                }
            }
        });

        ChatViewModel.ApplyProposalIfAny(message, proposal);

        message.InteractionCard.Should().NotBeNull();
        message.InteractionCard!.Action.Kind.Should().Be(AiActionKind.DiscoverUnityLibraries);
        message.InteractionCard.Action.WillOverwriteExistingData.Should().BeTrue();
    }

    [Fact]
    public async Task SendCommand_ForModelClearProposal_ShowsClearConfirmationCard()
    {
        var chatService = Substitute.For<IChatService>();
        chatService.StreamSkillMessageWithThinkingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<AiSkill>>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse("I prepared a clear-chat card.", string.Empty, CreateClearConversationProposal()));
        var viewModel = CreateViewModel(chatService);
        viewModel.InputText = "clear chat";

        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.Messages.Should().Contain(message => message.Sender == ChatSender.User);
        var card = viewModel.PendingInteractionCard;
        card.Should().NotBeNull();
        card!.Action.Kind.Should().Be(AiActionKind.ClearConversation);
        card.Title.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ConfirmInteraction_ForClearConversation_ClearsMessagesAndHistory()
    {
        var chatService = Substitute.For<IChatService>();
        chatService.StreamSkillMessageWithThinkingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<AiSkill>>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse("I prepared a clear-chat card.", string.Empty, CreateClearConversationProposal()));
        var viewModel = CreateViewModel(chatService);
        viewModel.InputText = "context is messy";
        await viewModel.SendCommand.ExecuteAsync(null);
        var card = viewModel.PendingInteractionCard!;

        await viewModel.ConfirmInteractionCommand.ExecuteAsync(card);

        viewModel.Messages.Should().BeEmpty();
        chatService.Received(1).ClearHistory();
    }


    [Fact]
    public void InputText_WhenSlash_ShowsCommandSuggestions()
    {
        var viewModel = CreateViewModel();

        viewModel.InputText = "/";

        viewModel.IsSlashCommandMenuOpen.Should().BeTrue();
        viewModel.SlashCommandSuggestions.Select(item => item.Command).Should().Contain(["/new", "/clear", "/clear console"]);
        viewModel.SlashCommandSuggestions.Should().OnlyContain(item => !string.IsNullOrWhiteSpace(item.Description));
    }


    [Fact]
    public void InputText_WhenAt_ShowsSkillMentionSuggestions()
    {
        var viewModel = CreateViewModel();

        viewModel.InputText = "@";

        viewModel.IsSkillMentionMenuOpen.Should().BeTrue();
        viewModel.SkillMentionSuggestions.Select(item => item.Mention).Should().Contain(["@app-guide", "@disk-management", "@path-cleanup-recommendation", "@unity-project-cleanup"]);
        viewModel.SelectedSkillMentionSuggestion.Should().NotBeNull();
    }

    [Fact]
    public void ConfirmActiveCompletion_ForSkillMention_InsertsMention()
    {
        var viewModel = CreateViewModel();
        viewModel.InputText = "@";

        viewModel.MoveCompletionSelection(1);
        var handled = viewModel.ConfirmActiveCompletion();

        handled.Should().BeTrue();
        viewModel.InputText.Should().Be("@disk-management ");
        viewModel.IsSkillMentionMenuOpen.Should().BeFalse();
    }

    [Fact]
    public void MoveCompletionSelection_ForFilteredSkillMention_NavigatesVisibleMatches()
    {
        var viewModel = CreateViewModel();
        viewModel.InputText = "@unity";

        viewModel.FilteredSkillMentionSuggestions.Should().ContainSingle(item => item.Mention == "@unity-project-cleanup");
        viewModel.MoveCompletionSelection(1);
        viewModel.SelectedSkillMentionSuggestion.Should().NotBeNull();
        viewModel.SelectedSkillMentionSuggestion!.Mention.Should().Be("@unity-project-cleanup");
    }

    [Fact]
    public async Task SendCommand_ForUnityLibraryDiscovery_UsesModelProposalCard()
    {
        var proposal = JsonSerializer.SerializeToElement(new
        {
            action = new
            {
                kind = nameof(AiActionKind.DiscoverUnityLibraries),
                scope_label = "Unity Library",
                will_overwrite_existing_data = false
            },
            card = new
            {
                title = "Discover Unity Library",
                description = "Discover cleanup candidates from Unity Library.",
                confirm_text = "Start Discovery",
                cancel_text = "Cancel"
            }
        });
        var chatService = Substitute.For<IChatService>();
        chatService.StreamSkillMessageWithThinkingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<AiSkill>>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse("I prepared a Unity discovery card.", string.Empty, proposal));
        var actionExecutor = Substitute.For<IAiDiskActionExecutor>();
        var routed = new AiSkillRoutingResult([]);
        var viewModel = CreateViewModel(chatService, routed);
        viewModel.SetActionExecutor(actionExecutor);
        viewModel.InputText = "clean Unity Library";

        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.PendingInteractionCard.Should().NotBeNull();
        viewModel.PendingInteractionCard!.Action.Kind.Should().Be(AiActionKind.DiscoverUnityLibraries);
        await actionExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<AiActionRequest>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IProgress<AiActionProgress>>());
    }
    [Fact]
    public void SlashCommandDescriptions_FollowCurrentLanguage()
    {
        try
        {
            L.SetLanguage("en-US");
            var englishViewModel = CreateViewModel();
            englishViewModel.SlashCommandSuggestions.Single(item => item.Command == "/clear console")
                .Description.Should().Be("Clear the console log shown in the app");

            L.SetLanguage("zh-CN");
            var chineseViewModel = CreateViewModel();
            var chineseDescription = chineseViewModel.SlashCommandSuggestions.Single(item => item.Command == "/clear").Description;
            chineseDescription.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            L.SetLanguage("en-US");
        }
    }

    [Fact]
    public async Task SendCommand_ForSlashClearConsole_RaisesConsoleClearOnly()
    {
        var chatService = Substitute.For<IChatService>();
        var viewModel = CreateViewModel(chatService);
        var clearConsoleCount = 0;
        viewModel.ClearConsoleRequested += () => clearConsoleCount++;
        viewModel.Messages.Add(new ChatMessage { Text = "old" });
        viewModel.InputText = "/clear console";

        await viewModel.SendCommand.ExecuteAsync(null);

        clearConsoleCount.Should().Be(1);
        viewModel.Messages.Should().ContainSingle(message => message.Text == "old");
        viewModel.InputText.Should().BeNull();
        chatService.DidNotReceive().ClearHistory();
    }

    [Fact]
    public async Task SendCommand_ForSlashClear_ClearsImmediatelyWithoutChatBubble()
    {
        var chatService = Substitute.For<IChatService>();
        var viewModel = CreateViewModel(chatService);
        viewModel.Messages.Add(new ChatMessage { Text = "old" });
        viewModel.InputText = "/clear";

        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.Messages.Should().BeEmpty();
        viewModel.IsSlashCommandMenuOpen.Should().BeFalse();
        chatService.Received(1).ClearHistory();
    }

    [Fact]
    public async Task SendCommand_ForSlashNew_KeepsMessagesAndStartsFreshContext()
    {
        var chatService = Substitute.For<IChatService>();
        var viewModel = CreateViewModel(chatService);
        viewModel.Messages.Add(new ChatMessage { Sender = ChatSender.User, Text = "old" });
        viewModel.InputText = "/new";

        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.Messages.Should().HaveCount(2);
        viewModel.Messages[0].Text.Should().Be("old");
        viewModel.Messages[1].Sender.Should().Be(ChatSender.System);
        viewModel.Messages[1].Text.Should().NotBeNullOrWhiteSpace();
        viewModel.IsSlashCommandMenuOpen.Should().BeFalse();
        chatService.Received(1).ClearHistory();
    }



    [Fact]
    public async Task SendCommand_ForScanProposal_WithExistingPath_ExecutesScanWithoutConfirmationCard()
    {
        using var temp = new TempScanRoot();
        var proposal = JsonSerializer.SerializeToElement(new
        {
            action = new
            {
                kind = nameof(AiActionKind.StartScan),
                path = temp.Path,
                will_overwrite_existing_data = false
            },
            card = new
            {
                title = "Scan temp root",
                description = "Scan before analysis.",
                confirm_text = "Start scan",
                cancel_text = "Cancel"
            }
        });
        var chatService = Substitute.For<IChatService>();
        chatService.StreamSkillMessageWithThinkingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<AiSkill>>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse("I prepared a scan card.", string.Empty, proposal));
        var actionExecutor = Substitute.For<IAiDiskActionExecutor>();
        actionExecutor.ExecuteAsync(Arg.Any<AiActionRequest>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<AiActionProgress>>())
            .Returns(AiActionResult.Ok("scan complete"));
        var viewModel = CreateViewModel(chatService, new AiSkillRoutingResult([]));
        viewModel.SetActionExecutor(actionExecutor);
        viewModel.InputText = "scan temp root";

        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.PendingInteractionCard.Should().BeNull();
        viewModel.Messages.Last().Text.Should().Be("I prepared a scan card.");
        viewModel.Messages.Last().Thinking.Should().BeEmpty();
        viewModel.Messages.Last().OperationResultText.Should().Contain("\u626b\u63cf\u5b8c\u6210");
        await actionExecutor.Received(1).ExecuteAsync(
            Arg.Is<AiActionRequest>(request => request.Kind == AiActionKind.StartScan && request.Path == temp.Path),
            Arg.Any<CancellationToken>(),
            Arg.Any<IProgress<AiActionProgress>>());
    }
    [Fact]
    public async Task SendCommand_ForDirectScanProposal_UsesAgentWorkflowActiveStep()
    {
        using var temp = new TempScanRoot();
        var proposal = JsonSerializer.SerializeToElement(new
        {
            action = new
            {
                kind = nameof(AiActionKind.StartScan),
                path = temp.Path,
                will_overwrite_existing_data = false
            },
            workflow_active_step_id = "scan_path",
            workflow_steps = new[]
            {
                new { step_id = "validate_path", title = "校验路径" },
                new { step_id = "check_existing_data", title = "检查已有数据" },
                new { step_id = "scan_path", title = "Scan target folder" },
                new { step_id = "analyze_recommendations", title = "Analyze cleanup recommendations" },
                new { step_id = "finish", title = "完成" }
            }
        });
        var chatService = Substitute.For<IChatService>();
        chatService.StreamSkillMessageWithThinkingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<AiSkill>>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse("Scanning.", string.Empty, proposal));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actionExecutor = Substitute.For<IAiDiskActionExecutor>();
        actionExecutor.ExecuteAsync(Arg.Any<AiActionRequest>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<AiActionProgress>>())
            .Returns(async _ =>
            {
                started.SetResult();
                await release.Task;
                return AiActionResult.Ok("scan complete");
            });
        var viewModel = CreateViewModel(chatService, new AiSkillRoutingResult([]));
        viewModel.SetActionExecutor(actionExecutor);
        viewModel.InputText = "scan temp root";

        var sendTask = viewModel.SendCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.ShouldShowWorkflowStepIndicator.Should().BeTrue();
        viewModel.CurrentWorkflowStepNumber.Should().Be(3);
        viewModel.WorkflowSteps[0].Status.Should().Be(CopilotWorkflowStepStatus.Finished);
        viewModel.WorkflowSteps[1].Status.Should().Be(CopilotWorkflowStepStatus.Finished);
        viewModel.WorkflowSteps[2].Status.Should().Be(CopilotWorkflowStepStatus.Running);

        release.SetResult();
        await sendTask;
    }


    [Fact]
    public async Task SendCommand_ForScanProposalWithAgentFollowUp_QueuesAgentProvidedPromptAfterDirectScan()
    {
        using var temp = new TempScanRoot();
        var proposal = JsonSerializer.SerializeToElement(new
        {
            action = new
            {
                kind = nameof(AiActionKind.StartScan),
                path = temp.Path,
                will_overwrite_existing_data = false
            },
            card = new
            {
                follow_up_prompt = "continue with cleanup recommendation analysis"
            }
        });
        var analysisProposal = JsonSerializer.SerializeToElement(new
        {
            action = new
            {
                kind = nameof(AiActionKind.AnalyzeCleanup),
                path = temp.Path,
                will_overwrite_existing_data = false
            },
            workflow_active_step_id = "analyze_recommendations",
            workflow_steps = new[]
            {
                new { step_id = "scan_path", title = "Scan target folder" },
                new { step_id = "analyze_recommendations", title = "Analyze cleanup recommendations" }
            }
        });
        var chatService = Substitute.For<IChatService>();
        chatService.StreamSkillMessageWithThinkingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<AiSkill>>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse("I prepared a scan.", string.Empty, proposal),
                new ChatResponse("Analysis follow-up reached.", string.Empty, analysisProposal));
        var actionExecutor = Substitute.For<IAiDiskActionExecutor>();
        actionExecutor.ExecuteAsync(Arg.Any<AiActionRequest>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<AiActionProgress>>())
            .Returns(call =>
            {
                var request = call.Arg<AiActionRequest>();
                return request.Kind == AiActionKind.AnalyzeCleanup
                    ? AiActionResult.Ok("analysis complete", "generated 2 recommendations")
                    : AiActionResult.Ok("scan complete");
            });
        var viewModel = CreateViewModel(chatService, new AiSkillRoutingResult([]));
        viewModel.SetActionExecutor(actionExecutor);
        viewModel.InputText = "scan temp root";

        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.Messages.Should().HaveCount(2);
        var assistant = viewModel.Messages.Last();
        assistant.Sender.Should().Be(ChatSender.Assistant);
        assistant.Text.Should().Contain("I prepared a scan.");
        assistant.Text.Should().Contain("Analysis follow-up reached.");
        assistant.OperationResultText.Should().Contain("扫描完成");
        assistant.OperationResultText.Should().Contain("analysis complete");
        assistant.OperationResultText.Should().Contain("generated 2 recommendations");

        await chatService.Received(1).StreamSkillMessageWithThinkingAsync(
            Arg.Is<string>(prompt => prompt.StartsWith("continue with cleanup recommendation analysis", StringComparison.Ordinal)),
            Arg.Any<IReadOnlyList<AiSkill>>(),
            Arg.Any<string?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Any<Action<string>?>(),
            Arg.Any<Action<string>?>(),
            Arg.Any<CancellationToken>());
    }
    [Fact]
    public async Task SendCommand_ForScanProposalWithOverwrite_ShowsConfirmationCard()
    {
        using var temp = new TempScanRoot();
        var proposal = JsonSerializer.SerializeToElement(new
        {
            action = new
            {
                kind = nameof(AiActionKind.StartScan),
                path = temp.Path,
                will_overwrite_existing_data = true
            },
            card = new
            {
                title = "Replace scan data?",
                description = "Existing scan data will be replaced.",
                confirm_text = "Overwrite",
                cancel_text = "Cancel"
            }
        });
        var chatService = Substitute.For<IChatService>();
        chatService.StreamSkillMessageWithThinkingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<AiSkill>>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse("I prepared a scan confirmation.", string.Empty, proposal));
        var actionExecutor = Substitute.For<IAiDiskActionExecutor>();
        var viewModel = CreateViewModel(chatService, new AiSkillRoutingResult([]));
        viewModel.SetActionExecutor(actionExecutor);
        viewModel.InputText = "scan temp root and replace current scan";

        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.PendingInteractionCard.Should().NotBeNull();
        viewModel.PendingInteractionCard!.Action.Kind.Should().Be(AiActionKind.StartScan);
        viewModel.PendingInteractionCard.Action.WillOverwriteExistingData.Should().BeTrue();
        await actionExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<AiActionRequest>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IProgress<AiActionProgress>>());
    }

    [Fact]
    public async Task SendCommand_ForScanProposalWithoutCard_ExecutesScanWithoutConfirmationCard()
    {
        using var temp = new TempScanRoot();
        var proposal = JsonSerializer.SerializeToElement(new
        {
            action = new
            {
                kind = nameof(AiActionKind.StartScan),
                path = temp.Path,
                will_overwrite_existing_data = false
            }
        });
        var chatService = Substitute.For<IChatService>();
        chatService.StreamSkillMessageWithThinkingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<AiSkill>>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse("I prepared a scan card.", string.Empty, proposal));
        var actionExecutor = Substitute.For<IAiDiskActionExecutor>();
        actionExecutor.ExecuteAsync(Arg.Any<AiActionRequest>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<AiActionProgress>>())
            .Returns(AiActionResult.Ok("scan complete"));
        var viewModel = CreateViewModel(chatService, new AiSkillRoutingResult([]));
        viewModel.SetActionExecutor(actionExecutor);
        viewModel.InputText = "扫描 " + temp.Path;

        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.PendingInteractionCard.Should().BeNull();
        viewModel.Messages.Last().InteractionCard.Should().BeNull();
        viewModel.Messages.Last().Text.Should().Be("I prepared a scan card.");
        viewModel.Messages.Last().Thinking.Should().BeEmpty();
        viewModel.Messages.Last().OperationResultText.Should().Contain("\u626b\u63cf\u5b8c\u6210");
        viewModel.Messages.Last().OperationResultText.Should().NotContain("card");
        await actionExecutor.Received(1).ExecuteAsync(
            Arg.Is<AiActionRequest>(request => request.Kind == AiActionKind.StartScan && request.Path == temp.Path),
            Arg.Any<CancellationToken>(),
            Arg.Any<IProgress<AiActionProgress>>());
    }

    [Fact]
    public async Task SendCommand_ForCleanupAnalysisProposalWithoutOverwrite_ExecutesWithoutConfirmationCard()
    {
        var proposal = JsonSerializer.SerializeToElement(new
        {
            action = new
            {
                kind = nameof(AiActionKind.AnalyzeCleanup),
                scope_label = "current scan",
                will_overwrite_existing_data = false
            },
            card = new
            {
                title = "Analyze cleanup recommendations",
                description = "Analyze cleanup candidates.",
                confirm_text = "Start analysis",
                cancel_text = "Cancel"
            }
        });
        var chatService = Substitute.For<IChatService>();
        chatService.StreamSkillMessageWithThinkingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<AiSkill>>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse("I prepared an analysis card. This should not be shown as a cleanup report.", string.Empty, proposal));
        var actionExecutor = Substitute.For<IAiDiskActionExecutor>();
        actionExecutor.ExecuteAsync(Arg.Any<AiActionRequest>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<AiActionProgress>>())
            .Returns(AiActionResult.Ok("analysis complete", "3 candidates"));
        var viewModel = CreateViewModel(chatService, new AiSkillRoutingResult([]));
        viewModel.SetActionExecutor(actionExecutor);
        viewModel.InputText = "analyze cleanup recommendations";

        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.PendingInteractionCard.Should().BeNull();
        viewModel.Messages.Last().InteractionCard.Should().BeNull();
        viewModel.Messages.Last().Text.Should().Be("I prepared an analysis card. This should not be shown as a cleanup report.");
        viewModel.Messages.Last().OperationResultText.Should().Contain("analysis complete");
        viewModel.Messages.Last().OperationResultText.Should().Contain("3 candidates");
        viewModel.Messages.Last().OperationResultText.Should().NotContain("cleanup report");
        await actionExecutor.Received(1).ExecuteAsync(
            Arg.Is<AiActionRequest>(request => request.Kind == AiActionKind.AnalyzeCleanup),
            Arg.Any<CancellationToken>(),
            Arg.Any<IProgress<AiActionProgress>>());
    }

    [Fact]
    public async Task SendCommand_ForCleanupAnalysisProposalWithOverwrite_ShowsConfirmationCard()
    {
        var proposal = JsonSerializer.SerializeToElement(new
        {
            action = new
            {
                kind = nameof(AiActionKind.AnalyzeCleanup),
                scope_label = "current scan",
                will_overwrite_existing_data = true
            },
            card = new
            {
                title = "Analyze cleanup recommendations",
                description = "Analyze cleanup candidates.",
                impact = "Existing recommendations will be replaced.",
                confirm_text = "Start analysis",
                cancel_text = "Cancel"
            }
        });
        var chatService = Substitute.For<IChatService>();
        chatService.StreamSkillMessageWithThinkingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<AiSkill>>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse("Existing recommendations need confirmation.", string.Empty, proposal));
        var actionExecutor = Substitute.For<IAiDiskActionExecutor>();
        var viewModel = CreateViewModel(chatService, new AiSkillRoutingResult([]));
        viewModel.SetActionExecutor(actionExecutor);
        viewModel.InputText = "analyze cleanup recommendations";

        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.PendingInteractionCard.Should().NotBeNull();
        viewModel.PendingInteractionCard!.Action.Kind.Should().Be(AiActionKind.AnalyzeCleanup);
        viewModel.PendingInteractionCard.Action.WillOverwriteExistingData.Should().BeTrue();
        await actionExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<AiActionRequest>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IProgress<AiActionProgress>>());
    }

    [Fact]
    public async Task ConfirmInteraction_PassesCardUserNotesToExecutor()
    {
        var chatService = Substitute.For<IChatService>();
        var viewModel = CreateViewModel(chatService);
        var actionExecutor = Substitute.For<IAiDiskActionExecutor>();
        actionExecutor.ExecuteAsync(Arg.Any<AiActionRequest>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<AiActionProgress>>())
            .Returns(AiActionResult.Ok("discovery complete"));
        viewModel.SetActionExecutor(actionExecutor);
        var card = new AiInteractionCard
        {
            Title = "Discover cleanup candidates",
            Description = "Discover Unity cleanup candidates.",
            Action = new AiActionRequest(AiActionKind.DiscoverUnityLibraries),
            UserNotes = "用户备注：只处理 D: 和 E:"
        };
        viewModel.PendingInteractionCard = card;

        await viewModel.ConfirmInteractionCommand.ExecuteAsync(card);

        await actionExecutor.Received(1).ExecuteAsync(
            Arg.Is<AiActionRequest>(request => request.Kind == AiActionKind.DiscoverUnityLibraries && request.UserNotes == "用户备注：只处理 D: 和 E:"),
            Arg.Any<CancellationToken>(),
            Arg.Any<IProgress<AiActionProgress>>());
    }

    [Fact]
    public async Task SendCommand_ForScanProposal_WithMissingPath_ReportsErrorWithoutConfirmationCard()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "spacemonger-missing-" + Guid.NewGuid().ToString("N"));
        var proposal = JsonSerializer.SerializeToElement(new
        {
            action = new
            {
                kind = nameof(AiActionKind.StartScan),
                path = missingPath,
                will_overwrite_existing_data = false
            },
            card = new
            {
                title = "Scan missing root",
                description = "Scan before analysis.",
                confirm_text = "Start scan",
                cancel_text = "Cancel"
            }
        });
        var chatService = Substitute.For<IChatService>();
        chatService.StreamSkillMessageWithThinkingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<AiSkill>>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse("I prepared a scan card.", string.Empty, proposal));
        var actionExecutor = Substitute.For<IAiDiskActionExecutor>();
        var viewModel = CreateViewModel(chatService, new AiSkillRoutingResult([]));
        viewModel.SetActionExecutor(actionExecutor);
        viewModel.InputText = "scan missing root";

        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.PendingInteractionCard.Should().BeNull();
        viewModel.Messages.Last().IsError.Should().BeTrue();
        viewModel.Messages.Last().Text.Should().Be("I prepared a scan card.");
        viewModel.Messages.Last().OperationResultText.Should().Contain("\u8def\u5f84\u4e0d\u5b58\u5728");
        await actionExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<AiActionRequest>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IProgress<AiActionProgress>>());
    }

    [Fact]
    public async Task SubmitOrStop_WhenDirectScanIsRunning_CancelsExecutorToken()
    {
        using var temp = new TempScanRoot();
        var proposal = JsonSerializer.SerializeToElement(new
        {
            action = new
            {
                kind = nameof(AiActionKind.StartScan),
                path = temp.Path,
                will_overwrite_existing_data = false
            },
            card = new
            {
                title = "Scan temp root",
                description = "Scan before analysis.",
                confirm_text = "Start scan",
                cancel_text = "Cancel"
            }
        });
        var chatService = Substitute.For<IChatService>();
        chatService.StreamSkillMessageWithThinkingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<AiSkill>>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse("I prepared a scan card.", string.Empty, proposal));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actionExecutor = Substitute.For<IAiDiskActionExecutor>();
        actionExecutor.ExecuteAsync(Arg.Any<AiActionRequest>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<AiActionProgress>>())
            .Returns(async call =>
            {
                var token = call.Arg<CancellationToken>();
                using var registration = token.Register(() => cancelled.TrySetResult());
                started.SetResult();
                await Task.Delay(TimeSpan.FromSeconds(10), token);
                return AiActionResult.Ok("scan complete");
            });
        var viewModel = CreateViewModel(chatService, new AiSkillRoutingResult([]));
        viewModel.SetActionExecutor(actionExecutor);
        viewModel.InputText = "scan temp root";

        var sendTask = viewModel.SendCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await viewModel.SubmitOrStopCommand.ExecuteAsync(null);

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await sendTask;
        viewModel.Messages.Last().Text.Should().Contain("\u5df2\u505c\u6b62");
    }

    [Fact]
    public async Task ConfirmInteraction_HidesOverlayCardImmediatelyAndUsesWorkflowStepsForProgress()
    {
        var actionExecutor = Substitute.For<IAiDiskActionExecutor>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        actionExecutor.ExecuteAsync(Arg.Any<AiActionRequest>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<AiActionProgress>>())
            .Returns(async _ =>
            {
                started.SetResult();
                await release.Task;
                return AiActionResult.Ok("scan done");
            });
        var viewModel = CreateViewModel();
        viewModel.SetActionExecutor(actionExecutor);
        var card = new AiInteractionCard
        {
            Title = "Start a scan to analyze disk space",
            Description = "Drive C:",
            Action = new AiActionRequest(AiActionKind.StartScan, Path: @"C:", ScopeLabel: "Drive C:")
        };
        viewModel.PendingInteractionCard = card;

        var confirmTask = viewModel.ConfirmInteractionCommand.ExecuteAsync(card);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.PendingInteractionCard.Should().BeNull();
        viewModel.IsWorkflowProgressVisible.Should().BeTrue();
        viewModel.WorkflowSteps.Should().ContainSingle(step => step.Title.Contains("Drive C:"));

        release.SetResult();
        await confirmTask;
    }

    [Fact]
    public async Task SendThenConfirm_WrappedDiscoveryProposalShowsWorkflowSteps()
    {
        var proposal = JsonSerializer.SerializeToElement(new
        {
            ok = true,
            proposal = new
            {
                action = new
                {
                    kind = "discover_unity_libraries",
                    scope_label = "Unity projects",
                    will_overwrite_existing_data = false
                },
                card = new
                {
                    title = "Start Scan",
                    description = "Scan ready drives for Unity cleanup candidates.",
                    confirm_text = "Start Scan",
                    cancel_text = "Cancel"
                }
            }
        });
        var chatService = Substitute.For<IChatService>();
        chatService.StreamSkillMessageWithThinkingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<AiSkill>>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse("Ready.", string.Empty, proposal));
        var actionExecutor = Substitute.For<IAiDiskActionExecutor>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        actionExecutor.ExecuteAsync(Arg.Any<AiActionRequest>(), Arg.Any<CancellationToken>(), Arg.Any<IProgress<AiActionProgress>>())
            .Returns(async _ =>
            {
                started.SetResult();
                await release.Task;
                return AiActionResult.Ok("done");
            });
        var viewModel = CreateViewModel(chatService, new AiSkillRoutingResult([new AiSkill("unity-project-cleanup", "Unity", "Prompt")]));
        viewModel.SetActionExecutor(actionExecutor);
        viewModel.InputText = "discover unity cleanup candidates";

        await viewModel.SendCommand.ExecuteAsync(null);
        var card = viewModel.PendingInteractionCard;
        card.Should().NotBeNull();

        var confirmTask = viewModel.ConfirmInteractionCommand.ExecuteAsync(card);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.PendingInteractionCard.Should().BeNull();
        viewModel.IsWorkflowProgressVisible.Should().BeTrue();
        viewModel.WorkflowSteps.Should().NotBeEmpty();
        viewModel.WorkflowProgressText.Should().Contain("/");

        release.SetResult();
        await confirmTask;
    }

    [Fact]
    public async Task CancelInteraction_HidesOverlayCardWithoutStartingFollowUpFlow()
    {
        var chatService = Substitute.For<IChatService>();
        var viewModel = CreateViewModel(chatService);
        var card = new AiInteractionCard
        {
            Title = "Start a scan to analyze disk space",
            Description = "Drive C:",
            FollowUpPrompt = "continue after scan",
            Action = new AiActionRequest(AiActionKind.StartScan, Path: @"C:", ScopeLabel: "Drive C:")
        };
        viewModel.PendingInteractionCard = card;

        viewModel.CancelInteractionCommand.Execute(card);
        await Task.Delay(50);

        viewModel.PendingInteractionCard.Should().BeNull();
        viewModel.IsWorkflowProgressVisible.Should().BeFalse();
        await chatService.DidNotReceive().StreamSkillMessageWithThinkingAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<AiSkill>>(),
            Arg.Any<string?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Any<Action<string>?>(),
            Arg.Any<Action<string>?>(),
            Arg.Any<CancellationToken>());
    }


    [Fact]
    public async Task SendCommand_WithExplicitSkillDoesNotShowHostWorkflowSteps()
    {
        var chatService = Substitute.For<IChatService>();
        chatService.StreamSkillMessageWithThinkingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<AiSkill>>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse("Done.", string.Empty, null));
        var viewModel = CreateViewModel(chatService, new AiSkillRoutingResult([new AiSkill("unity-project-cleanup", "Unity", "Prompt")])
        {
            SelectedSkillIds = ["unity-project-cleanup"]
        });
        viewModel.InputText = "@unity-project-cleanup clean Library";

        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.IsWorkflowProgressVisible.Should().BeFalse();
        viewModel.WorkflowSteps.Should().BeEmpty();
    }
    [Fact]
    public async Task SendCommand_UsesConfiguredEnglishLanguageBeforeUserMessageLanguage()
    {
        var chatService = Substitute.For<IChatService>();
        chatService.StreamSkillMessageWithThinkingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<AiSkill>>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse("ok", string.Empty, null));
        var viewModel = CreateViewModel(chatService, settings: new AppSettings { Language = "en" });
        viewModel.InputText = "scan drive C";

        await viewModel.SendCommand.ExecuteAsync(null);

        await chatService.Received(1).StreamSkillMessageWithThinkingAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<AiSkill>>(),
            "en",
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Any<Action<string>?>(),
            Arg.Any<Action<string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmInteraction_WaitsForSlowVirtualScanInsteadOfTimingOut()
    {
        var virtualExecutor = new VirtualScanActionExecutor();
        virtualExecutor.AddDrive(@"C:\", VirtualDriveFactory.CreateUnityProjectDrive(@"C:\", "ArcadeGame"));
        virtualExecutor.AddDrive(@"D:\", VirtualDriveFactory.CreateCacheDrive(@"D:\", "Steam"));
        virtualExecutor.AddDrive(@"E:\", VirtualDriveFactory.CreateCacheDrive(@"E:\", "UnityHub"));
        virtualExecutor.Delay = TimeSpan.FromMilliseconds(150);
        var viewModel = CreateViewModel();
        viewModel.SetActionExecutor(virtualExecutor);
        var card = new AiInteractionCard
        {
            Title = "Kick off C scan",
            Description = "Virtual Drive C:",
            Action = new AiActionRequest(AiActionKind.StartScan, Path: @"C:\", ScopeLabel: "Drive C:")
        };
        viewModel.PendingInteractionCard = card;

        await viewModel.ConfirmInteractionCommand.ExecuteAsync(card);

        viewModel.PendingInteractionCard.Should().BeNull();
        virtualExecutor.ScanHistory.Should().ContainSingle(@"C:\");
        virtualExecutor.LastSession.Should().NotBeNull();
        virtualExecutor.LastSession!.RootEntry!.Children.Should().Contain(child => child.Name == "ArcadeGame");
    }

    [Fact]
    public async Task SendCommand_ForUnavailableScanTarget_AsksModelWithDriveContextWithoutExecutingAction()
    {
        var chatService = Substitute.For<IChatService>();
        chatService.StreamSkillMessageWithThinkingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<AiSkill>>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var onThinkingToken = callInfo.ArgAt<Action<string>?>(6);
                var onTextToken = callInfo.ArgAt<Action<string>?>(7);
                onThinkingToken?.Invoke("Checking available drives before proposing a scan.\n");
                onTextToken?.Invoke("\u65e0\u6cd5\u626b\u63cf Z:\\\\?\u8be5\u78c1\u76d8\u6216\u6587\u4ef6\u5939\u4e0d\u5b58\u5728\u3001\u672a\u6302\u8f7d\uff0c\u6216\u5f53\u524d\u4e0d\u53ef\u8bbf\u95ee\u3002");
                return Task.FromResult(new ChatResponse("ok", string.Empty, null));
            });
        var actionExecutor = Substitute.For<IAiDiskActionExecutor>();
        var routed = new AiSkillRoutingResult([]);
        var viewModel = CreateViewModel(chatService, routed);
        viewModel.SetActionExecutor(actionExecutor);
        viewModel.InputText = "scan Z drive";

        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.PendingInteractionCard.Should().BeNull();
        viewModel.IsWorkflowProgressVisible.Should().BeFalse();
        viewModel.WorkflowSteps.Should().BeEmpty();
        viewModel.Messages.Should().Contain(message =>
            message.Sender == ChatSender.Assistant &&
            !message.IsError &&
            message.Thinking.Contains("Checking available drives") &&
            message.Text.Contains(@"Z:") &&
            message.Text.Contains("\u65e0\u6cd5\u626b\u63cf"));
        await chatService.Received(1).StreamSkillMessageWithThinkingAsync(
            Arg.Is<string>(message =>
                message.Contains("Host disk context JSON") &&
                message.Contains("available_drives") &&
                message.Contains("scan Z drive")),
            Arg.Any<IReadOnlyList<AiSkill>>(),
            Arg.Any<string?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Any<Action<string>?>(),
            Arg.Any<Action<string>?>(),
            Arg.Any<CancellationToken>());
        await actionExecutor.DidNotReceive().ExecuteAsync(Arg.Any<AiActionRequest>(), Arg.Any<CancellationToken>());
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        condition().Should().BeTrue();
    }

    private static JsonElement CreateClearConversationProposal()
        => JsonSerializer.SerializeToElement(new
        {
            action = new
            {
                kind = nameof(AiActionKind.ClearConversation)
            },
            card = new
            {
                title = "Clear this chat window?",
                description = "Clear messages and Copilot context after confirmation.",
                confirm_text = "Clear Chat",
                cancel_text = "Cancel"
            }
        });

    private static ChatViewModel CreateViewModel(IChatService? chatService = null, AiSkillRoutingResult? routingResult = null, AppSettings? settings = null)
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.LoadSettings().Returns(settings ?? new AppSettings { Language = "zh-CN" });
        settingsService.GetApiKey(Arg.Any<AppSettings>()).Returns("test-key");
        settingsService.EncryptApiKey(Arg.Any<string>()).Returns([]);

        var router = Substitute.For<IAiSkillRouter>();
        router.GetSkillCatalog().Returns([
            new AiSkillCatalogItem("app-guide", "app-guide", "Guide the app"),
            new AiSkillCatalogItem("disk-management", "disk-management", "Manage disk space"),
            new AiSkillCatalogItem("path-cleanup-recommendation", "path-cleanup-recommendation", "Analyze cleanup recommendations for a path"),
            new AiSkillCatalogItem("unity-project-cleanup", "unity-project-cleanup", "Clean Unity projects")
        ]);
        router.Route(Arg.Any<string>(), Arg.Any<FileEntry?>(), Arg.Any<FileEntry?>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(routingResult ?? new AiSkillRoutingResult([]));

        return new ChatViewModel(chatService ?? Substitute.For<IChatService>(), settingsService, router);
    }

    private sealed class TempScanRoot : IDisposable
    {
        public TempScanRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "spacemonger-scan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class VirtualScanActionExecutor : IAiDiskActionExecutor
    {
        private readonly Dictionary<string, ScanSession> _drives = new(StringComparer.OrdinalIgnoreCase);
        public bool HasExistingRecommendations => false;
        public TimeSpan Delay { get; set; }
        public List<string> ScanHistory { get; } = [];
        public ScanSession? LastSession { get; private set; }

        public void AddDrive(string path, ScanSession session) => _drives[path] = session;

        public async Task<AiActionResult> ExecuteAsync(AiActionRequest request, CancellationToken cancellationToken, IProgress<AiActionProgress>? progress = null)
        {
            if (request.Kind != AiActionKind.StartScan || string.IsNullOrWhiteSpace(request.Path))
            {
                return AiActionResult.Fail("unsupported");
            }

            ScanHistory.Add(request.Path);
            progress?.Report(new AiActionProgress("scan", "Scanning virtual drive", AiActionProgressStatus.Running));
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            if (!_drives.TryGetValue(request.Path, out var session))
            {
                return AiActionResult.Fail("virtual drive not found", request.Path);
            }

            LastSession = session;
            progress?.Report(new AiActionProgress("scan", "Scanning virtual drive", AiActionProgressStatus.Completed));
            return AiActionResult.Ok("virtual scan complete", session.RootEntry?.Path);
        }
    }

    private static class VirtualDriveFactory
    {
        public static ScanSession CreateUnityProjectDrive(string rootPath, string projectName)
        {
            var root = Dir(rootPath, rootPath.TrimEnd('\\'));
            var project = Dir(Path.Combine(rootPath, projectName), projectName);
            AddChild(root, project);
            AddChild(project, Dir(Path.Combine(project.Path, "Assets"), "Assets"));
            AddChild(project, Dir(Path.Combine(project.Path, "ProjectSettings"), "ProjectSettings"));
            var library = Dir(Path.Combine(project.Path, "Library"), "Library");
            AddChild(project, library);
            AddChild(library, File(Path.Combine(library.Path, "artifact-cache.bin"), "artifact-cache.bin", 512 * 1024 * 1024));
            root.RecalculateSize();
            return Session(rootPath, root);
        }

        public static ScanSession CreateCacheDrive(string rootPath, string appName)
        {
            var root = Dir(rootPath, rootPath.TrimEnd('\\'));
            var app = Dir(Path.Combine(rootPath, appName), appName);
            AddChild(root, app);
            var cache = Dir(Path.Combine(app.Path, "Cache"), "Cache");
            AddChild(app, cache);
            AddChild(cache, File(Path.Combine(cache.Path, "blob.cache"), "blob.cache", 128 * 1024 * 1024));
            root.RecalculateSize();
            return Session(rootPath, root);
        }

        private static ScanSession Session(string targetPath, FileEntry root) => new()
        {
            TargetPath = targetPath,
            RootEntry = root,
            StartTime = DateTime.Now.AddSeconds(-1),
            EndTime = DateTime.Now,
            TotalFiles = root.SubtreeFileCount,
            TotalFolders = root.SubtreeFolderCount,
            TotalSize = root.Size
        };

        private static FileEntry Dir(string path, string name) => new()
        {
            Path = path,
            Name = name,
            IsDirectory = true,
            LastModified = DateTime.Now.AddDays(-7)
        };

        private static FileEntry File(string path, string name, long size) => new()
        {
            Path = path,
            Name = name,
            IsDirectory = false,
            Size = size,
            LastModified = DateTime.Now.AddDays(-7)
        };

        private static void AddChild(FileEntry parent, FileEntry child)
        {
            child.Parent = parent;
            child.Depth = parent.Depth + 1;
            parent.Children.Add(child);
        }
    }

}

