using System.Text;
using System.Text.Json;
using SpaceMonger.Core.Models;
using SpaceMonger.Core.Services.Copilot;
using SpaceMonger.Core.Services.Llm;

namespace SpaceMonger.Core.Services.Agent;

public sealed class AgentRuntime : IAgentRuntime
{
    private const int MaxToolRounds = 4;
    private const int MaxToolCalls = 8;
    private const int MaxObservationChars = 16_000;

    private readonly ILlmClient _llmClient;
    private readonly IReadOnlyDictionary<string, IAgentTool> _tools;

    public AgentRuntime(ILlmClient llmClient, IEnumerable<IAgentTool> tools)
    {
        _llmClient = llmClient;
        _tools = tools.ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<AgentResponse> RunAsync(
        AgentContext? context,
        IReadOnlyList<(string Role, string Content)> conversationHistory,
        string userMessage,
        IReadOnlyList<AiSkill> activeSkills,
        string? responseLanguage,
        string apiKey,
        string? baseUrl,
        bool enableThinking,
        Action<string>? onThinkingToken,
        CancellationToken cancellationToken)
    {
        var messages = conversationHistory.ToList();
        messages.Add(("user", BuildUserMessage(context, userMessage)));

        var systemPrompt = BuildSystemPrompt(activeSkills, responseLanguage);
        var allToolResults = new List<AgentToolResult>();
        var toolCallCount = 0;
        var reachedToolLimit = false;

        for (var round = 0; round <= MaxToolRounds; round++)
        {
            var assistantText = await CompleteAssistantTurnAsync(
                systemPrompt,
                messages,
                apiKey,
                baseUrl,
                enableThinking,
                onThinkingToken,
                cancellationToken).ConfigureAwait(false);

            var toolCalls = TryParseToolCalls(assistantText);
            if (toolCalls.Count == 0)
            {
                return new AgentResponse(assistantText, [], allToolResults, reachedToolLimit, ExtractProposal(allToolResults));
            }

            if (round == MaxToolRounds || toolCallCount >= MaxToolCalls)
            {
                reachedToolLimit = true;
                break;
            }

            var allowedCalls = toolCalls.Take(MaxToolCalls - toolCallCount).ToList();
            if (allowedCalls.Count < toolCalls.Count)
            {
                reachedToolLimit = true;
            }

            messages.Add(("assistant", assistantText));

            var roundResults = new List<AgentToolResult>();
            foreach (var toolCall in allowedCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ExecuteToolAsync(context, toolCall, cancellationToken).ConfigureAwait(false);
                roundResults.Add(result);
                allToolResults.Add(result);
                toolCallCount++;
            }

            messages.Add(("user", BuildObservationMessage(roundResults, reachedToolLimit)));
        }

        messages.Add(("user", "Tool call limit reached. Provide the best possible final answer from the observations already supplied. Do not request more tools."));
        var finalText = await CompleteAssistantTurnAsync(systemPrompt, messages, apiKey, baseUrl, enableThinking, onThinkingToken, cancellationToken).ConfigureAwait(false);
        return new AgentResponse(finalText, [], allToolResults, true, ExtractProposal(allToolResults));
    }

    private async Task<string> CompleteAssistantTurnAsync(
        string systemPrompt,
        List<(string role, string content)> messages,
        string apiKey,
        string? baseUrl,
        bool enableThinking,
        Action<string>? onThinkingToken,
        CancellationToken cancellationToken)
    {
        if (!enableThinking)
        {
            return await _llmClient.SendChatAsync(
                systemPrompt,
                messages,
                apiKey,
                baseUrl,
                cancellationToken).ConfigureAwait(false);
        }

        var response = await _llmClient.StreamChatWithThinkingAsync(
            systemPrompt,
            messages,
            apiKey,
            baseUrl,
            enableThinking,
            onThinkingToken,
            null,
            cancellationToken).ConfigureAwait(false);

        return response.Text;
    }

    private static JsonElement? ExtractProposal(IReadOnlyList<AgentToolResult> toolResults)
    {
        foreach (var result in toolResults.Reverse())
        {
            if (!string.Equals(result.ToolName, "propose_copilot_action", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (result.Content.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (result.Content.TryGetProperty("proposal", out var proposal) && proposal.ValueKind == JsonValueKind.Object)
            {
                return proposal.Clone();
            }
        }

        return null;
    }


    private async Task<AgentToolResult> ExecuteToolAsync(AgentContext? context, AgentToolCall toolCall, CancellationToken cancellationToken)
    {
        if (!_tools.TryGetValue(toolCall.Name, out var tool))
        {
            return new AgentToolResult(
                toolCall.Id,
                toolCall.Name,
                true,
                JsonError("unknown_tool", $"Unknown tool: {toolCall.Name}"),
                $"Unknown tool: {toolCall.Name}");
        }

        if (tool.RequiresScanContext && (context?.Session is null || context.CurrentViewRoot is null))
        {
            return new AgentToolResult(
                toolCall.Id,
                tool.Name,
                true,
                JsonError("scan_context_unavailable", "A completed scan context is required for file tree tools."),
                "A completed scan context is required for file tree tools.");
        }

        var toolContext = context ?? new AgentContext(null, null, null, null, false);
        try
        {
            var content = await tool.ExecuteAsync(toolContext, toolCall.Arguments, cancellationToken).ConfigureAwait(false);
            var isError = content.ValueKind == JsonValueKind.Object
                && content.TryGetProperty("ok", out var ok)
                && ok.ValueKind == JsonValueKind.False;

            return new AgentToolResult(toolCall.Id, tool.Name, isError, content, isError ? "Tool returned an error result." : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolResult(
                toolCall.Id,
                toolCall.Name,
                true,
                JsonError("execution_failed", ex.Message),
                ex.Message);
        }
    }

    private string BuildSystemPrompt(IReadOnlyList<AiSkill> activeSkills, string? responseLanguage)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are SpaceMonger Copilot, an open skill-driven agent host specialized for disk scanning, MFT-backed space analysis, registry-assisted app discovery, and cleanup planning.");
        builder.AppendLine($"Answer language: {FormatResponseLanguageInstruction(responseLanguage)}.");
        builder.AppendLine("This answer-language rule overrides the language used inside fetched skill records or skill source text.");
        builder.AppendLine();
        builder.AppendLine("Guidelines:");
        builder.AppendLine("- The answer-language setting is explicit app policy. Follow it even when the user's message is in another language.");
        builder.AppendLine("- You are agent-first: understand the user's request yourself before choosing tools.");
        builder.AppendLine("- The host does not route natural-language intents with keyword enums; skills and model reasoning define the workflow, risk model, and next action proposal.");
        builder.AppendLine("- Treat built-in disk and registry capabilities as exposed tools, not app-side hardcoded cleanup policies.");
        builder.AppendLine("- Skills explicitly mentioned with @skill are pre-injected. For unmentioned disk-management workflows, use manage_disk_skills list/read to discover and read relevant built-in or user skills before choosing a workflow.");
        builder.AppendLine("- For path-specific cleanup recommendation workflows, read the built-in path-cleanup-recommendation skill and follow it. Do not replace that workflow with generic file-tree summaries, list_children, summarize_subtree, or find_large_files unless the user only asks for a read-only explanation rather than generated recommendations.");
        builder.AppendLine("- If no skill is pre-injected but the user asks to scan, inspect disk usage, or create cleanup recommendations, reason from the skill catalog and generic disk tools instead of asking repeated follow-up questions.");
        builder.AppendLine("- Capability boundary: you cannot directly read the live filesystem, expand environment variables, validate path existence, start scans, navigate the UI, or delete files from prose alone. Use host tools for those actions.");
        builder.AppendLine("- For any user-supplied path that may include environment variables, relative segments, or ambiguity, call resolve_path first. Use resolved_path in later tool calls and in your explanation.");
        builder.AppendLine("- If resolve_path returns can_scan=true for an explicit scan request, call propose_copilot_action with kind=StartScan and the resolved_path. Do not tell the user to click a scan card or start button for clear low-risk scan requests; the host executes them directly.");
        builder.AppendLine("- If the user asks for cleanup recommendations for a path that already matches the current scan context, prefer kind=AnalyzeCleanup over StartScan. Only rescan when no matching scan exists or the user explicitly asks to refresh/rescan.");
        builder.AppendLine("- When a selected skill requires analysis after a successful scan, put the next agent instruction in propose_copilot_action.follow_up_prompt. The follow-up should explicitly instruct the next agent turn which tool/action to call, not merely summarize the user request. The host only continues from this explicit agent-provided field, never from app-side keyword matching.");
        builder.AppendLine("- If resolve_path reports can_scan=false or an error, do not propose scanning. Explain the resolved path and why it cannot be scanned.");
        builder.AppendLine("- Call manage_disk_skills with list/read when you need to discover which disk-management skill applies to the user's request.");
        builder.AppendLine("- Call manage_disk_skills with create/update/delete only when the user explicitly asks to manage skills. For creation, first understand the intended workflow. Create/update only disk-management skills implementable with available SpaceMonger host tools; refuse non-disk or unsupported skill requests without calling internal tools.");
        builder.AppendLine("- If the user asks about a path/folder but there is no scan context or the target is outside the current scan, do not stop with an error.");
        builder.AppendLine("- In that case, call get_copilot_context if current state matters, call resolve_path for the requested path, then call propose_copilot_action with kind=StartScan and the resolved path when can_scan=true.");
        builder.AppendLine("- If the user asks what a feature means or how to use it, answer directly; do not require a scan unless execution actually depends on one.");
        builder.AppendLine("- To create a first-level confirmation card, call the proposal tool instead of merely describing that a card could exist.");
        builder.AppendLine("- Use confirmation cards for ambiguous requests, destructive/irreversible actions, existing scan/recommendation overwrite decisions, and skill workflows that explicitly require user confirmation. Do not use app modal dialogs from chat; create the card by proposing the host action with will_overwrite_existing_data=true. Do not use cards for clear low-risk scans or recommendation analysis that will not overwrite existing data.");
        builder.AppendLine("- After proposing a directly executable low-risk action, keep the final answer terse. Do not say a card is ready, do not mention a start button, and do not add unrelated guidance.");
        builder.AppendLine("- You MUST use tools for path lookup, name lookup, list children, subtree summary, largest files, or multi-step deep-dive requests.");
        builder.AppendLine("- Never claim to execute destructive commands, delete files, or move files. Tool calls can return observations or user-confirmed proposals; obey each tool risk level and schema.");
        builder.AppendLine("- Base path and size claims on provided context or tool observations.");
        builder.AppendLine("- When you need a tool, respond with a single JSON object only, no markdown, no prose:");
        builder.AppendLine("  {\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"tool_name\",\"arguments\":{}}]}");
        builder.AppendLine("- After observations are provided, answer normally in the required answer language above, even when the user message uses another language.");
        builder.AppendLine();
        if (activeSkills.Count > 0)
        {
            builder.AppendLine("Explicitly selected skills for this turn:");
            foreach (var skill in activeSkills)
            {
                builder.AppendLine($"- {skill.Id}: {skill.Prompt}");
            }
            builder.AppendLine();
        }

        builder.AppendLine("Available tools:");

        foreach (var tool in _tools.Values.OrderBy(tool => tool.Name))
        {
            builder.AppendLine($"- {tool.Name} (risk: {tool.RiskLevel}): {tool.Description}");
            builder.AppendLine($"  schema: {tool.Schema.GetRawText()}");
        }

        return builder.ToString();
    }

    private static string FormatResponseLanguageInstruction(string? responseLanguage)
    {
        if (string.IsNullOrWhiteSpace(responseLanguage))
        {
            return "match the app UI language, and if unavailable match the user's language";
        }

        return responseLanguage.Trim() switch
        {
            "zh-CN" => "Simplified Chinese (zh-CN)",
            "zh" => "Chinese",
            "en" => "English",
            _ => responseLanguage.Trim()
        };
    }

    private static string BuildUserMessage(AgentContext? context, string userMessage)
    {
        if (context is null)
        {
            var appOnlyContext = new
            {
                scan_available = false,
                note = "No scan has been provided for this turn. App-level proposal tools may be used for directly executable scan proposals or confirmation-required discovery cards. Do not call file tree tools or claim scan data exists."
            };

            return $"App context JSON:\n{JsonSerializer.Serialize(appOnlyContext, AgentJson.Options)}\n\nUser question: {userMessage}";
        }

        if (context.Session is null || context.CurrentViewRoot is null)
        {
            var appOnlyContext = new
            {
                scan_available = false,
                note = "This turn has app-only context. File tree tools require a completed scan context."
            };

            return $"App context JSON:\n{JsonSerializer.Serialize(appOnlyContext, AgentJson.Options)}\n\nUser question: {userMessage}";
        }

        var contextBlock = new
        {
            current_view_path = context.CurrentViewRoot.Path,
            current_view_items = context.CurrentViewRoot.Children
                .OrderByDescending(child => child.Size)
                .Take(80)
                .Select(ToEntryContext),
            selected_item = BuildSelectedItem(context.LinkedEntry, context.LinkedRecommendation),
            scan_summary = new
            {
                root_path = context.Session.RootEntry?.Path,
                total_size_bytes = context.Session.TotalSize,
                total_files = context.Session.TotalFiles,
                total_folders = context.Session.TotalFolders,
                drive_capacity_bytes = context.Session.DriveCapacity,
                drive_free_space_bytes = context.Session.DriveFreeSpace
            }
        };

        return $"Scan context JSON:\n{JsonSerializer.Serialize(contextBlock, AgentJson.Options)}\n\nUser question: {userMessage}";
    }

    private static object? BuildSelectedItem(FileEntry? linkedEntry, CleanupRecommendation? linkedRecommendation)
    {
        if (linkedEntry is not null)
        {
            return ToEntryContext(linkedEntry);
        }

        if (linkedRecommendation is null)
        {
            return null;
        }

        return new
        {
            path = linkedRecommendation.TargetPath,
            size_bytes = linkedRecommendation.Size,
            category = linkedRecommendation.Category.ToString(),
            safety_rating = linkedRecommendation.SafetyRating.ToString(),
            type = linkedRecommendation.Entry?.IsDirectory == true ? "directory" : "file"
        };
    }

    private static object ToEntryContext(FileEntry entry)
    {
        return new
        {
            path = entry.Path,
            name = entry.Name,
            size_bytes = entry.Size,
            type = entry.IsDirectory ? "directory" : "file",
            extension = entry.Extension,
            child_count = entry.IsDirectory ? entry.Children.Count : 0
        };
    }

    private static string BuildObservationMessage(IReadOnlyList<AgentToolResult> results, bool reachedToolLimit)
    {
        var observationsJson = JsonSerializer.Serialize(new
        {
            tool_observations = results.Select(result => new
            {
                id = result.ToolCallId,
                tool = result.ToolName,
                is_error = result.IsError,
                content = result.Content
            }),
            reached_tool_limit = reachedToolLimit
        }, AgentJson.Options);

        if (observationsJson.Length > MaxObservationChars)
        {
            observationsJson = observationsJson[..MaxObservationChars] + "\n...TRUNCATED...";
        }

        return $"Tool observations JSON:\n{observationsJson}\n\nContinue. If you have enough evidence, provide the final answer. If another tool call is necessary and allowed by the tool schema/risk level, request another tool call JSON object.";
    }

    private static IReadOnlyList<AgentToolCall> TryParseToolCalls(string text)
    {
        var json = ExtractJsonObject(text);
        if (json is null)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<AgentToolCall>();
            var index = 0;
            foreach (var call in calls.EnumerateArray())
            {
                if (call.ValueKind != JsonValueKind.Object || !call.TryGetProperty("name", out var nameElement))
                {
                    continue;
                }

                var name = nameElement.GetString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var id = call.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString() ?? $"call_{index + 1}"
                    : $"call_{index + 1}";
                var arguments = call.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object
                    ? args.Clone()
                    : JsonSerializer.SerializeToElement(new { }, AgentJson.Options);

                results.Add(new AgentToolCall(id, name, arguments));
                index++;
            }

            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ExtractJsonObject(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineEnd = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLineEnd >= 0 && lastFence > firstLineEnd)
            {
                trimmed = trimmed[(firstLineEnd + 1)..lastFence].Trim();
            }
        }

        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            return trimmed;
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : null;
    }

    private static JsonElement JsonError(string code, string message)
    {
        return JsonSerializer.SerializeToElement(new
        {
            ok = false,
            error = new
            {
                code,
                message
            }
        }, AgentJson.Options);
    }
}




