using FluentAssertions;
using SpaceMonger.Core.Models;

namespace SpaceMonger.Core.Tests;

public class ChatMessageTests
{
    [Fact]
    public void ThinkingSummary_ExtractsTitleAndBodyFromStreamingReasoning()
    {
        var message = new ChatMessage
        {
            Thinking = "**Inspecting cleanup plan**\n\nReviewing candidate folders."
        };

        message.HasThinking.Should().BeTrue();
        message.ThinkingTitle.Should().Be("Inspecting cleanup plan");
        message.ThinkingPreview.Should().Be("Inspecting cleanup plan");
        message.ThinkingBody.Should().Be("Reviewing candidate folders.");
    }

    [Fact]
    public void ThinkingSummary_UsesFirstLineWhenNoTitle()
    {
        var message = new ChatMessage
        {
            Thinking = "Reviewing candidate folders.\nChecking risk."
        };

        message.ThinkingTitle.Should().BeNull();
        message.ThinkingPreview.Should().Be("Reviewing candidate folders.");
        message.ThinkingBody.Should().Be("Reviewing candidate folders.\nChecking risk.");
    }

    [Fact]
    public void Thinking_NotifiesSummaryProperties()
    {
        var message = new ChatMessage();
        var changed = new List<string?>();
        message.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        message.Thinking = "streaming";

        changed.Should().Contain(new[]
        {
            nameof(ChatMessage.Thinking),
            nameof(ChatMessage.HasThinking),
            nameof(ChatMessage.ThinkingTitle),
            nameof(ChatMessage.ThinkingBody),
            nameof(ChatMessage.ThinkingPreview),
            nameof(ChatMessage.HasThinkingBody)
        });
    }
}
