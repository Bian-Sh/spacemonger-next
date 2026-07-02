using System.Text.Json;
using SpaceMonger.Core.Services.Copilot;

namespace SpaceMonger.Core.Services.Agent;

public sealed class ManageDiskSkillsTool(ISkillPromptProvider skillPromptProvider) : AppCopilotToolBase
{
    private static readonly HashSet<string> ProtectedSkillIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "app-guide",
        "disk-management",
        "path-cleanup-recommendation",
        "unity-project-cleanup"
    };

    private static readonly HashSet<string> AllowedHostTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "get_copilot_context",
        "resolve_path",
        "propose_copilot_action",
        "read_unity_registry_context",
        "find_by_name",
        "find_by_path",
        "list_children",
        "summarize_subtree",
        "find_large_files"
    };

    public override string Name => "manage_disk_skills";

    public override string Description => "List and read built-in or user disk-management skills so the agent can discover native workflows; create, update, or delete user disk-management skills only when the user explicitly asks to manage skills. Create/update only for workflows implementable with SpaceMonger host tools; refuse non-disk or unsupported skill requests.";

    public override ToolRiskLevel RiskLevel => ToolRiskLevel.Medium;

    public override JsonElement Schema { get; } = SchemaJson("""
        {"type":"object","properties":{"operation":{"type":"string","enum":["list","read","create","update","delete"]},"id":{"type":"string","description":"Lowercase skill id using letters, digits, and hyphens."},"domain":{"type":"string","enum":["disk_management"]},"host_tools":{"type":"array","items":{"type":"string","enum":["get_copilot_context","resolve_path","propose_copilot_action","read_unity_registry_context","find_by_name","find_by_path","list_children","summarize_subtree","find_large_files"]}},"title":{"type":"string"},"description":{"type":"string"},"body_markdown":{"type":"string","description":"Concise SKILL.md body sections after Purpose. Use imperative instructions; put risk rules here, not in app code."},"overwrite":{"type":"boolean"}},"required":["operation"]}
        """);

    public override Task<JsonElement> ExecuteAsync(AgentContext context, JsonElement arguments, CancellationToken cancellationToken)
    {
        var operation = GetString(arguments, "operation")?.Trim().ToLowerInvariant();
        var id = GetString(arguments, "id")?.Trim();

        return Task.FromResult(operation switch
        {
            "list" => Json(new { ok = true, skills = skillPromptProvider.GetSkillCatalog() }),
            "read" => ReadSkill(id),
            "create" => CreateOrUpdate(arguments, id, overwrite: false),
            "update" => CreateOrUpdate(arguments, id, overwrite: GetBool(arguments, "overwrite") ?? true),
            "delete" => DeleteSkill(id),
            _ => JsonError("invalid_operation", "operation must be one of list, read, create, update, or delete.")
        });
    }

    private JsonElement ReadSkill(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return JsonError("missing_id", "id is required for read.");
        }

        var content = skillPromptProvider.GetRawContent(id);
        return content is null
            ? JsonError("not_found", "Skill does not exist.")
            : Json(new { ok = true, id, content });
    }

    private JsonElement CreateOrUpdate(JsonElement arguments, string? id, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return JsonError("missing_id", "id is required for create/update.");
        }

        if (!string.Equals(GetString(arguments, "domain"), "disk_management", StringComparison.OrdinalIgnoreCase))
        {
            return JsonError("unsupported_domain", "Only disk-management skills can be created or updated in this app.");
        }

        var hostTools = GetStringArray(arguments, "host_tools");
        if (hostTools.Count == 0)
        {
            return JsonError("missing_host_tools", "At least one SpaceMonger host tool must support the requested skill workflow.");
        }

        var unsupportedTools = hostTools.Where(tool => !AllowedHostTools.Contains(tool)).ToArray();
        if (unsupportedTools.Length > 0)
        {
            return Json(new { ok = false, error = new { code = "unsupported_host_tools", tools = unsupportedTools } });
        }

        if (ProtectedSkillIds.Contains(id))
        {
            return JsonError("protected_skill", "Built-in skills cannot be modified through this tool.");
        }

        var result = skillPromptProvider.CreateOrUpdateSkill(
            id,
            GetString(arguments, "title") ?? id,
            GetString(arguments, "description") ?? string.Empty,
            GetString(arguments, "body_markdown") ?? string.Empty,
            overwrite);

        return Json(new
        {
            ok = result.Success,
            id = result.SkillId,
            message = result.Message,
            content = result.Content,
            error = result.Success ? null : new { code = result.ErrorCode, message = result.Message }
        });
    }

    private JsonElement DeleteSkill(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return JsonError("missing_id", "id is required for delete.");
        }

        if (ProtectedSkillIds.Contains(id))
        {
            return JsonError("protected_skill", "Built-in skills cannot be deleted through this tool.");
        }

        var result = skillPromptProvider.DeleteSkill(id);
        return Json(new
        {
            ok = result.Success,
            id = result.SkillId,
            message = result.Message,
            error = result.Success ? null : new { code = result.ErrorCode, message = result.Message }
        });
    }

    private static bool? GetBool(JsonElement arguments, string name)
    {
        return arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToList();
    }

    private static JsonElement JsonError(string code, string message)
    {
        return Json(new { ok = false, error = new { code, message } });
    }
}
