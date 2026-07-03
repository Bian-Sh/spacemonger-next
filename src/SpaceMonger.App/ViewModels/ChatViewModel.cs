using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SpaceMonger.App.Localization;
using SpaceMonger.App.Services.Copilot;
using SpaceMonger.Core.Enums;
using SpaceMonger.Core.Models;
using SpaceMonger.Core.Services.Chat;
using SpaceMonger.Core.Services.Copilot;
using SpaceMonger.Core.Services.Llm;
using SpaceMonger.Core.Services.Scanning;
using SpaceMonger.Core.Services.Settings;

namespace SpaceMonger.App.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly IChatService _chatService;
    private readonly ISettingsService _settingsService;
    private readonly IAiSkillRouter _skillRouter;
    private readonly ILogger<ChatViewModel> _logger;
    private IAiDiskActionExecutor _actionExecutor = new NullAiDiskActionExecutor();
    private CancellationTokenSource? _activeOperationCancellation;
    private ChatMessage? _pendingInteractionSourceMessage;

    private ScanSession? _currentSession;
    private FileEntry? _currentViewRoot;

    [ObservableProperty] private ObservableCollection<ChatMessage> _messages = new();
    [ObservableProperty] private bool _hasMessages;
    [ObservableProperty] private string? _inputText;
    [ObservableProperty] private bool _isSlashCommandMenuOpen;
    [ObservableProperty] private bool _isSkillMentionMenuOpen;
    [ObservableProperty] private AiInteractionCard? _pendingInteractionCard;
    [ObservableProperty] private bool _isWorkflowProgressVisible;
    [ObservableProperty] private int _currentWorkflowStepNumber;
    [ObservableProperty] private ObservableCollection<CopilotWorkflowStep> _workflowSteps = new();
    [ObservableProperty] private ObservableCollection<ChatCommandSuggestion> _slashCommandSuggestions = new(BuildSlashCommandSuggestions());
    [ObservableProperty] private ObservableCollection<ChatSkillSuggestion> _skillMentionSuggestions = new();
    [ObservableProperty] private ObservableCollection<ChatSkillSuggestion> _filteredSkillMentionSuggestions = new();
    [ObservableProperty] private ChatCommandSuggestion? _selectedSlashCommandSuggestion;
    [ObservableProperty] private ChatSkillSuggestion? _selectedSkillMentionSuggestion;
    [ObservableProperty] private FileEntry? _linkedEntry;
    [ObservableProperty] private CleanupRecommendation? _linkedRecommendation;
    [ObservableProperty] private bool _isChatAvailable;
    [ObservableProperty] private bool _isApiKeyConfigured;
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private bool _isOperationRunning;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _linkedItemPath;

    public ChatViewModel(IChatService chatService, ISettingsService settingsService, IAiSkillRouter skillRouter, ILogger<ChatViewModel>? logger = null)
    {
        _chatService = chatService;
        _settingsService = settingsService;
        _skillRouter = skillRouter;
        _logger = logger ?? NullLogger<ChatViewModel>.Instance;
        _logger.LogInformation("ChatViewModel created");
        SkillMentionSuggestions = new ObservableCollection<ChatSkillSuggestion>(
            _skillRouter.GetSkillCatalog().Select(skill => new ChatSkillSuggestion($"@{skill.Id}", skill.DisplayName, skill.Description)));
        FilteredSkillMentionSuggestions = new ObservableCollection<ChatSkillSuggestion>(SkillMentionSuggestions);
        Messages.CollectionChanged += Messages_CollectionChanged;
        L.LanguageChanged += RefreshSlashCommandSuggestions;
        RefreshApiKeyStatus();
    }

    public event Action? ClearConsoleRequested;

    public void SetActionExecutor(IAiDiskActionExecutor actionExecutor) => _actionExecutor = actionExecutor;

    partial void OnMessagesChanged(ObservableCollection<ChatMessage>? oldValue, ObservableCollection<ChatMessage> newValue)
    {
        if (oldValue is not null) oldValue.CollectionChanged -= Messages_CollectionChanged;
        newValue.CollectionChanged += Messages_CollectionChanged;
        HasMessages = newValue.Count > 0;
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => HasMessages = Messages.Count > 0;

    public void SetContext(ScanSession session, FileEntry viewRoot)
    {
        _currentSession = session;
        _currentViewRoot = viewRoot;
        IsChatAvailable = true;
        RefreshApiKeyStatus();
    }

    public void UpdateViewRoot(FileEntry viewRoot) => _currentViewRoot = viewRoot;

    public void RefreshApiKeyStatus()
    {
        var settings = _settingsService.LoadSettings();
        IsApiKeyConfigured = !string.IsNullOrEmpty(_settingsService.GetApiKey(settings));
    }

    public bool HasPendingInteractionCard => PendingInteractionCard is not null;
    public bool HasWorkflowSteps => WorkflowSteps.Count > 0;
    public bool ShouldShowWorkflowStepIndicator => WorkflowSteps.Count > 1;
    public int CompletedWorkflowStepNumber => WorkflowSteps.Count(step => step.Status == CopilotWorkflowStepStatus.Finished);
    public string WorkflowProgressText => HasWorkflowSteps
        ? Localized($"Step {CurrentWorkflowStepNumber}/{WorkflowSteps.Count}", $"\u7b2c {CurrentWorkflowStepNumber}/{WorkflowSteps.Count} \u6b65")
        : string.Empty;
    public string WorkflowProgressArcData => BuildWorkflowProgressArcData(CompletedWorkflowStepNumber, WorkflowSteps.Count);
    public string CurrentWorkflowIconState => CurrentWorkflowStepNumber > 0 && CurrentWorkflowStepNumber <= WorkflowSteps.Count
        ? WorkflowSteps[CurrentWorkflowStepNumber - 1].StatusIconState
        : "idle";
    public string SendButtonText => IsOperationRunning ? Localized("Stop", "\u505c\u6b62") : L.Text("SendButton");
    public bool IsCompletionMenuOpen => IsSlashCommandMenuOpen || IsSkillMentionMenuOpen;

    partial void OnLinkedEntryChanged(FileEntry? value) => LinkedItemPath = value?.Path;
    partial void OnLinkedRecommendationChanged(CleanupRecommendation? value) => LinkedItemPath = value?.TargetPath;
    partial void OnInputTextChanged(string? value)
    {
        IsSlashCommandMenuOpen = IsSlashCommandPrompt(value);
        IsSkillMentionMenuOpen = IsSkillMentionPrompt(value);
        FilteredSkillMentionSuggestions = new ObservableCollection<ChatSkillSuggestion>(BuildFilteredSkillMentionSuggestions(value));
        SelectedSlashCommandSuggestion = IsSlashCommandMenuOpen ? SlashCommandSuggestions.FirstOrDefault() : null;
        SelectedSkillMentionSuggestion = IsSkillMentionMenuOpen ? FilteredSkillMentionSuggestions.FirstOrDefault() : null;
        OnPropertyChanged(nameof(IsCompletionMenuOpen));
    }

    partial void OnIsSlashCommandMenuOpenChanged(bool value) => OnPropertyChanged(nameof(IsCompletionMenuOpen));
    partial void OnIsSkillMentionMenuOpenChanged(bool value) => OnPropertyChanged(nameof(IsCompletionMenuOpen));
    partial void OnPendingInteractionCardChanged(AiInteractionCard? value) => OnPropertyChanged(nameof(HasPendingInteractionCard));
    partial void OnIsOperationRunningChanged(bool value) => OnPropertyChanged(nameof(SendButtonText));
    partial void OnCurrentWorkflowStepNumberChanged(int value)
    {
        OnPropertyChanged(nameof(WorkflowProgressText));
        OnPropertyChanged(nameof(WorkflowProgressArcData));
        OnPropertyChanged(nameof(CurrentWorkflowIconState));
    }

    private void NotifyWorkflowProgressChanged()
    {
        OnPropertyChanged(nameof(CompletedWorkflowStepNumber));
        OnPropertyChanged(nameof(WorkflowProgressText));
        OnPropertyChanged(nameof(WorkflowProgressArcData));
        OnPropertyChanged(nameof(CurrentWorkflowIconState));
    }

    private static string BuildWorkflowProgressArcData(int completedStepCount, int stepCount)
    {
        if (completedStepCount <= 0 || stepCount <= 0) return string.Empty;

        var progress = Math.Clamp((double)completedStepCount / stepCount, 0.001, 0.999);
        const double center = 7;
        const double radius = 5.2;
        var angle = (progress * 360) - 90;
        var radians = angle * Math.PI / 180;
        var endX = center + radius * Math.Cos(radians);
        var endY = center + radius * Math.Sin(radians);
        var isLargeArc = progress > 0.5 ? 1 : 0;

        return FormattableString.Invariant($"M 7,1.8 A 5.2,5.2 0 {isLargeArc} 1 {endX:0.###},{endY:0.###}");
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SubmitOrStopAsync()
    {
        if (IsOperationRunning)
        {
            CancelActiveOperation();
            return;
        }

        await SendAsync();
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (IsOperationRunning)
        {
            CancelActiveOperation();
            return;
        }

        RefreshApiKeyStatus();
        if (string.IsNullOrWhiteSpace(InputText)) return;

        var userInput = InputText.Trim();
        _logger.LogInformation("Chat send requested; length={Length}", userInput.Length);
        PendingInteractionCard = null;
        var settings = _settingsService.LoadSettings();
        var responseLanguage = ResolveResponseLanguage(settings.Language);

        if (TryExecuteSlashCommand(userInput))
        {
            InputText = null;
            IsSlashCommandMenuOpen = false;
            return;
        }

        Messages.Add(new ChatMessage
        {
            Sender = ChatSender.User,
            Text = userInput,
            Timestamp = DateTime.Now,
            LinkedEntry = LinkedEntry,
            LinkedRecommendation = LinkedRecommendation
        });
        InputText = null;

        var routed = _skillRouter.Route(userInput, LinkedEntry, _currentViewRoot, _actionExecutor.HasExistingRecommendations, responseLanguage);
        if (!IsApiKeyConfigured)
        {
            Messages.Add(new ChatMessage
            {
                Sender = ChatSender.Assistant,
                Text = Localized("Configure a model service API Key before using Copilot.", "需要先配置模型服务 API Key，才能使用 Copilot。"),
                Timestamp = DateTime.Now,
                IsError = true
            });
            return;
        }

        IsSending = true;
        ErrorMessage = null;
        var cancellationToken = BeginActiveOperation();

        var assistantMessage = new ChatMessage
        {
            Sender = ChatSender.Assistant,
            Text = string.Empty,
            Thinking = string.Empty,
            Timestamp = DateTime.Now,
            IsStreaming = true
        };
        var operationStartedAt = DateTime.Now;
        var operationStatus = "completed";
        using var operationStatusCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var operationStatusTask = UpdateOperationStatusAsync(assistantMessage, operationStartedAt, operationStatusCancellation.Token);
        Messages.Add(assistantMessage);

        try
        {
            var apiKey = _settingsService.GetApiKey(settings)!;
            var baseUrl = settings.AnthropicBaseUrl;
            var modelUserInput = BuildModelUserInput(userInput, routed, LinkedEntry, LinkedRecommendation, _currentViewRoot, _currentSession, _actionExecutor.HasExistingRecommendations);
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
                    baseUrl,
                    enableThinking,
                    thinkingToken => AppendStreamingToken(assistantMessage, thinkingToken, isThinking: true),
                    textToken => AppendStreamingToken(assistantMessage, textToken, isThinking: false),
                    cancellationToken);
            }
            else
            {
                response = await _chatService.StreamSkillMessageWithThinkingAsync(
                    modelUserInput,
                    routed.Skills,
                    responseLanguage,
                    apiKey,
                    baseUrl,
                    enableThinking,
                    thinkingToken => AppendStreamingToken(assistantMessage, thinkingToken, isThinking: true),
                    textToken => AppendStreamingToken(assistantMessage, textToken, isThinking: false),
                    cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(assistantMessage.Text) && !string.IsNullOrWhiteSpace(response.Text))
            {
                assistantMessage.Text = response.Text;
            }

            if (await TryRunDirectActionProposalAsync(assistantMessage, response.Proposal, routed, cancellationToken))
            {
                return;
            }

            ApplyProposalIfAny(assistantMessage, response.Proposal);

            if (response.Proposal.HasValue && assistantMessage.InteractionCard is null)
            {
                _logger.LogWarning("Chat response contained a proposal that could not be converted to an interaction card: {Proposal}", response.Proposal.Value.GetRawText());
            }

            _logger.LogInformation("Chat response completed; textLength={TextLength}, hasProposal={HasProposal}", assistantMessage.Text.Length, response.Proposal.HasValue);
            MoveMessageInteractionCardToInputOverlay(assistantMessage);
            assistantMessage.IsStreaming = false;
            LinkedEntry = null;
            LinkedRecommendation = null;
            LinkedItemPath = null;
        }
        catch (OperationCanceledException)
        {
            operationStatus = "stopped";
            _logger.LogWarning("Chat operation cancelled");
            assistantMessage.IsStreaming = false;
            assistantMessage.IsError = true;
            assistantMessage.Text = string.IsNullOrEmpty(assistantMessage.Text)
                ? Localized("Stopped.", "\u5df2\u505c\u6b62\u3002")
                : assistantMessage.Text + Localized("\nStopped.", "\n\u5df2\u505c\u6b62\u3002");
            MarkRunningWorkflowStep(false);
            HideWorkflowProgress();
        }
        catch (Exception ex)
        {
            operationStatus = "failed";
            _logger.LogError(ex, "Chat operation failed");
            assistantMessage.IsStreaming = false;
            assistantMessage.IsError = true;
            assistantMessage.Text = string.IsNullOrEmpty(assistantMessage.Text)
                ? ex.Message
                : assistantMessage.Text + L.Format("ChatErrorAppend", ex.Message);
            if (routed.SelectedSkillIds.Count > 0 && WorkflowSteps.Count > 0)
            {
                CompleteWorkflowStep(Math.Max(0, CurrentWorkflowStepNumber - 1), false);
            }
            ErrorMessage = ex.Message;
        }
        finally
        {
            operationStatusCancellation.Cancel();
            try { await operationStatusTask; } catch (OperationCanceledException) { }
            if (operationStatus == "completed" && assistantMessage.IsError)
            {
                operationStatus = "failed";
            }
            var operationCompletedAt = DateTime.Now;
            assistantMessage.OperationStatusText = FormatOperationStatus(operationStatus, operationCompletedAt - operationStartedAt);
            assistantMessage.MarkCompletedAt(operationCompletedAt);
            IsSending = false;
            FinishActiveOperation();
            if (!assistantMessage.IsStreaming)
            {
                HideWorkflowProgress();
            }
        }
    }



    private static string BuildModelUserInput(
        string userInput,
        AiSkillRoutingResult routed,
        FileEntry? linkedEntry,
        CleanupRecommendation? linkedRecommendation,
        FileEntry? currentViewRoot,
        ScanSession? currentSession,
        bool hasExistingRecommendations)
    {
        var context = new
        {
            scan_available = currentSession is not null,
            scan_root_path = currentSession?.RootEntry?.Path ?? currentSession?.TargetPath,
            current_view_path = currentViewRoot?.Path,
            selected_path = linkedEntry?.Path ?? linkedRecommendation?.TargetPath,
            has_existing_recommendations = hasExistingRecommendations,
            selected_skills = routed.SelectedSkillIds.Select(id => "@" + id).ToArray(),
            available_drives = DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .Select(drive => drive.Name)
                .ToArray()
        };
        var contextJson = JsonSerializer.Serialize(context);
        return $"{userInput}\n\nHost disk context JSON:\n{contextJson}";
    }

    
    private static bool ShouldEnableThinking(AppSettings settings, AiSkillRoutingResult routed)
        => settings.EnableThinking && routed.SelectedSkillIds.Count == 0;

    private static void AppendOperationResult(ChatMessage message, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        message.OperationResultText = string.IsNullOrWhiteSpace(message.OperationResultText)
            ? text.Trim()
            : message.OperationResultText.TrimEnd() + Environment.NewLine + Environment.NewLine + text.Trim();
    }

    private static void AppendAssistantText(ChatMessage message, string text, string? sectionHeader = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var section = string.IsNullOrWhiteSpace(sectionHeader)
            ? text.Trim()
            : sectionHeader.Trim() + Environment.NewLine + text.Trim();
        message.Text = string.IsNullOrWhiteSpace(message.Text)
            ? section
            : message.Text.TrimEnd() + Environment.NewLine + Environment.NewLine + section;
    }

    private static void AppendAssistantThinking(ChatMessage message, string thinking, string? sectionHeader = null)
    {
        if (string.IsNullOrWhiteSpace(thinking))
        {
            return;
        }

        var section = string.IsNullOrWhiteSpace(sectionHeader)
            ? thinking.Trim()
            : sectionHeader.Trim() + Environment.NewLine + thinking.Trim();
        message.Thinking = string.IsNullOrWhiteSpace(message.Thinking)
            ? section
            : message.Thinking.TrimEnd() + Environment.NewLine + Environment.NewLine + section;
    }

    private string FormatStepSectionHeader(ChatMessage message, int workflowStepIndex)
    {
        var stepText = workflowStepIndex >= 0 && workflowStepIndex < WorkflowSteps.Count
            ? WorkflowSteps[workflowStepIndex].Title
            : Localized("Current step", "当前步骤");
        var statusText = string.IsNullOrWhiteSpace(message.OperationStatusText)
            ? FormatOperationStatus("running", DateTime.Now - message.Timestamp)
            : message.OperationStatusText;
        return $"**{statusText} · {stepText}**";
    }

    private string FormatStepSectionHeader(ChatMessage message, JsonElement? proposal)
    {
        var stepText = ChatProposalMapper.TryGetWorkflowStepLabel(proposal, out var label)
            ? label
            : Localized("Next step", "下一步");
        var statusText = string.IsNullOrWhiteSpace(message.OperationStatusText)
            ? FormatOperationStatus("running", DateTime.Now - message.Timestamp)
            : message.OperationStatusText;
        return $"**{statusText} · {stepText}**";
    }

    private static async Task AppendStreamingTextAsync(ChatMessage message, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AppendStreamingToken(message, text, isThinking: false);
        await Task.CompletedTask;
    }

    private static void AppendStreamingToken(ChatMessage message, string token, bool isThinking)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        void Append()
        {
            if (isThinking)
            {
                message.Thinking += token;
            }
            else
            {
                message.Text += token;
            }
        }

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Append();
        }
        else
        {
            dispatcher.Invoke(Append);
        }
    }

    private static string FormatActionResult(AiActionResult result)
    {
        var text = result.Details is null ? result.Message : $"{result.Message}\n{result.Details}";
        return Environment.NewLine + text + Environment.NewLine;
    }

    internal static string Localized(string english, string chinese)
        => L.CurrentLanguageName.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? english : chinese;

    private static string ResolveResponseLanguage(string? configuredLanguage)
    {
        if (string.IsNullOrWhiteSpace(configuredLanguage))
        {
            return "zh-CN";
        }

        return string.Equals(configuredLanguage, L.AutoLanguage, StringComparison.OrdinalIgnoreCase)
            ? L.CurrentLanguageName
            : configuredLanguage.Trim();
    }

    public static void ApplyProposalIfAny(ChatMessage message, JsonElement? proposal)
        => ChatProposalMapper.ApplyProposalIfAny(message, proposal);
}










