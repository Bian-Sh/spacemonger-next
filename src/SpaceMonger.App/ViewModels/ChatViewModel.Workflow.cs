using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using SpaceMonger.Core.Models;
using SpaceMonger.App.Localization;
using SpaceMonger.Core.Services.Copilot;

namespace SpaceMonger.App.ViewModels;

public partial class ChatViewModel
{
    private static async Task UpdateOperationStatusAsync(ChatMessage message, DateTime startedAt, CancellationToken cancellationToken)
    {
        while (true)
        {
            message.OperationStatusText = FormatOperationStatus("running", DateTime.Now - startedAt);
            await Task.Delay(1000, cancellationToken);
        }
    }

    private static string FormatElapsedForRunning(TimeSpan elapsed)
        => elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
            : $"{Math.Max(0, (int)elapsed.TotalSeconds)}s";

    private static string FormatOperationStatus(string status, TimeSpan elapsed)
    {
        var elapsedText = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        return status switch
        {
            "running" => L.Format("ChatOperationRunningStatus", FormatElapsedForRunning(elapsed)),
            "stopped" => L.Format("ChatOperationStoppedStatus", elapsedText),
            "failed" => L.Format("ChatOperationFailedStatus", elapsedText),
            _ => L.Format("ChatOperationCompleteStatus", elapsedText)
        };
    }
    private void SetWorkflowPlan(IReadOnlyList<string> stepTitles)
    {
        WorkflowSteps = new ObservableCollection<CopilotWorkflowStep>(stepTitles.Select(title => new CopilotWorkflowStep(title)));
        CurrentWorkflowStepNumber = WorkflowSteps.Count > 0 ? 1 : 0;
        if (WorkflowSteps.Count > 0)
        {
            WorkflowSteps[0].Status = CopilotWorkflowStepStatus.Running;
        }
        IsWorkflowProgressVisible = WorkflowSteps.Count > 0;
        OnPropertyChanged(nameof(HasWorkflowSteps));
        OnPropertyChanged(nameof(ShouldShowWorkflowStepIndicator));
        NotifyWorkflowProgressChanged();
    }

    private void SetWorkflowPlan(IReadOnlyList<AiWorkflowStep> steps)
    {
        WorkflowSteps = new ObservableCollection<CopilotWorkflowStep>(steps.Select(step => new CopilotWorkflowStep(step.Title, step.StepId)));
        CurrentWorkflowStepNumber = WorkflowSteps.Count > 0 ? 1 : 0;
        if (WorkflowSteps.Count > 0)
        {
            WorkflowSteps[0].Status = CopilotWorkflowStepStatus.Running;
        }
        IsWorkflowProgressVisible = WorkflowSteps.Count > 0;
        OnPropertyChanged(nameof(HasWorkflowSteps));
        OnPropertyChanged(nameof(ShouldShowWorkflowStepIndicator));
        NotifyWorkflowProgressChanged();
    }
    private void SetWorkflowPlan(IReadOnlyList<WorkflowStepPlan> steps)
    {
        WorkflowSteps = new ObservableCollection<CopilotWorkflowStep>(steps.Select(step => new CopilotWorkflowStep(step.Title, step.StepId)));
        CurrentWorkflowStepNumber = WorkflowSteps.Count > 0 ? 1 : 0;
        if (WorkflowSteps.Count > 0)
        {
            WorkflowSteps[0].Status = CopilotWorkflowStepStatus.Running;
        }
        IsWorkflowProgressVisible = WorkflowSteps.Count > 0;
        OnPropertyChanged(nameof(HasWorkflowSteps));
        OnPropertyChanged(nameof(ShouldShowWorkflowStepIndicator));
        NotifyWorkflowProgressChanged();
    }

    private void StartWorkflowStep(int index)
    {
        if (index < 0 || index >= WorkflowSteps.Count) return;
        for (var stepIndex = 0; stepIndex < index; stepIndex++)
        {
            if (WorkflowSteps[stepIndex].Status != CopilotWorkflowStepStatus.Failed)
            {
                WorkflowSteps[stepIndex].Status = CopilotWorkflowStepStatus.Finished;
            }
        }

        CurrentWorkflowStepNumber = index + 1;
        WorkflowSteps[index].Status = CopilotWorkflowStepStatus.Running;
        NotifyWorkflowProgressChanged();
    }

    private int FindWorkflowStepIndex(string? stepId)
    {
        if (string.IsNullOrWhiteSpace(stepId))
        {
            return 0;
        }

        var index = WorkflowSteps
            .Select((step, stepIndex) => new { step, stepIndex })
            .FirstOrDefault(item => string.Equals(item.step.StepId, stepId, StringComparison.OrdinalIgnoreCase))
            ?.stepIndex ?? -1;
        return index >= 0 ? index : 0;
    }

    private void CompleteWorkflowStep(int index, bool success)
    {
        if (index < 0 || index >= WorkflowSteps.Count) return;
        WorkflowSteps[index].Status = success ? CopilotWorkflowStepStatus.Finished : CopilotWorkflowStepStatus.Idle;
        NotifyWorkflowProgressChanged();
    }

    private void ApplyWorkflowProgress(AiActionProgress progress)
    {
        var index = WorkflowSteps
            .Select((step, stepIndex) => new { step, stepIndex })
            .FirstOrDefault(item => string.Equals(item.step.StepId, progress.StepId, StringComparison.OrdinalIgnoreCase))
            ?.stepIndex ?? -1;

        if (index < 0)
        {
            WorkflowSteps.Add(new CopilotWorkflowStep(progress.Title, progress.StepId));
            index = WorkflowSteps.Count - 1;
            OnPropertyChanged(nameof(HasWorkflowSteps));
            OnPropertyChanged(nameof(ShouldShowWorkflowStepIndicator));
        }

        WorkflowSteps[index].Status = progress.Status switch
        {
            AiActionProgressStatus.Running => CopilotWorkflowStepStatus.Running,
            AiActionProgressStatus.Completed => CopilotWorkflowStepStatus.Finished,
            AiActionProgressStatus.Failed => CopilotWorkflowStepStatus.Failed,
            _ => WorkflowSteps[index].Status
        };

        if (progress.Status == AiActionProgressStatus.Running)
        {
            CurrentWorkflowStepNumber = index + 1;
        }

        IsWorkflowProgressVisible = WorkflowSteps.Count > 0;
        NotifyWorkflowProgressChanged();
    }

    private CancellationToken BeginActiveOperation()
    {
        _activeOperationCancellation?.Dispose();
        _activeOperationCancellation = new CancellationTokenSource();
        IsOperationRunning = true;
        return _activeOperationCancellation.Token;
    }

    private void CancelActiveOperation()
    {
        _activeOperationCancellation?.Cancel();
    }

    private void FinishActiveOperation()
    {
        _activeOperationCancellation?.Dispose();
        _activeOperationCancellation = null;
        IsOperationRunning = false;
    }

    private void HideWorkflowProgress()
    {
        IsWorkflowProgressVisible = false;
    }

    private void MarkRunningWorkflowStep(bool success)
    {
        var index = WorkflowSteps
            .Select((step, stepIndex) => new { step, stepIndex })
            .FirstOrDefault(item => item.step.Status == CopilotWorkflowStepStatus.Running)
            ?.stepIndex ?? Math.Max(0, CurrentWorkflowStepNumber - 1);
        CompleteWorkflowStep(index, success);
    }

    private static IReadOnlyList<string> BuildWorkflowSteps(AiActionRequest action, AiSkillRoutingResult? routed)
    {
        action = ChatProposalMapper.ResolveActionPath(action);
        var scope = action.ScopeLabel ?? action.Path ?? Localized("current scope", "当前范围");
        return action.Kind switch
        {
            AiActionKind.StartScan =>
            [
                Localized($"Run confirmed scan for {scope}", $"执行已确认的扫描：{scope}")
            ],
            AiActionKind.AnalyzeCleanup =>
            [
                Localized($"Run confirmed cleanup analysis for {scope}", $"执行已确认的清理分析：{scope}")
            ],
            AiActionKind.NavigateToScannedPath =>
            [
                Localized($"Navigate to {scope}", $"导航到 {scope}")
            ],
            _ =>
            [
                Localized("Run confirmed action", "执行已确认的操作")
            ]
        };
    }

    private static IReadOnlyList<WorkflowStepPlan> BuildUnityDiscoveryWorkflowSteps(AiActionRequest action)
    {
        var steps = new List<WorkflowStepPlan>
        {
            new("enumerate_drives", string.IsNullOrWhiteSpace(action.Path)
                ? Localized("AI checks ready drives", "AI 确认可扫描磁盘")
                : Localized("AI checks the scan root", "AI 确认扫描根目录"))
        };

        if (!string.IsNullOrWhiteSpace(action.Path))
        {
            var scope = action.ScopeLabel ?? action.Path;
            steps.Add(new WorkflowStepPlan("scan_scope:" + action.Path, Localized($"AI scans {scope} for cleanup candidates", $"AI 扫描 {scope} 的清理候选项。")));
        }
        else
        {
            steps.AddRange(DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase)
                .Select(drive => new WorkflowStepPlan(
                    "scan_drive:" + drive.Name.TrimEnd('\\'),
                    Localized($"AI scans {drive.Name} for cleanup candidates", $"AI 扫描 {drive.Name} 的清理候选项。"))));
        }

        steps.Add(new WorkflowStepPlan("write_unity_recommendations", Localized("AI writes cleanup recommendations", "AI 写入清理建议")));
        return steps;
    }


}

internal sealed record WorkflowStepPlan(string StepId, string Title);

public sealed partial class CopilotWorkflowStep(string title, string? stepId = null) : ObservableObject
{
    [ObservableProperty] private CopilotWorkflowStepStatus _status = CopilotWorkflowStepStatus.Idle;

    public string? StepId { get; } = stepId;
    public string Title { get; } = title;
    public string StatusIconState => Status switch
    {
        CopilotWorkflowStepStatus.Running => "running",
        CopilotWorkflowStepStatus.Finished => "finish",
        CopilotWorkflowStepStatus.Failed => "failed",
        _ => "idle"
    };
    public string StatusIconGlyph => Status switch
    {
        CopilotWorkflowStepStatus.Running => "●",
        CopilotWorkflowStepStatus.Finished => "✓",
        CopilotWorkflowStepStatus.Failed => "⚠",
        _ => "○"
    };

    partial void OnStatusChanged(CopilotWorkflowStepStatus value)
    {
        OnPropertyChanged(nameof(StatusIconState));
        OnPropertyChanged(nameof(StatusIconGlyph));
    }
}

public enum CopilotWorkflowStepStatus
{
    Idle,
    Running,
    Finished,
    Failed
}

