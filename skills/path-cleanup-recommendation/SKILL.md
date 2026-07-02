---
name: path-cleanup-recommendation
description: Use when the user asks SpaceMonger Copilot to scan and analyze cleanup recommendations for a specific folder, drive, environment-variable path, or named local path.
---

# Path Cleanup Recommendation Skill

## Purpose
Run the native SpaceMonger Copilot workflow for “scan this path, then generate cleanup recommendations”.

## When To Use
Use this skill when the user asks to analyze, recommend cleanup for, or find cleanable items in a specific local path or folder. Examples include `%USERPROFILE%`, `D:\Downloads`, “Downloads”, “这个目录”, “当前文件夹”, or any user-selected path.

Do not use this skill when the user is only asking what the recommendation feature means, how to use it, or what existing recommendations say.

## Workflow
1. Validate the target path first. Call `resolve_path` for any user-supplied path, environment-variable path, relative path, or named folder before proposing scan or analysis.
2. Check current state. Use `Host disk context JSON` and, when state is not already clear, call `get_copilot_context` to inspect whether scan data or cleanup recommendation data already exists.
3. If existing scan data or recommendation data would be replaced, do not continue silently. Create the chat confirmation card by calling `propose_copilot_action` with `will_overwrite_existing_data=true`; the card lets the user choose overwrite/continue or cancel. Do not use an app modal dialog and do not ask in plain prose only.
4. Scan the validated target folder with `propose_copilot_action` using `kind=StartScan`, the resolved path, and `follow_up_prompt`. The follow-up prompt must be an explicit agent instruction to call `propose_copilot_action` with `kind=AnalyzeCleanup` for the current scan scope after the scan succeeds; do not make it a vague user-facing summary prompt.
5. In the follow-up turn, analyze cleanup candidates by calling `propose_copilot_action` with `kind=AnalyzeCleanup`, `will_overwrite_existing_data=false` unless context says recommendations already exist, and a scope label for the current scan.
6. Finish with a short completion message. Tell the user that generated recommendations are available in the recommendation list for review.


## Existing Scan Reuse
- After `resolve_path`, compare the resolved target path with `get_copilot_context.scan_root_path` and `get_copilot_context.current_view_path` when present.
- If the current conversation just completed the scan for that same path, do not scan again. Call `propose_copilot_action` with `kind=AnalyzeCleanup`, `will_overwrite_existing_data=false`, and `workflow_active_step_id="analyze_recommendations"`.
- If the app already has scan data for the same path but the user did not just ask to continue from that scan, ask through a chat confirmation card whether to use the existing scan data for analysis. Use `kind=AnalyzeCleanup`, `will_overwrite_existing_data=true`, `confirm_text` like "Use existing scan", and `cancel_text` like "Cancel". Do not silently rescan.
- Only call `kind=StartScan` when there is no matching scan context or the user explicitly asks to rescan/refresh the scan.

## Existing Data Policy
- Existing scan data means Treemap, TreeView, and AI-readable file tree context are already populated and a new scan would replace them.
- Existing recommendation data means the recommendation list/ScrollView already has generated cleanup candidates and a new analysis would replace them.
- If either kind of existing data matters for the next step, set `will_overwrite_existing_data=true` on the proposed host action so the chat input overlay confirmation card is shown.
- If there is no existing data to overwrite, set `will_overwrite_existing_data=false` so clear low-risk work can execute directly.
- When the user clicks the app’s native “分析” button directly, do not add an AI tool-call confirmation solely because the recommendation ScrollView already has data; that button path owns its own prompt. This skill governs AI-driven chat workflows.

## Safety
- Never delete, move, or modify files.
- Only scan metadata and generate reviewable recommendations.
- Actual cleanup remains controlled by the app’s separate cleanup confirmation flow.
