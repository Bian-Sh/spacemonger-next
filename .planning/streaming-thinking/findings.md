# 发现记录

- opencode 参考：`packages/tui/src/routes/session/index.tsx` 将 reasoning 作为独立 part 渲染，默认 hide 模式下折叠为一行标题/摘要，点击后展开完整 markdown；流式时 header 保持可见并显示 `Thinking` spinner。
- opencode 数据流：`packages/tui/src/context/data.tsx` 通过 `session.next.reasoning.started/delta/ended` 独立追加 reasoning text。
- 本项目已有 `ChatMessage.Thinking`、`IsThinkingExpanded` 和 XAML 折叠区，但折叠态只显示标题栏，不显示流式摘要；后台 streaming 回调直接改绑定对象，存在非 UI 线程更新风险。
- 最小方案：保留现有折叠区，给 `ChatMessage` 增加 thinking title/body/preview 派生属性，折叠态显示 preview，展开态显示 body；token 追加统一经 WPF Dispatcher。
