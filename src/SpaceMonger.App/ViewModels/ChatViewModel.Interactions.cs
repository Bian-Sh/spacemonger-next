using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SpaceMonger.App.Localization;
using SpaceMonger.Core.Models;
using SpaceMonger.Core.Services.Copilot;
using SpaceMonger.Core.Services.Llm;

namespace SpaceMonger.App.ViewModels;

public partial class ChatViewModel
{
    private async Task<bool> TryRunDirectActionProposalAsync(ChatMessage assistantMessage, JsonElement? proposal, AiSkillRoutingResult routed, CancellationToken cancellationToken)
    {
        if (!ChatProposalMapper.TryGetActionRequest(proposal, out var action) || !ShouldExecuteProposalDirectly(action, routed))
        {
            return false;
        }

        action = ChatProposalMapper.ResolveActionPath(action);

        if (action.Kind == AiActionKind.StartScan)
        {
            var path = action.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (!Directory.Exists(path))
            {
                assistantMessage.InteractionCard = null;
                assistantMessage.IsStreaming = false;
                assistantMessage.IsError = true;
                assistantMessage.OperationResultText = Localized($"Path not found or cannot be scanned: {path}", $"路径不存在或无法扫描：{path}");
                return true;
            }
        }

        assistantMessage.InteractionCard = null;
        var workflowActiveStepId = ChatProposalMapper.TryGetWorkflowActiveStepId(proposal, out var proposalActiveStepId) ? proposalActiveStepId : null;
        if (ChatProposalMapper.TryGetWorkflowSteps(proposal, out var proposalWorkflowSteps))
        {
            SetWorkflowPlan(proposalWorkflowSteps);
        }
        else
        {
            SetWorkflowPlan(BuildWorkflowSteps(action, null));
        }

        var workflowStepIndex = FindWorkflowStepIndex(workflowActiveStepId);
        StartWorkflowStep(workflowStepIndex);
        var progress = new Progress<AiActionProgress>(ApplyWorkflowProgress);
        var result = await _actionExecutor.ExecuteAsync(action, cancellationToken, progress);
        CompleteWorkflowStep(workflowStepIndex, result.Success);
        assistantMessage.IsError = !result.Success;
        AppendOperationResult(assistantMessage, FormatStepSectionHeader(assistantMessage, workflowStepIndex) + Environment.NewLine + FormatDirectActionResult(action, result));
        if (!result.Success)
        {
            ErrorMessage = assistantMessage.OperationResultText;
        }
        else if (action.Kind == AiActionKind.StartScan && ChatProposalMapper.TryGetFollowUpPrompt(proposal, out var followUpPrompt))
        {
            await ContinueAfterConfirmedScanAsync(followUpPrompt, assistantMessage, cancellationToken, "direct scan follow-up");
        }

        assistantMessage.IsStreaming = false;

        return true;
    }

    private static bool ShouldExecuteProposalDirectly(AiActionRequest action, AiSkillRoutingResult routed)
        => (action.Kind == AiActionKind.StartScan && !action.WillOverwriteExistingData
            || action.Kind == AiActionKind.AnalyzeCleanup && !action.WillOverwriteExistingData)
           && !routed.SelectedSkillIds.Any(id => id.Contains("unity", StringComparison.OrdinalIgnoreCase));

    private static string FormatDirectActionResult(AiActionRequest action, AiActionResult result)
    {
        if (!result.Success)
        {
            return result.Details is null ? result.Message : $"{result.Message}{Environment.NewLine}{result.Details}";
        }

        if (action.Kind == AiActionKind.StartScan && !string.IsNullOrWhiteSpace(action.Path))
        {
            return Localized($"Scan complete: {action.Path}", $"扫描完成：{action.Path}");
        }

        return result.Details is null ? result.Message : $"{result.Message}{Environment.NewLine}{result.Details}";
    }


    private void MoveMessageInteractionCardToInputOverlay(ChatMessage message)
    {
        if (message.InteractionCard is null) return;
        PendingInteractionCard = message.InteractionCard;
        _pendingInteractionSourceMessage = message;
        message.InteractionCard = null;
    }

    [RelayCommand]
    private async Task ConfirmInteractionAsync(AiInteractionCard? card)
    {
        if (card is null || !card.IsPending) return;
        card.IsBusy = true;
        card.Status = AiInteractionCardStatus.Running;
        card.StatusText = L.Text("CopilotCardRunning");
        if (ReferenceEquals(PendingInteractionCard, card))
        {
            PendingInteractionCard = null;
        }

        var cancellationToken = BeginActiveOperation();
        try
        {
            if (card.Action.Kind == AiActionKind.ClearConversation)
            {
                ClearConversation();
                return;
            }

            if (card.WorkflowSteps.Count > 0)
            {
                SetWorkflowPlan(card.WorkflowSteps);
            }
            else if (card.Action.Kind == AiActionKind.DiscoverUnityLibraries)
            {
                SetWorkflowPlan(BuildUnityDiscoveryWorkflowSteps(card.Action));
            }
            else
            {
                SetWorkflowPlan(BuildWorkflowSteps(card.Action, null));
            }

            var workflowStepIndex = FindWorkflowStepIndex(card.WorkflowActiveStepId);
            StartWorkflowStep(workflowStepIndex);
            var progress = new Progress<AiActionProgress>(ApplyWorkflowProgress);
            var action = WithCardUserNotes(card.Action, card.UserNotes);
            var result = await _actionExecutor.ExecuteAsync(action, cancellationToken, progress);
            if (card.WorkflowSteps.Count == 0 && card.Action.Kind == AiActionKind.DiscoverUnityLibraries)
            {
                MarkRunningWorkflowStep(result.Success);
            }
            else
            {
                CompleteWorkflowStep(workflowStepIndex, result.Success);
            }
            card.Status = result.Success ? AiInteractionCardStatus.Completed : AiInteractionCardStatus.Failed;
            card.StatusText = result.Details is null ? result.Message : $"{result.Message}\n{result.Details}";
            if (result.Success && card.Action.Kind == AiActionKind.StartScan && !string.IsNullOrWhiteSpace(card.FollowUpPrompt))
            {
                await ContinueAfterConfirmedScanAsync(card.FollowUpPrompt, _pendingInteractionSourceMessage, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Chat operation cancelled");
            card.Status = AiInteractionCardStatus.Cancelled;
            card.StatusText = Localized("Stopped.", "\u5df2\u505c\u6b62\u3002");
            MarkRunningWorkflowStep(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat operation failed");
            card.Status = AiInteractionCardStatus.Failed;
            card.StatusText = ex.Message;
        }
        finally
        {
            card.IsBusy = false;
            FinishActiveOperation();
            HideWorkflowProgress();
            if (card.IsFinished)
            {
                PendingInteractionCard = null;
                _pendingInteractionSourceMessage = null;
            }
        }
    }

    [RelayCommand]
    private void CancelInteraction(AiInteractionCard? card)
    {
        if (card is null || !card.IsPending) return;
        card.Status = AiInteractionCardStatus.Cancelled;
        card.StatusText = L.Text("CopilotCardCancelled");
        PendingInteractionCard = null;
        _pendingInteractionSourceMessage = null;
    }

    private static AiActionRequest WithCardUserNotes(AiActionRequest action, string? userNotes)
        => string.IsNullOrWhiteSpace(userNotes) ? action : action with { UserNotes = userNotes.Trim() };

    private async Task ContinueAfterConfirmedScanAsync(string? followUpPrompt, ChatMessage? message, CancellationToken cancellationToken, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(followUpPrompt))
        {
            return;
        }

        try
        {
            _logger.LogInformation("Chat follow-up running inside current assistant message; reason={Reason}", reason ?? "confirmed action");
            var settings = _settingsService.LoadSettings();
            var responseLanguage = ResolveResponseLanguage(settings.Language);
            var apiKey = _settingsService.GetApiKey(settings);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                if (message is not null)
                {
                    AppendOperationResult(message, Localized("Configure a model service API Key before using Copilot.", "需要先配置模型服务 API Key，才能使用 Copilot。"));
                }
                return;
            }

            var routed = _skillRouter.Route(followUpPrompt, LinkedEntry, _currentViewRoot, _actionExecutor.HasExistingRecommendations, responseLanguage);
            var modelUserInput = BuildModelUserInput(followUpPrompt, routed, LinkedEntry, LinkedRecommendation, _currentViewRoot, _currentSession, _actionExecutor.HasExistingRecommendations);
            var enableThinking = ShouldEnableThinking(settings, routed);
            ChatResponse response;
            if (_currentSession is not null && _currentViewRoot is not null)
            {
                response = await _chatService.StreamMessageWithThinkingAsync(
                    modelUserInput,
                    LinkedEntry,
                    LinkedRecommendation,
                    _currentViewRoot,
                    _currentSession,
                    _actionExecutor.HasExistingRecommendations,
                    routed.Skills,
                    responseLanguage,
                    apiKey,
                    settings.AnthropicBaseUrl,
                    enableThinking,
                    thinkingToken => { },
                    textToken => { },
                    cancellationToken);
            }
            else
            {
                response = await _chatService.StreamSkillMessageWithThinkingAsync(
                    modelUserInput,
                    routed.Skills,
                    responseLanguage,
                    apiKey,
                    settings.AnthropicBaseUrl,
                    enableThinking,
                    thinkingToken => { },
                    textToken => { },
                    cancellationToken);
            }

            if (message is not null)
            {
                var sectionHeader = FormatStepSectionHeader(message, response.Proposal);
                AppendAssistantThinking(message, response.Thinking, sectionHeader);
                AppendAssistantText(message, response.Text, sectionHeader);
            }

            if (message is not null && await TryRunDirectActionProposalAsync(message, response.Proposal, routed, cancellationToken))
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Chat follow-up cancelled; reason={Reason}", reason ?? "confirmed action");
        }
    }
}
