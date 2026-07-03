/* Anthropic Provider Adapter
   把现有 Anthropic 调用包装成 Provider 接口的示例。
   注意：此文件为示例，示范如何把现有硬编码调用迁移到可插拔适配器。
*/

import type { Provider, ModelRef, GenerateResult } from "./provider_interface"

// 假设你的项目中有一个 anthopicClient.generate(...) 方法
// 这里演示如何包装成 Provider 接口

export class AnthropicProvider implements Provider {
  constructor(private readonly client: any /* 你的 Anthropic 客户端 */) {}

  async getModel(providerID: string, modelID: string) {
    // 返回 model 信息，至少包括 language（或直接返回 modelID）
    return { id: modelID, providerID, language: "en" }
  }

  async generate(params: {
    model: ModelRef
    messages: { role: string; content: string }[]
    temperature?: number
    topP?: number
    schema?: unknown
  }): Promise<GenerateResult> {
    // 将 messages 转为你现在 Anthropic 使用的输入格式
    const prompt = params.messages.map((m) => `${m.role}: ${m.content}`).join("\n")
    // 这是伪代码，替换为你现有的调用
    const resp = await this.client.generate({ prompt, model: params.model.modelID, temperature: params.temperature ?? 0.3 })
    return { text: resp.text }
  }

  async *stream(params: any) {
    // 如果你支持流式输出，封装成 AsyncIterable
    // 否则可以省略
    const iter = await this.client.stream({ /* ... */ })
    for await (const part of iter) {
      yield part
    }
  }
}
