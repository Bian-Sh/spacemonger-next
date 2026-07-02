# Findings

- opencode 的核心边界是 `tool` 定义：宿主提供 `id`、`description`、参数 schema 和 `execute`，模型根据 prompt/tool 描述决定何时调用；宿主不靠自然语言关键词枚举意图。
- 当前项目已经有类似边界：`AgentRuntime` 注入工具描述，`AppCopilotAgentTools` 提供 `propose_copilot_action`，文件树和 Unity registry 能力也在 tool 层。
- 原 `AiSkillRouter` 把中文/英文关键词、意图、动作卡和本地 FAQ 混在一起，导致多语言不可扩展，也绕过了 skill 中已有的风险算法与步骤声明。

## 2026-06-29 Agent host 收尾发现
- AiSkillRouter 现在只负责 skill prompt 注入和 @skill 选择，不再做中文/英文关键词意图分类。
- AgentRuntime 工具执行边界是模型显式 `tool_calls` JSON；宿主侧关键词预取/自动执行已通过测试防回归。
- 全量测试失败点仍是既有 RecommendationEngineTests.AnalyzeWithDiagnosticsAsync_AddsUnityLibraryRecommendationWhenProjectMarkersExist，不是本次 Copilot 路由改动引入。


## 2026-06-29 21:08:39 +08:00 Unity 风险模型边界
- Unity Library 的宿主侧职责应是发现候选和提供证据：Assets + ProjectSettings + Library、LastModified、UnityHubListed。
- 宿主不再根据 LastModified 或 Hub 缺失把候选自动判为 Safe/Caution；统一保持 ReviewFirst，符合“风险算法在 skill 里给 AI，而不是 app 硬编码”的方向。

## 2026-06-29 21:18:37 +08:00 Chat 自然语言路由边界
- /clear 属于显式 UI 命令，可以由宿主直接执行；自然语言清空/重置聊天不再由宿主短语表识别。
- ClearConversation 现在和其它动作一样走模型 proposal -> 确认卡 -> 用户确认执行，减少中文/英文关键词硬编码。

## 2026-06-29 21:29:48 +08:00 文件驱动 Skill Catalog
- AiSkillRouter 不再声明具体内置 skill 清单；开放扩展点变成 skills/<id>/SKILL.md 文件。
- 新 skill 的可见名称和说明来自 skill 文件自身，不需要在 app/router 中追加枚举、关键词或语言分支。

## 2026-06-29 21:40:18 +08:00 App-level proposal 工具边界
- propose_copilot_action 现在接受宿主符号动作名（StartScan/AnalyzeCleanup/DiscoverUnityLibraries/ClearConversation/NavigateToScannedPath）以及既有 snake_case 别名。
- 这不是自然语言关键词路由；skill/model 明确选择宿主 capability，App 只生成确认卡，不直接执行。
- app-only 工具通过 IAgentTool.RequiresScanContext=false 明确声明可在无扫描上下文下运行；文件树工具默认仍要求扫描上下文。

## 2026-06-29 21:49:45 +08:00 Router 和工具上下文边界
- AiSkillRoutingResult 删除未使用的 CanRunWithoutScanContext/PreferModelAnswer，AiSkillRouter 不再对 app-guide 做任何 id 特判。
- 文件树工具现在只在完成扫描上下文（Session + CurrentViewRoot）存在时执行；app-level tools 仍通过 RequiresScanContext=false 声明可无扫描运行。
- FileTreeAgentTools 通过 RequireScanSession 统一获得非空 ScanSession，避免半空 AgentContext 进入查询层。
- 本轮记录的工具错误：PowerShell 不支持 bash heredoc；apply_patch 返回 Access is denied；后续使用临时脚本精确替换，避免重复相同失败。

## 2026-06-29 21:56:45 +08:00 Runtime prompt capability 边界
- 旧 system prompt 曾把所有 tools 说成 read-only/in-memory scan tree，这与 propose_copilot_action、registry/app-level tools 不一致。
- 无 scan context 的 user message 也曾说只能回答 app-guide/identity，和 app-level scan proposal 目标冲突。
- 已改为：tool calls 可以返回 observations 或 user-confirmed proposals；无 scan 时允许 app-level proposal tools，但禁止 file tree tools 或伪造 scan data。

## 2026-06-29 22:02:53 +08:00 Chat 工具观察提示边界
- ChatService streaming thinking 不再把工具观察描述成 read-only file tree 或 no scan context ignored。
- 扫描上下文内外统一描述为 agent tool observation，并统计 succeeded/failed；tool limit 仍作为运行时事实保留。
- 这避免 UI 文案继续暗示宿主只有文件树工具，符合 app-level proposal/registry/disk capability 由 tool schema + skill prompt 声明的 Agent host 边界。

## 2026-06-29 22:05:14 +08:00 Agent tool 文案残留
- 全仓扫描旧标记后，唯一生产代码残留是 Settings 说明中的 read-only file tree tools。
- 已改为 agent tools，避免 UI 继续把能力边界绑定到文件树查询。

## 2026-06-29 22:16:54 +08:00 Skill 管理 toolcall 边界
- 默认注入全部 skills 会重新把 prompt 上下文变成宿主侧策略注入，不符合开放 Agent host；现在无 @skill 时不注入 skill 内容。
- 新增 manage_disk_skills 作为显式 toolcall 承载 list/read/create/update/delete；创建/更新只允许 domain=disk_management 且必须声明可用 SpaceMonger host tools。
- 非磁盘管理或宿主工具无法实现的 skill 创建请求，应由模型直接婉拒；工具层也会拒绝 unsupported_domain/missing_host_tools。

## 2026-06-30 09:31:20 +08:00 本轮 Agent/Skill 问题定位
- 截图中的“整理 Unity 项目”未触发 Unity skill：路由只认显式 @skill，普通自然语言没有注入 skill，模型只能在通用 prompt 下啰嗦或卡在 thinking。
- 修复方向保持去硬编码：不是在 app 中写 Unity 关键词路由，而是按 skill 自身声明文本做最小 token 匹配；未知 @skill 不回退自动匹配。
- DiscoverUnityLibraries 确认卡属于通用发现/扫描候选工作流，不应在卡片文案中写死 Unity 专名。

## 2026-06-30 10:24:16 +08:00 proposal 卡片/step 停滞根因
- 现场日志显示 Chat response completed; hasProposal=True，但 UI 没有 PendingInteractionCard，也没有 action executor/scan 日志。
- 根因：UI 层 ApplyProposalIfAny 只接受直接 {action, card} 且 kind 必须 PascalCase；真实模型/tool 结果可能返回 {ok,true, proposal:{...}} 或 snake_case kind（如 discover_unity_libraries），导致 proposal 被静默丢弃。
- 修复：兼容 wrapped proposal 与 snake_case kind，并在有 proposal 但不能转卡片时打 Warning。

## 2026-07-01 path cleanup recommendation skill
- `disk-management` 覆盖一般磁盘能力；路径级“扫描并推荐清理”应沉淀为通用内置 skill，而不是 userprofile 专用流程。
- 直接点击“分析”按钮时，`MainWindow.Console.cs` 已在推荐 ScrollView 有数据时弹窗确认；AI toolcall 不需要重复这一层。
- `AiSkillRouter` 不应再做自然语言关键词路由；Agent 通过 `manage_disk_skills` list/read 发现并读取适用 skill。
- 修正：`userprofile` 只是示例；最终实现为通用 `path-cleanup-recommendation`，并移除本地隐式路由。
