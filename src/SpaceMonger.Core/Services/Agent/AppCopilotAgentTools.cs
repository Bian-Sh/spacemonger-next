using System.IO;
using System.Text.Json;
using SpaceMonger.Core.Services.Copilot;
using SpaceMonger.Core.Services.Scanning;

namespace SpaceMonger.Core.Services.Agent;

public abstract class AppCopilotToolBase : IAgentTool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract JsonElement Schema { get; }
    public virtual ToolRiskLevel RiskLevel => ToolRiskLevel.Low;
    public bool RequiresScanContext => false;

    public abstract Task<JsonElement> ExecuteAsync(AgentContext context, JsonElement arguments, CancellationToken cancellationToken);

    protected static JsonElement Json(object value)
    {
        return JsonSerializer.SerializeToElement(value, AgentJson.Options);
    }

    protected static JsonElement SchemaJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    protected static string? GetString(JsonElement arguments, string name)
    {
        return arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

public sealed class ResolvePathTool : AppCopilotToolBase
{
    public override string Name => "resolve_path";

    public override string Description => "Resolve a user-supplied Windows path before proposing or executing scan/navigation actions. Expands environment variables such as %USERPROFILE%, normalizes to a full path, and reports whether the target exists and is scannable as a directory.";

    public override JsonElement Schema { get; } = SchemaJson("""
        {"type":"object","properties":{"path":{"type":"string","description":"User-supplied path, including environment variables like %USERPROFILE% or relative paths."}},"required":["path"]}
        """);


    public override Task<JsonElement> ExecuteAsync(AgentContext context, JsonElement arguments, CancellationToken cancellationToken)
    {
        var inputPath = GetString(arguments, "path");
        if (!ScanPathResolver.TryResolve(inputPath, out var resolvedPath, out var error))
        {
            return Task.FromResult(Json(new
            {
                ok = false,
                input_path = inputPath,
                resolved_path = (string?)null,
                exists = false,
                is_directory = false,
                can_scan = false,
                error
            }));
        }

        var isDirectory = Directory.Exists(resolvedPath);
        var exists = isDirectory || File.Exists(resolvedPath);
        return Task.FromResult(Json(new
        {
            ok = true,
            input_path = inputPath,
            resolved_path = resolvedPath,
            exists,
            is_directory = isDirectory,
            can_scan = isDirectory,
            error = exists ? null : "Path does not exist."
        }));
    }
}

public sealed class GetCopilotContextTool : AppCopilotToolBase
{
    public override string Name => "get_copilot_context";
    public override string Description => "Get current scan, view, selection, and recommendation availability state for the copilot.";
    public override ToolRiskLevel RiskLevel => ToolRiskLevel.ReadOnly;
    public override JsonElement Schema { get; } = SchemaJson("""
        {"type":"object","properties":{}}
        """);

    public override Task<JsonElement> ExecuteAsync(AgentContext context, JsonElement arguments, CancellationToken cancellationToken)
    {
        return Task.FromResult(Json(new
        {
            ok = true,
            has_scan = context.Session is not null,
            scan_root_path = context.Session?.RootEntry?.Path,
            current_view_path = context.CurrentViewRoot?.Path,
            selected_path = context.LinkedEntry?.Path ?? context.LinkedRecommendation?.TargetPath,
            has_existing_recommendations = context.HasExistingRecommendations
        }));
    }
}

public sealed class ProposeCopilotActionTool : AppCopilotToolBase
{
    public override string Name => "propose_copilot_action";
    public override string Description => "Propose a host copilot action. Clear low-risk scan and recommendation-analysis actions may execute directly; ambiguous, destructive, or skill-confirmed workflows remain confirmation cards. Use follow_up_prompt only when the selected skill requires the agent to continue after a successful scan.";
    public override JsonElement Schema { get; } = SchemaJson("""
        {"type":"object","properties":{"kind":{"type":"string","enum":["StartScan","AnalyzeCleanup","DiscoverUnityLibraries","ClearConversation","NavigateToScannedPath","scan","analyze_cleanup","discover_unity_libraries","clear_conversation","navigate"]},"path":{"type":"string"},"scope_label":{"type":"string"},"will_overwrite_existing_data":{"type":"boolean"},"user_notes":{"type":"string","description":"Optional user constraints or supplemental instructions to pass with the action."},"follow_up_prompt":{"type":"string","description":"Optional agent-authored follow-up user prompt to run after this action succeeds. Use for skill workflows that need scan first, then another agent turn."},"workflow_steps":{"type":"array","description":"Optional agent-authored workflow indicator steps for the host UI. The host renders these generic steps without inferring natural-language intent.","items":{"type":"object","properties":{"step_id":{"type":"string"},"title":{"type":"string"}},"required":["step_id","title"]}},"workflow_active_step_id":{"type":"string","description":"Optional step_id from workflow_steps that corresponds to the proposed action currently being executed."},"title":{"type":"string"},"description":{"type":"string"},"impact":{"type":"string"},"confirm_text":{"type":"string"},"cancel_text":{"type":"string"}},"required":["kind"]}
        """);

    public override Task<JsonElement> ExecuteAsync(AgentContext context, JsonElement arguments, CancellationToken cancellationToken)
    {
        var kind = GetString(arguments, "kind")?.Trim();
        var path = GetString(arguments, "path")?.Trim();
        var scopeLabel = GetString(arguments, "scope_label")?.Trim();
        var title = GetString(arguments, "title")?.Trim();
        var description = GetString(arguments, "description")?.Trim();
        var impact = GetString(arguments, "impact")?.Trim();
        var userNotes = GetString(arguments, "user_notes")?.Trim();
        var followUpPrompt = GetString(arguments, "follow_up_prompt")?.Trim();
        var confirmText = GetString(arguments, "confirm_text")?.Trim();
        var cancelText = GetString(arguments, "cancel_text")?.Trim();
        var workflowSteps = GetWorkflowSteps(arguments);
        var workflowActiveStepId = GetString(arguments, "workflow_active_step_id")?.Trim();
        var willOverwrite = arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("will_overwrite_existing_data", out var overwriteProp)
            && overwriteProp.ValueKind is JsonValueKind.True or JsonValueKind.False
            && overwriteProp.GetBoolean();

        var actionKind = ResolveActionKind(kind);

        if (actionKind == AiActionKind.None)
        {
            return Task.FromResult(Json(new { ok = false, error = new { code = "invalid_kind", message = "Unsupported copilot action kind." } }));
        }

        return Task.FromResult(Json(new
        {
            ok = true,
            proposal = new
            {
                action = new
                {
                    kind = actionKind.ToString(),
                    path,
                    scope_label = scopeLabel,
                    will_overwrite_existing_data = willOverwrite,
                    user_notes = userNotes
                },
                card = new
                {
                    title,
                    description,
                    impact,
                    confirm_text = confirmText,
                    cancel_text = cancelText,
                    follow_up_prompt = followUpPrompt
                },
                workflow_steps = workflowSteps,
                workflow_active_step_id = workflowActiveStepId
            }
        }));
    }

    private static IReadOnlyList<object> GetWorkflowSteps(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("workflow_steps", out var steps)
            || steps.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<object>();
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var stepId = GetString(step, "step_id")?.Trim();
            var title = GetString(step, "title")?.Trim();
            if (string.IsNullOrWhiteSpace(stepId) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            result.Add(new { step_id = stepId, title });
        }

        return result;
    }
    private static AiActionKind ResolveActionKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return AiActionKind.None;
        }

        if (Enum.TryParse<AiActionKind>(kind, ignoreCase: true, out var actionKind))
        {
            return actionKind;
        }

        return kind switch
        {
            "scan" => AiActionKind.StartScan,
            "analyze_cleanup" => AiActionKind.AnalyzeCleanup,
            "discover_unity_libraries" => AiActionKind.DiscoverUnityLibraries,
            "clear_conversation" => AiActionKind.ClearConversation,
            "navigate" => AiActionKind.NavigateToScannedPath,
            _ => AiActionKind.None
        };
    }
}
