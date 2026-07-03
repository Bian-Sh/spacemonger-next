/* Provider 抽象接口示例
   这只是示例 TypeScript 接口文件，方便理解需要实现的能力。
   你可以把这个接口迁移到 C#（定义相应的接口/类并实现）。
*/

export type ProviderID = string
export type ModelID = string

export type ModelRef = {
  providerID: ProviderID
  modelID: ModelID
}

export type ModelInfo = {
  id: ModelID
  providerID: ProviderID
  language?: string
}

export type GenerateResult = {
  text: string
  // 其他字段：tokens/cost 等
}

export interface Provider {
  // 解析并返回 provider 的 language / canonical model id
  getModel: (providerID: ProviderID, modelID: ModelID) => Promise<ModelInfo>

  // 生成一次性输出
  generate: (params: {
    model: ModelRef
    messages: { role: "system" | "user" | "assistant"; content: string }[]
    temperature?: number
    topP?: number
    schema?: unknown
  }) => Promise<GenerateResult>

  // 可选的流式接口
  stream?: (params: {
    model: ModelRef
    messages: { role: string; content: string }[]
    onPart: (part: string) => void
  }) => AsyncIterable<string>
}
