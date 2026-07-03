/* Runtime Adapter (示例)
   展示如何在你的应用中提供一个简单的 runPromise 接口来调用 agent 功能。
   在 C# 项目中，你可以把这映射为一个后台服务/单例，并把 provider 注入进去。
*/

import { AgentAdapter } from "./agent_adapter"
import { AnthropicProvider } from "./anthropic_provider_adapter"

// 示例：初始化
export function initAgentRuntime(anthropicClient: any) {
  const provider = new AnthropicProvider(anthropicClient)
  const agent = new AgentAdapter(provider)
  return { provider, agent }
}

// 示例：在你的应用中调用
export async function exampleRun(anthropicClient: any) {
  const { agent } = initAgentRuntime(anthropicClient)
  const list = await agent.list()
  const generated = await agent.generate("A helpful file-organizer agent that suggests cleanup tasks")
  return { list, generated }
}
