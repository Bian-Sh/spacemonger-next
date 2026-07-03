using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SpaceMonger.App.Localization;
using SpaceMonger.Core.Enums;
using SpaceMonger.Core.Models;

namespace SpaceMonger.App.ViewModels;

public partial class ChatViewModel
{
    [RelayCommand]
    private void SelectSlashCommand(ChatCommandSuggestion? suggestion)
    {
        if (suggestion is null) return;
        InputText = suggestion.Command;
        IsSlashCommandMenuOpen = false;
    }

    [RelayCommand]
    private void SelectSkillMention(ChatSkillSuggestion? suggestion)
    {
        if (suggestion is null) return;
        InputText = suggestion.Mention + " ";
        IsSkillMentionMenuOpen = false;
    }

    public void MoveCompletionSelection(int delta)
    {
        if (IsSlashCommandMenuOpen)
        {
            SelectedSlashCommandSuggestion = MoveSelection(SlashCommandSuggestions, SelectedSlashCommandSuggestion, delta);
            return;
        }

        if (IsSkillMentionMenuOpen)
        {
            SelectedSkillMentionSuggestion = MoveSelection(FilteredSkillMentionSuggestions, SelectedSkillMentionSuggestion, delta);
        }
    }

    public bool ConfirmActiveCompletion()
    {
        if (IsSlashCommandMenuOpen)
        {
            SelectSlashCommand(SelectedSlashCommandSuggestion ?? SlashCommandSuggestions.FirstOrDefault());
            return true;
        }

        if (IsSkillMentionMenuOpen)
        {
            SelectSkillMention(SelectedSkillMentionSuggestion ?? FilteredSkillMentionSuggestions.FirstOrDefault());
            return true;
        }

        return false;
    }

    private static T? MoveSelection<T>(IReadOnlyList<T> items, T? selected, int delta) where T : class
    {
        if (items.Count == 0) return null;
        var index = 0;
        if (selected is not null)
        {
            for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
            {
                if (EqualityComparer<T>.Default.Equals(items[itemIndex], selected))
                {
                    index = itemIndex;
                    break;
                }
            }
        }
        index = (index + delta + items.Count) % items.Count;
        return items[index];
    }

    private static bool IsSlashCommandPrompt(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        return trimmed == "/" || (trimmed.StartsWith("/", StringComparison.Ordinal) && !trimmed.Contains(" ", StringComparison.Ordinal));
    }

    private static bool IsSkillMentionPrompt(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        return trimmed == "@" || (trimmed.StartsWith("@", StringComparison.Ordinal) && !trimmed.Contains(" ", StringComparison.Ordinal));
    }

    private IEnumerable<ChatSkillSuggestion> BuildFilteredSkillMentionSuggestions(string? text)
    {
        var query = (text?.Trim() ?? string.Empty).TrimStart('@');
        if (string.IsNullOrWhiteSpace(query))
        {
            return SkillMentionSuggestions;
        }

        return SkillMentionSuggestions.Where(skill =>
            skill.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || skill.Mention.Contains(query, StringComparison.OrdinalIgnoreCase)
            || skill.Description.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryExecuteSlashCommand(string text)
    {
        var command = text.Trim().ToLowerInvariant();
        if (command == "/new")
        {
            StartNewConversationSegment();
            return true;
        }

        if (command == "/clear")
        {
            ClearConversation();
            return true;
        }

        if (command == "/clear console")
        {
            ClearConsoleRequested?.Invoke();
            return true;
        }

        return false;
    }

    private static IEnumerable<ChatCommandSuggestion> BuildSlashCommandSuggestions()
    {
        yield return new ChatCommandSuggestion("/new", L.Text("SlashCommandNewDescription"));
        yield return new ChatCommandSuggestion("/clear", L.Text("SlashCommandClearDescription"));
        yield return new ChatCommandSuggestion("/clear console", L.Text("SlashCommandClearConsoleDescription"));
    }

    private void RefreshSlashCommandSuggestions()
    {
        SlashCommandSuggestions = new ObservableCollection<ChatCommandSuggestion>(BuildSlashCommandSuggestions());
        SelectedSlashCommandSuggestion = IsSlashCommandMenuOpen ? SlashCommandSuggestions.FirstOrDefault() : null;
    }

    private void ClearConversation()
    {
        _logger.LogInformation("Chat conversation cleared; messages={MessageCount}", Messages.Count);
        _chatService.ClearHistory();
        Messages.Clear();
        PendingInteractionCard = null;
        _pendingInteractionSourceMessage = null;
        IsWorkflowProgressVisible = false;
        WorkflowSteps.Clear();
        CurrentWorkflowStepNumber = 0;
        OnPropertyChanged(nameof(HasWorkflowSteps));
        OnPropertyChanged(nameof(ShouldShowWorkflowStepIndicator));
        NotifyWorkflowProgressChanged();
        LinkedEntry = null;
        LinkedRecommendation = null;
        LinkedItemPath = null;
        ErrorMessage = null;
    }

    private void StartNewConversationSegment()
    {
        _logger.LogInformation("Chat conversation segment started; messages={MessageCount}", Messages.Count);
        _chatService.ClearHistory();
        PendingInteractionCard = null;
        _pendingInteractionSourceMessage = null;
        IsWorkflowProgressVisible = false;
        WorkflowSteps.Clear();
        CurrentWorkflowStepNumber = 0;
        OnPropertyChanged(nameof(HasWorkflowSteps));
        OnPropertyChanged(nameof(ShouldShowWorkflowStepIndicator));
        NotifyWorkflowProgressChanged();
        LinkedEntry = null;
        LinkedRecommendation = null;
        LinkedItemPath = null;
        ErrorMessage = null;
        Messages.Add(new ChatMessage
        {
            Sender = ChatSender.System,
            Text = L.Text("ChatNewSessionDividerDescription"),
            Timestamp = DateTime.Now
        });
    }

    [RelayCommand]
    private void ToggleThinking(ChatMessage message) => message.IsThinkingExpanded = !message.IsThinkingExpanded;
}

public sealed record ChatCommandSuggestion(string Command, string Description);

public sealed record ChatSkillSuggestion(string Mention, string DisplayName, string Description);
