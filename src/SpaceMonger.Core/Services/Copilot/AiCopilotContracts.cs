using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SpaceMonger.Core.Services.Copilot;

public enum AiActionKind
{
    None,
    StartScan,
    AnalyzeCleanup,
    DiscoverUnityLibraries,
    ClearConversation,
    NavigateToScannedPath,
    SelectRecommendation,
    DeselectRecommendation
}

public enum AiActionProgressStatus
{
    Pending,
    Running,
    Completed,
    Failed
}

public enum AiInteractionCardStatus
{
    Pending,
    Running,
    Completed,
    Cancelled,
    Failed
}

public sealed record AiSkill(
    string Id,
    string Description,
    string Prompt);

public sealed record AiSkillCatalogItem(
    string Id,
    string DisplayName,
    string Description);

public sealed record AiSkillStoreResult(
    bool Success,
    string Message,
    string? SkillId = null,
    string? Content = null,
    string? ErrorCode = null);

public sealed record AiActionRequest(
    AiActionKind Kind,
    string? Path = null,
    string? RecommendationId = null,
    bool WillOverwriteExistingData = false,
    string? ScopeLabel = null,
    string? UserNotes = null);

public sealed record AiActionResult(
    bool Success,
    string Message,
    string? Details = null)
{
    public static AiActionResult Ok(string message, string? details = null) => new(true, message, details);
    public static AiActionResult Fail(string message, string? details = null) => new(false, message, details);
}

public sealed record AiActionProgress(
    string StepId,
    string Title,
    AiActionProgressStatus Status);

public sealed record AiWorkflowStep(
    string StepId,
    string Title);

public sealed class AiInteractionCard : INotifyPropertyChanged
{
    private AiInteractionCardStatus _status = AiInteractionCardStatus.Pending;
    private string? _statusText;
    private string? _userNotes;
    private bool _isBusy;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Title { get; init; }
    public required string Description { get; init; }
    public string? Impact { get; init; }
    public string ConfirmText { get; init; } = "确认";
    public string CancelText { get; init; } = "取消";
    public string? FollowUpPrompt { get; init; }
    public IReadOnlyList<AiWorkflowStep> WorkflowSteps { get; init; } = [];
    public string? WorkflowActiveStepId { get; init; }
    public required AiActionRequest Action { get; init; }

    public string? UserNotes
    {
        get => _userNotes;
        set
        {
            if (_userNotes == value)
                return;

            _userNotes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasUserNotes));
        }
    }

    public AiInteractionCardStatus Status
    {
        get => _status;
        set
        {
            if (_status == value)
                return;

            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPending));
            OnPropertyChanged(nameof(IsFinished));
            OnPropertyChanged(nameof(StatusIconGlyph));
            OnPropertyChanged(nameof(StatusIconState));
        }
    }

    public string? StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText == value)
                return;

            _statusText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasStatusText));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value)
                return;

            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPending));
            OnPropertyChanged(nameof(StatusIconGlyph));
            OnPropertyChanged(nameof(StatusIconState));
        }
    }

    public bool IsPending => Status == AiInteractionCardStatus.Pending && !IsBusy;
    public bool IsFinished => Status is AiInteractionCardStatus.Completed or AiInteractionCardStatus.Cancelled or AiInteractionCardStatus.Failed;
    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);
    public bool HasUserNotes => !string.IsNullOrWhiteSpace(UserNotes);
    public string StatusIconState => IsBusy || Status == AiInteractionCardStatus.Running
        ? "running"
        : Status == AiInteractionCardStatus.Completed
            ? "finish"
            : "idle";

    public string StatusIconGlyph => StatusIconState switch
    {
        "running" => "◐",
        "finish" => "✓",
        _ => "○"
    };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record AiSkillRoutingResult(IReadOnlyList<AiSkill> Skills)
{
    public IReadOnlyList<string> SelectedSkillIds { get; init; } = [];
}

