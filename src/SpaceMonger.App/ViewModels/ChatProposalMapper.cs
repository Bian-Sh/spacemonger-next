using System.IO;
using System.Text.Json;
using SpaceMonger.App.Localization;
using SpaceMonger.Core.Models;
using SpaceMonger.Core.Services.Copilot;
using SpaceMonger.Core.Services.Scanning;

namespace SpaceMonger.App.ViewModels;

internal static class ChatProposalMapper
{
    internal static AiInteractionCard BuildInteractionCard(AiActionRequest action, string followUpPrompt)
    {
        action = ResolveActionPath(action);
        var scope = action.ScopeLabel ?? action.Path ?? ChatViewModel.Localized("current scope", "当前范围");
        return action.Kind switch
        {
            AiActionKind.DiscoverUnityLibraries => new AiInteractionCard
            {
                Title = ChatViewModel.Localized("Discover cleanup candidates", "发现清理候选项"),
                Description = ChatViewModel.Localized("Scan ready drives one by one, detect candidates described by the selected skill, and write reviewable cleanup recommendations.", "依次扫描可用磁盘，按已选 skill 描述发现候选项，并写入可复核的清理建议。"),
                Impact = action.WillOverwriteExistingData
                    ? ChatViewModel.Localized("This replaces the current recommendations list. Actual deletion still requires another confirmation.", "这会替换当前推荐列表；真正删除仍需要再次确认。")
                    : ChatViewModel.Localized("The current TreeView/Treemap scan result stays unchanged. Actual deletion still requires another confirmation.", "当前 TreeView/Treemap 扫描结果保持不变；真正删除仍需要再次确认。"),
                ConfirmText = ChatViewModel.Localized("Start Discovery", "开始发现"),
                CancelText = L.Text("CopilotCardDefaultCancel"),
                FollowUpPrompt = followUpPrompt,
                Action = action
            },
            AiActionKind.StartScan => new AiInteractionCard
            {
                Title = ChatViewModel.Localized("Scan this path", "扫描这个路径"),
                Description = ChatViewModel.Localized($"Scan {scope} before analyzing its space usage.", $"需要先扫描 {scope}，才能继续分析里面的空间占用。"),
                Impact = ChatViewModel.Localized("This replaces the current scan result and refreshes Treemap, TreeView, and AI-readable space context.", "会替换当前扫描结果，并刷新 Treemap、TreeView 和 AI 可理解的空间上下文。"),
                ConfirmText = ChatViewModel.Localized("Start Scan", "开始扫描"),
                CancelText = L.Text("CopilotCardDefaultCancel"),
                FollowUpPrompt = followUpPrompt,
                Action = action
            },
            AiActionKind.AnalyzeCleanup => new AiInteractionCard
            {
                Title = ChatViewModel.Localized("Analyze cleanup recommendations", "分析清理建议"),
                Description = ChatViewModel.Localized($"Generate reviewable cleanup candidates for {scope}.", $"为 {scope} 生成可复核的清理候选项。"),
                Impact = action.WillOverwriteExistingData
                    ? ChatViewModel.Localized("This overwrites existing recommendations; actual cleanup still requires another confirmation.", "会覆盖现有推荐结果；真正清理仍需要你再次确认。")
                    : ChatViewModel.Localized("Actual cleanup still requires another confirmation.", "真正清理仍需要你再次确认。"),
                ConfirmText = ChatViewModel.Localized("Start Analysis", "开始分析"),
                CancelText = L.Text("CopilotCardDefaultCancel"),
                FollowUpPrompt = followUpPrompt,
                Action = action
            },
            _ => new AiInteractionCard
            {
                Title = L.Text("CopilotCardDefaultTitle"),
                Description = ChatViewModel.Localized($"Prepare to handle {scope}.", $"准备处理 {scope}。"),
                ConfirmText = L.Text("CopilotCardDefaultConfirm"),
                CancelText = L.Text("CopilotCardDefaultCancel"),
                FollowUpPrompt = followUpPrompt,
                Action = action
            }
        };
    }

    internal static void ApplyProposalIfAny(ChatMessage message, JsonElement? proposal)
    {
        if (!TryGetActionRequest(proposal, out var request)) return;
        if (!TryGetProposalRoot(proposal, out var root)) return;
        root.TryGetProperty("card", out var card);
        var hasCard = card.ValueKind == JsonValueKind.Object;

        message.InteractionCard = new AiInteractionCard
        {
            Title = hasCard ? GetString(card, "title") ?? L.Text("CopilotCardDefaultTitle") : L.Text("CopilotCardDefaultTitle"),
            Description = hasCard ? GetString(card, "description") ?? request.ScopeLabel ?? request.Path ?? L.Text("CopilotCardDefaultTitle") : request.ScopeLabel ?? request.Path ?? L.Text("CopilotCardDefaultTitle"),
            Impact = hasCard ? GetString(card, "impact") : null,
            ConfirmText = hasCard ? GetString(card, "confirm_text") ?? L.Text("CopilotCardDefaultConfirm") : L.Text("CopilotCardDefaultConfirm"),
            CancelText = hasCard ? GetString(card, "cancel_text") ?? L.Text("CopilotCardDefaultCancel") : L.Text("CopilotCardDefaultCancel"),
            UserNotes = request.UserNotes,
            FollowUpPrompt = TryGetFollowUpPrompt(proposal, out var followUpPrompt) ? followUpPrompt : null,
            WorkflowSteps = TryGetWorkflowSteps(proposal, out var workflowSteps) ? workflowSteps : [],
            WorkflowActiveStepId = TryGetWorkflowActiveStepId(proposal, out var workflowActiveStepId) ? workflowActiveStepId : null,
            Action = request
        };
    }

    internal static bool TryGetActionRequest(JsonElement? proposal, out AiActionRequest request)
    {
        request = new AiActionRequest(AiActionKind.None);
        if (!TryGetProposalRoot(proposal, out var root)) return false;
        if (!root.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.Object) return false;
        if (!action.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String) return false;

        var actionKind = ResolveActionKind(kindElement.GetString());
        if (actionKind == AiActionKind.None) return false;

        request = ResolveActionPath(new AiActionRequest(
            actionKind,
            Path: GetString(action, "path"),
            WillOverwriteExistingData: GetBool(action, "will_overwrite_existing_data"),
            ScopeLabel: GetString(action, "scope_label"),
            UserNotes: GetString(action, "user_notes")));
        return true;
    }

    private static bool TryGetProposalRoot(JsonElement? proposal, out JsonElement root)
    {
        root = default;
        if (proposal is null || proposal.Value.ValueKind != JsonValueKind.Object) return false;
        root = proposal.Value;
        if (root.TryGetProperty("proposal", out var nestedProposal) && nestedProposal.ValueKind == JsonValueKind.Object)
        {
            root = nestedProposal;
        }

        return true;
    }
    internal static bool TryGetWorkflowSteps(JsonElement? proposal, out IReadOnlyList<AiWorkflowStep> workflowSteps)
    {
        workflowSteps = [];
        if (!TryGetProposalRoot(proposal, out var root)) return false;
        if (!TryGetWorkflowStepsElement(root, out var stepsElement)) return false;

        var steps = new List<AiWorkflowStep>();
        foreach (var step in stepsElement.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var stepId = GetString(step, "step_id");
            var title = GetString(step, "title");
            if (string.IsNullOrWhiteSpace(stepId) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            steps.Add(new AiWorkflowStep(stepId.Trim(), title.Trim()));
        }

        if (steps.Count == 0) return false;
        workflowSteps = steps;
        return true;
    }

    internal static bool TryGetWorkflowStepsElement(JsonElement root, out JsonElement stepsElement)
    {
        if (root.TryGetProperty("workflow_steps", out stepsElement) && stepsElement.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (root.TryGetProperty("card", out var card)
            && card.ValueKind == JsonValueKind.Object
            && card.TryGetProperty("workflow_steps", out stepsElement)
            && stepsElement.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        stepsElement = default;
        return false;
    }

    internal static bool TryGetWorkflowActiveStepId(JsonElement? proposal, out string stepId)
    {
        stepId = string.Empty;
        if (!TryGetProposalRoot(proposal, out var root)) return false;
        var value = GetString(root, "workflow_active_step_id");
        if (string.IsNullOrWhiteSpace(value)
            && root.TryGetProperty("card", out var card)
            && card.ValueKind == JsonValueKind.Object)
        {
            value = GetString(card, "workflow_active_step_id");
        }

        if (string.IsNullOrWhiteSpace(value)) return false;
        stepId = value.Trim();
        return true;
    }


    internal static bool TryGetWorkflowStepLabel(JsonElement? proposal, out string label)
    {
        label = string.Empty;
        if (!TryGetWorkflowActiveStepId(proposal, out var activeStepId)) return false;
        if (!TryGetWorkflowSteps(proposal, out var steps)) return false;

        var step = steps.FirstOrDefault(item => string.Equals(item.StepId, activeStepId, StringComparison.OrdinalIgnoreCase));
        if (step is null || string.IsNullOrWhiteSpace(step.Title)) return false;

        label = step.Title.Trim();
        return true;
    }

    internal static bool TryGetFollowUpPrompt(JsonElement? proposal, out string followUpPrompt)
    {
        followUpPrompt = string.Empty;
        if (!TryGetProposalRoot(proposal, out var root)) return false;
        if (!root.TryGetProperty("card", out var card) || card.ValueKind != JsonValueKind.Object) return false;
        var value = GetString(card, "follow_up_prompt");
        if (string.IsNullOrWhiteSpace(value)) return false;
        followUpPrompt = value.Trim();
        return true;
    }

    internal static AiActionRequest ResolveActionPath(AiActionRequest action)
    {
        if (action.Kind is not (AiActionKind.StartScan or AiActionKind.NavigateToScannedPath)
            || string.IsNullOrWhiteSpace(action.Path))
        {
            return action;
        }

        return action with { Path = ScanPathResolver.Resolve(action.Path) };
    }

    private static AiActionKind ResolveActionKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return AiActionKind.None;
        if (Enum.TryParse<AiActionKind>(kind, ignoreCase: true, out var actionKind)) return actionKind;
        return kind.Trim() switch
        {
            "scan" => AiActionKind.StartScan,
            "start_scan" => AiActionKind.StartScan,
            "analyze_cleanup" => AiActionKind.AnalyzeCleanup,
            "discover_unity_libraries" => AiActionKind.DiscoverUnityLibraries,
            "clear_conversation" => AiActionKind.ClearConversation,
            "navigate" => AiActionKind.NavigateToScannedPath,
            "navigate_to_scanned_path" => AiActionKind.NavigateToScannedPath,
            _ => AiActionKind.None
        };
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool GetBool(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

}
