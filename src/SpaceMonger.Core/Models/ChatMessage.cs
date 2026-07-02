using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Text.RegularExpressions;
using SpaceMonger.Core.Enums;
using SpaceMonger.Core.Services.Copilot;

namespace SpaceMonger.Core.Models;

public class ChatMessage : INotifyPropertyChanged
{
    private static readonly Regex ThinkingTitleRegex = new(@"^\s*\*\*([^*\r\n]+)\*\*(?:\r?\n\r?\n|\s*$)", RegexOptions.Compiled);

    private string _text = string.Empty;
    private string _thinking = string.Empty;
    private bool _isError;
    private bool _isStreaming;
    private string _operationStatusText = string.Empty;
    private string _operationResultText = string.Empty;
    private string _completedAtText = string.Empty;
    private bool _isThinkingExpanded;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; set; } = Guid.NewGuid();

    public ChatSender Sender { get; set; }

    public string Text
    {
        get => _text;
        set { _text = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasText)); }
    }

    /// <summary>
    /// The thinking/reasoning content from the AI model.
    /// </summary>
    public string Thinking
    {
        get => _thinking;
        set
        {
            _thinking = value;
            if (_isStreaming && !string.IsNullOrWhiteSpace(_thinking))
            {
                _isThinkingExpanded = true;
                OnPropertyChanged(nameof(IsThinkingExpanded));
                OnPropertyChanged(nameof(IsThinkingCollapsed));
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasThinking));
            OnPropertyChanged(nameof(ThinkingTitle));
            OnPropertyChanged(nameof(ThinkingBody));
            OnPropertyChanged(nameof(ThinkingPreview));
            OnPropertyChanged(nameof(HasThinkingBody));
        }
    }

    public DateTime Timestamp { get; set; }

    public string OperationStatusText
    {
        get => _operationStatusText;
        set { _operationStatusText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasOperationStatus)); }
    }

    public string OperationResultText
    {
        get => _operationResultText;
        set { _operationResultText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasOperationResult)); }
    }

    public string CompletedAtText
    {
        get => _completedAtText;
        set { _completedAtText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasCompletedAtText)); }
    }
    public FileEntry? LinkedEntry { get; set; }

    public CleanupRecommendation? LinkedRecommendation { get; set; }

    public AiInteractionCard? InteractionCard { get; set; }

    public bool IsError
    {
        get => _isError;
        set { _isError = value; OnPropertyChanged(); }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            _isStreaming = value;
            IsThinkingExpanded = value && HasThinking;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Whether the thinking section is expanded (visible).
    /// </summary>
    public bool IsThinkingExpanded
    {
        get => _isThinkingExpanded;
        set { _isThinkingExpanded = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsThinkingCollapsed)); }
    }

    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public bool HasThinking => !string.IsNullOrWhiteSpace(Thinking);
    public bool IsThinkingCollapsed => !IsThinkingExpanded;
    public string? ThinkingTitle => ParseThinking().Title;
    public string ThinkingBody => ParseThinking().Body;
    public bool HasThinkingBody => !string.IsNullOrWhiteSpace(ThinkingBody);
    public string ThinkingPreview
    {
        get
        {
            var parsed = ParseThinking();
            var preview = string.IsNullOrWhiteSpace(parsed.Title) ? FirstThinkingLine(parsed.Body) : parsed.Title;
            const int maxLength = 96;
            return preview.Length <= maxLength ? preview : preview[..maxLength].TrimEnd() + "…";
        }
    }
    public bool HasOperationStatus => !string.IsNullOrWhiteSpace(OperationStatusText);
    public bool HasOperationResult => !string.IsNullOrWhiteSpace(OperationResultText);
    public bool HasCompletedAtText => !string.IsNullOrWhiteSpace(CompletedAtText);

    public void MarkCompletedAt(DateTime completedAt)
    {
        Timestamp = completedAt;
        CompletedAtText = completedAt.ToString("HH:mm", CultureInfo.CurrentCulture);
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private (string? Title, string Body) ParseThinking()
    {
        var content = Thinking.Replace("[REDACTED]", string.Empty).Trim();
        if (string.IsNullOrEmpty(content))
        {
            return (null, string.Empty);
        }

        var match = ThinkingTitleRegex.Match(content);
        if (!match.Success)
        {
            return (null, content);
        }

        return (match.Groups[1].Value.Trim(), content[match.Length..].TrimEnd());
    }

    private static string FirstThinkingLine(string text)
    {
        using var reader = new StringReader(text.Trim());
        return reader.ReadLine()?.Trim() ?? string.Empty;
    }
}




