# 手机切应用实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans。在当前会话逐任务实施，不派发实现子任务；完成后执行一次整体独立审阅。

**Goal:** 手机选择 Codex 或 Antigravity 后，后续新消息持续投到该应用的消息编辑器，可返回跟随光标，并在微信收到真实切换结果。

**Architecture:** Core 保存命令、逐条消息路由和切换结果，Windows 提供两个独立适配器及有时限的 UIA helper，ReceiptSession 只装配两者。切换与输入共用操作门闩，微信发件独立执行；所有影响窗口或文字的操作先持久化意图，再执行并保存结果。

**Tech Stack:** 现有 .NET 10、C#、Windows UI Automation、Win32、DPAPI、xUnit，无新运行依赖。

**Spec:** `../specs/2026-11-07-application-switching-design.md`

2026-11-08 增量：主要入口改为微信“切换应用”编号菜单，用户回复 1/2/0 选择；增加主菜单、返回、取消、帮助、状态查询。按用户最新要求删除微信暂停/恢复入口，帮助改为文字指令，主菜单仅三个数字选项。快捷命令保留。此为设计更新，尚未实施。

## Global Constraints

- 本文件和设计在用户确认前仅为文档，不修改产品代码。
- 持续投到选定应用；默认及 `/跟随` 返回无需绑定的当前焦点模式。
- 仅适配已运行的 Codex 桌面版和 Antigravity 独立桌面版当前聊天编辑器；不新建或选择应用内会话，不启动程序，不执行任意脚本。
- 命令完整匹配：`/应用` 或“切换应用”打开菜单，`/code`、`/anti`、`/跟随` 及设计规定的整句别名保留；保留 `/状态`，`/输入 ` 转义正文，未知斜杠命令不注入。
- 菜单编号固定为 1 Codex、2 Antigravity、0 跟随，冻结每次映射，生成后有效 2 分钟且仅消费一次；无菜单时数字保持普通文字，失效菜单的旧序号拦截一次，不自动切换或输入。
- 主菜单 1 切应用、2 状态、0 跟随；应用列表 1/2 为目标应用。返回按层级导航、取消只关菜单、帮助通过文字进入，均不向电脑发送按键或切换应用。单动作合并一条最终回复；不新增微信暂停/恢复命令。
- 控制指令过去最多 15 秒、未来最多 5 秒，重复及重启遗留指令不执行；历史积压不自动补写。
- 普通回执 99 条加 1 条查询保留位，每秒最多 1 请求、有效期 5 分钟、失败间隔 2/5/15 秒、最多重试 3 次，发送失败不得重做窗口操作或输入。
- 锁定模式每次输入核对可信窗口、真实祖先归属和同一编辑器；多个候选不猜测，不回落到任意焦点，不使用固定坐标、Alt+Tab 或主动切桌面。
- UIA 操作由本项目 helper 承担，单次总时限 5 秒，结束或超时均回收 helper；跟随模式不做后台焦点争抢。
- 所有输出、注释及回执使用简体中文；诊断不含正文、窗口标题、路径或上下文；窗口句柄/PID/UIA 引用不落盘。

## Review Focus

- 同批次控制和正文交错时，A/B 分别投到各自应用；查询反映此前操作结果，而非整批收件时的旧状态——Task 3。
- 后来切应用不能改变旧 Queued 条目的目标；手动补写也不能执行历史命令——Task 1、3。
- ChatGPT.exe 与 codex.exe 混淆、多个同名窗口、托管控件跨进程和探测超时不能误输入——Task 2。
- 焦点在验证后变化或多行转 Enter，不能把文字送到其他控件或逐行提交——Task 2、3。
- 重启中断切换、存盘失败、发件重试、用户主动暂停及 Uncertain 暂停互不绕过——Task 3、4。

## Task 1: 命令及路由模型

**Files:** 新建 `src/Connector.Core/ConnectorCommand.cs`、`ApplicationRouting.cs`；修改 `Contracts.cs`、`StatusReceipts.cs`；新建 `tests/Connector.Tests/ConnectorCommandTests.cs`、`ApplicationRoutingTests.cs`。

**Interfaces:** `ApplicationId { Codex, Antigravity }`；`RouteMode { CurrentFocus, Application }`；`InputRoute(RouteMode Mode, ApplicationId? Application = null)`；`ApplicationRouting(InputRoute Route, bool Activated = false, bool InputReady = false, DateTimeOffset? LastCheckedAt = null)`。`ConnectorCommand.Parse(string text)` 返回可空命令对象，`Kind { Switch, Follow, Status, Literal, Unknown }`、可空 Application 及 LiteralText。InboxItem 增加可选 Route 及 Command；ReceiptSnapshot 增加可选 Routing，旧文件默认跟随。EventState 增加 Switching，区分切换处理中与文字 Inserting。新增模型置于上述文件，不引用 Windows 类型。

**菜单增量接口:** 同文件新增 `ApplicationCatalogEntry(int Number, ApplicationId Application, string DisplayName)`；`MenuKind { Main, Applications, Help }`；`MenuAction { OpenApplications, ShowStatus, SelectApplication, Follow }`；`MenuEntry(int Number, string DisplayName, MenuAction Action, ApplicationId? Application = null)`；`ConnectorMenuState(string Id, string ReceiptId, DateTimeOffset ExpiresAt, IReadOnlyList<MenuEntry> Entries, MenuKind Kind = MenuKind.Applications, MenuKind? Parent = null, bool Invalidated = false)`；ReceiptSnapshot 增加可选 Menu。CommandKind 增加 Menu、Home、Back、MenuChoice、Cancel、Help；`ConnectorCommand.Parse(string text, ConnectorMenuState? menu = null, DateTimeOffset? now = null)` 只在有效或失效待拒绝菜单上下文中解析序号。成功选出应用后持久化为 Switch/Follow，不能在恢复或重试时重新解析。菜单可执行状态重启作废并留下单次拒绝旧选择标记。

- [ ] 写失败测试：完整别名、大小写、首尾空白均准确匹配；“请讨论 code 和 anti”不是切换；`/输入 /anti` 得到普通正文 `/anti`；未知斜杠得到 Unknown；旧 JSON 无路由仍恢复为跟随；保存的消息路由不随当前路由变化。
- [ ] 菜单失败测试：固定编号、目录增加应用不重排、2 分钟边界、无菜单数字为正文、菜单内序号为控制、超长/无效纯数字不注入、取消、普通正文关闭菜单、Literal 优先转义、冻结映射不随运行窗口列表变化、过期/重启菜单只拒绝一次旧选择。
- [ ] 导航失败测试：同一数字按 Main/Applications 当前层级映射，0 始终跟随；Back 从 Applications 回 Main、Help 回 Parent，无菜单时回 Main；Cancel 不改 Route/AutoInput；帮助数字不解析为应用；冻结菜单动作而非根据当前层级重解释已保存的命令。
- [ ] 运行 `./scripts/dev.ps1 test tests/Connector.Tests --no-restore --filter "FullyQualifiedName~ConnectorCommandTests|FullyQualifiedName~ApplicationRoutingTests"`，确认因缺失接口或行为失败。
- [ ] 实现上述纯模型与解析，不修改当前运行版本；重跑对应测试及全套测试。
- [ ] 提交 `feat: 增加手机控制命令与持久化输入路由`。

## Task 2: 独立应用适配器与受约束输入

**Files:** 新建 Windows `Applications/IApplicationAdapter.cs`、`ApplicationWindowCatalog.cs`、`CodexAdapter.cs`、`AntigravityAdapter.cs`、`ApplicationProbeHost.cs`；修改 `FocusedInputProbe.cs`、`ProbePolicies.cs`、`tools/Connector.Probe/Program.cs`；新建 `tests/Connector.Windows.Tests/ApplicationAdapterTests.cs`。

**Interfaces:** `IApplicationAdapter.Application: ApplicationId`，`PrepareAsync(CancellationToken): Task<ApplicationPreparation>`；`ApplicationPreparation(ApplicationId Application, bool Activated, PreparedInputTarget? Target, string ReasonCode)`；`PreparedInputTarget(nint Window, int WindowProcessId, int[] EditorRuntimeId)` 仅内存/父子进程线协议使用，不能进入 DPAPI 快照。`FocusedInputProbe.InsertTargetedAsync(string text, PreparedInputTarget target, string recoveryPath, CancellationToken ct, bool autoEnter = false): Task<NativeProbeResult>`；Prepare 和 Insert 的执行均通过 ApplicationProbeHost，在同一操作截止时间内完成，输入前重验 token。helper 使用现有 OwnedHelperJob，所有分支明确返回无输入、已派发或结果不明。

- [ ] 写失败测试：可信安装身份与窗口匹配；CLI 不匹配 Codex；多个窗口按设计优先级或拒绝；Antigravity 消息 ComboBox 可选、其他输入框不选；Codex 无已验证选择器不就绪；跨 PID 只有真实祖先属于目标才允许；控件比较拒绝不能被 catch 吞掉；窗口消失、控件不唯一和 helper 超时均不执行 writer；多行不能逐行生成提交键。
- [ ] 运行 `./scripts/dev.ps1 test tests/Connector.Windows.Tests --no-restore --filter "FullyQualifiedName~ApplicationAdapterTests"`，确认失败。
- [ ] 实现窗口匹配及两个独立选择器。先只读核实 Codex UIA 暴露的标识/编辑语义，不能将 DOM 的 data 属性假定为 UIA ID；核实前保持未就绪。抽出可用假窗口/假控件驱动的策略边界，不通过真实桌面写入完成单元测试。
- [ ] 收紧原生执行器真实归属及稳定检查；换行与可选追加提交分开。无法可靠支持多行时明确 Queued，不回退到旧逐行 Enter 路径。
- [ ] 重跑对应及全套测试、Release 构建，提交 `feat: 增加 Codex 与 Antigravity 输入框适配`。

## Task 3: 顺序路由、持久化及状态回执

**Files:** 修改 `ReceiptCoordinator.cs`、`ReceiptFormatter.cs`、`ReceiptSession.cs`、`PillWindow.cs`；新建 Core `ApplicationCommandProcessor.cs`；扩展 `ReceiptCoordinatorTests.cs`、新建 `ApplicationCommandProcessorTests.cs`、更新仓库现有 `ConnectionStateTests.cs`。

**Interfaces:** `ApplicationCommandProcessor(ReceiptCoordinator coordinator, Func<ApplicationId,CancellationToken,Task<ApplicationSwitchOutcome>> prepare)`；`ApplicationSwitchOutcome(bool Activated, bool InputReady, DateTimeOffset CheckedAt)` 是 Core 纯结果；processor 的 `ProcessAsync(string storageKey, Func<TextEvent,InputRoute,CancellationToken,Task<InputOutcome>> writer, bool manual, CancellationToken ct): Task` 按已保存的 Command 路由到协调器。协调器的 `ProcessAsync`、`ProcessNextAsync` 的 writer 参数增加 InputRoute；新增 `ProcessControlAsync(string key, Func<ApplicationId,CancellationToken,Task<ApplicationSwitchOutcome>> prepare, CancellationToken ct): Task`，与原输入方法共用同一操作门闩，不嵌套获取门闩。`CommitBatch` 保留签名但改为仅去重、保存命令分类、逐条预定路由及游标，返回所有待按顺序处理的新条目，查询回执在 ProcessControlAsync 执行到它时才产生；缺转写回执也在逐条处理时生成，避免隐式提前执行。Literal 存储已解码正文及 Literal 标记，后续不能重新解析。

- [ ] 写失败测试：同批切 anti/A/切 code/B/状态，adapter 与 writer 调用次序、两条 Route 和查询结果正确；Literal 在后续处理不会二次解析；控制指令不写入且不进入手动补写；延迟/重复/重启指令不抢焦点。
- [ ] 菜单集成失败测试：打开菜单不调用 adapter/writer；同批菜单/1/A 依次发菜单、切 Codex、输入 Codex；选择去重且一次消费；菜单保存失败不对外发送；查询替换、过期和最终发送失败均使对应菜单失效；普通/查询队列不突破 99+1，菜单回执重试不切应用；其他身份无法消费菜单。
- [ ] 导航/查询集成失败测试：同批主菜单/1/2/A 最终向 Antigravity 输入；返回/取消/帮助不调用 adapter/writer；状态合并刷新菜单且只生成一条最终回执；所有导航及查询不改变 AutoInput/AutoEnter 或异常暂停；旧菜单发送失败不影响新菜单，新菜单不能被旧重试覆盖；格式化含菜单、状态及帮助的回复始终不超 500 文本元素。
- [ ] 写失败测试：切换前保存意图失败则 adapter 调用为 0；失败后新目标保留、旧目标 writer 调用为 0；旧队列路由不变；重启清就绪但保留目标，Switching 只标记指令中断且不重做，Inserting 仍变为 Uncertain 并暂停；定位成功不解除 Uncertain 或打开 AutoInput；回执重试不增加 adapter/writer 次数。
- [ ] 运行新测试确认失败，再实现顺序处理、逐条路由及查询/切换回执。现有查询/无转写测试改为经过 processor 后断言，继续验证身份、容量、节流、过期和幂等，不删除其约束。适配器准备失败返回固定原因；不是所有失败都进入 Uncertain 暂停。
- [ ] App 装配适配器，手动补写使用保存路由；UI 显示模式及最近确认时间，诊断只增加枚举、布尔值与计数。保持设置和旧 DPAPI 兼容。
- [ ] 运行全套测试和 Release 构建，提交 `feat: 接入手机切应用与持续定向输入`。

## Task 4: 独立审阅、发布和真实手机验收

**Files:** README、设计状态、新建 `docs/verification/application-switching.md`、现有启动脚本；自包含发行到 `artifacts/application-switching`。

- [ ] 对本轮提交范围执行一次整体独立审阅，修复重要问题并新增能复现问题的测试；不把策略单测称为真实 UI 验收。
- [ ] 运行 `./scripts/dev.ps1 test WeChatInputConnector.sln --no-restore`、`./scripts/dev.ps1 build WeChatInputConnector.sln -c Release --no-restore`，完成 win-x64 自包含发布并检查 Git 排除运行数据。
- [ ] 核对本项目正在运行实例，备份 DPAPI 与运行版本，在正常桌面替换新程序，保留原绑定；不清空积压。无需本轮推送远程。
- [ ] 用户手机分别验证 `/anti` 与 `/code`：窗口恢复、编辑器就绪、焦点丢失后新文字仍进目标、已有草稿保留、切换失败不会写旧窗口、返回 `/跟随`、微信切换及状态回执；初始关闭自动回车，随后独立验证提交行为。若某个应用无法可靠定位，清楚记录未完成，不标记整体通过。
- [ ] 手机验证“切换应用”收到文本菜单，回复 1/2/0 分别切到目标或跟随；无菜单数字可正常输入，过期或无效序号不会注入，重复旧选择不会切换，菜单与切换结果均在原私聊可见。
- [ ] 手机验证精简主菜单、返回上级、帮助后返回、取消保持目标；菜单内状态可见且一次只回一条，导航及查询不改变输入开关、不补写历史。
- [ ] 提交文档及实测结果，仅在两应用关键流程均实际通过后完成本轮功能；未通过项继续按 systematic-debugging 定位。

## 自检与执行方式

Task 1 定义 Core 类型供后续消费，Task 2 不将窗口对象传入持久化层，Task 3 同时修改收件查询时序和统一门闩以防跨应用乱序，Task 4 按应用验证。已有 112 项测试为基线；本计划要求新增跨模块行为覆盖，不以数量替代功能验收。

保留此前采用的执行方式：当前会话原生实现、一次最终独立审阅。用户确认本计划及设计后再写产品代码；不重复询问执行方式。
