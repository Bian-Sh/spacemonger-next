/* Agent Adapter
   一个非常轻量的 Agent 管理器示例：
   - 维护一组内置 agents（示例）
   - 提供 list/get/generate 接口
   - 使用 Provider.generate 发起 LLM 调用
*/

import type { Provider } from "./provider_interface"

export type AgentInfo = {
  name: string
  description?: string
  prompt?: string
  model?: { providerID: string; modelID: string }
  temperature?: number
}

export class AgentAdapter {
  private agents: Record<string, AgentInfo> = {}
  constructor(private readonly provider: Provider) {
    // 预置一些示例 agent
    this.agents["build"] = {
      name: "build",
      description: "默认 agent：根据权限执行工具",
      prompt: "You are an AI coding assistant.",
    }
    this.agents["explore"] = {
      name: "explore",
      description: "用于快速搜索代码",
      prompt: "You are a file search specialist.",
    }
  }

  async list(): Promise<AgentInfo[]> {
    return Object.values(this.agents)
  }

  async get(name: string): Promise<AgentInfo | undefined> {
    return this.agents[name]
  }

  async generate(description: string, model?: { providerID: string; modelID: string }) {
    const modelRef = model ?? { providerID: "anthropic", modelID: "claude-2" }
    const messages = [
      { role: "system", content: `Create an agent config based on: ${description}` },
      { role: "user", content: description },
    ]
    const res = await this.provider.generate({ model: modelRef, messages, temperature: 0.3 })
    return res.text
  }
}
