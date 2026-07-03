# Agent Integration Scaffold

这个目录包含一个轻量级的 agent-provider scaffold（示例实现与文档），用于帮助你把现有硬编码的 Anthropic 调用替换为可插拔的 Provider 抽象。因为你的项目是 C# 且仓库中 docs 很多，所有新增内容都放在 docs/agent-scaffold 下，便于审阅与迁移。

目录结构（本 PR 添加）：

- docs/agent-scaffold/AGENT_INTEGRATION.md  — 设计说明与迁移步骤（中文）
- docs/agent-scaffold/provider_interface.ts — Provider 抽象接口示例（TypeScript，可迁移到 C#）
- docs/agent-scaffold/anthropic_provider_adapter.ts — 把现有 Anthropic 调用包装为 Provider 接口的示例适配器
- docs/agent-scaffold/agent_adapter.ts — 轻量 Agent 管理器示例（list/get/generate）
- docs/agent-scaffold/runtime_adapter.ts — 最小 runtime 封装示例（runPromise 风格）
- docs/agent-scaffold/agent-config.example.json — agent 配置示例

提示：示例代码以 TypeScript 给出以便直接参考 opencode 的实现；如果你希望我把示例改写成 C#（.NET）版本，我可以在后续提交中完成。
