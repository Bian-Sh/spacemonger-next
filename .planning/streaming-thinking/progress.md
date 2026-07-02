# 进度记录

- 初始化计划：准备梳理仓库与参考实现。
- 已用 codegraph 确认索引可用且 up to date。
- 已参考 opencode reasoning part 的折叠/展开交互与 reasoning delta 数据流。
- 已实现 `ChatMessage` thinking 摘要/正文/预览派生属性。
- 已修改 ChatPanel：折叠态流式展示摘要，展开态展示正文。
- 已修改 ChatViewModel：thinking/text token 通过 UI Dispatcher 追加。
- 已修改 ChatPanel code-behind：消息 Text/Thinking 变化时自动滚动到底部。
- 已新增 Core 回归测试 `ChatMessageTests`。
- 验证：Core targeted tests 6 passed；App ChatViewModel tests 27 passed；solution build 0 errors；publish Release 到 outputs 成功。
