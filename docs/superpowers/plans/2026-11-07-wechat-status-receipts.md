# 微信状态回执实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans。逐项执行，使用 checkbox 跟踪，不派发实现子任务。

**Goal:** 将真实输入结果、暂停/恢复和 `/状态` 查询回复到已绑定微信私聊。

**Architecture:** Core 定义可加密持久化的回复上下文、回执和线程安全状态协调器。WeChat 实现发送协议，桌面程序只装配原有 Windows 输入执行器、协调器和独立发件循环；输入结果与回执在同次 DPAPI 保存中提交，网络请求在锁外执行。

**Tech Stack:** 当前 .NET 10 / C#、HttpClient、System.Text.Json、DPAPI、xUnit，无新增依赖。

**Spec:** `../specs/2026-11-07-wechat-status-receipts-design.md`

## Global Constraints

- 2026-11-07 用户明确要求考虑当前架构后开始执行，授权包含设计范围内的实现与绑定会话状态测试；保持当前会话内原生执行，不重复确认。
- 不实现手机切换/应用适配器或 AI 任务监测，只提供切换结果回执接入口。
- 无上下文、未授权身份及 Bot 回声不发送回复；所有上下文用 DPAPI 保存。
- Dispatched 不冒充 Verified，不因发送失败重复输入；Uncertain 暂停且禁止自动重放。
- 每秒最多 1 个发送请求；普通回执 99 条及查询保留位 1 条；5 分钟截止，失败间隔 2/5/15 秒，最多重试 3 次。
- 回执不含输入正文、路径、窗口/会话标题、Token 或原始错误正文。

## Review Focus

- 收件、发件、快捷键并发：同一状态协调器串行持久化，输入结果不被旧快照覆盖；Task 2。
- 输入已执行但回执未发送即崩溃：启动恢复不重新派发输入，Inserting 转 Uncertain；Task 2。
- 无转写/缺少 context_token/机器人回声：正常收件游标不导致回执循环或凭据泄露；Task 1、2。
- 发件超时和会话失效：只有限重试回执，失效同时停止收发；Task 1、3。
- 99 条普通回执后查询拥塞：暂停新输入，预留查询位且不覆盖在途请求；Task 2。

## Task 1: 回复上下文与发送协议

**Files:** `src/Connector.Core/Contracts.cs`、新增 `StatusReceipts.cs`；`src/Connector.WeChat/MessageParser.cs`、`ILinkClient.cs`；新增 `tests/Connector.Tests/StatusReplyProtocolTests.cs`。

**Interfaces:** `ReplyContext(AccountId, OwnerId, ContextToken, SourceMessageId, ReceivedAt)`；InboxItem 新增可选 Reply 和 InputAttempts，ParseResult 新增默认 false 的 Ignore；`ILinkClient.SendTextAsync(ReplyContext context, string clientId, string text, CancellationToken ct): Task`。

- [x] 先写发送 JSON/身份/上下文/错误脱敏/回声忽略/无转写上下文测试，运行确认失败。
- [x] 实现模型、解析与精确发送协议；上下文 ToString 隐藏敏感值。
- [x] 全套测试通过后提交 `feat: 增加微信状态回复协议与安全会话上下文`。

## Task 2: 状态协调与持久化发件箱

**Files:** 新增 Core `ReceiptFormatter.cs`、`ReceiptCoordinator.cs`；新增 `ReceiptCoordinatorTests.cs`。

**Interfaces:** `ReceiptSnapshot(Cursor, Events, Pending, AutoInput, AutoEnter, InputPaused, Skipped, LastContext, LastSendAt, Connected)`；`ReceiptCoordinator(snapshot, BoundIdentity identity, Action<ReceiptSnapshot> save, TimeProvider? clock)`。公开 `CommitBatch`、`ProcessAsync(key, writer, manual, ct)`、`ProcessNextAsync(writer, ct)`、`MarkSessionExpired`、`SetAutoInput`、`SetAutoEnter`、`AcknowledgeUncertain(clearRecovery)`、`ReportSwitch`、`TakeDue`、`CompleteSend`、`WaitForChangeAsync`。writer 返回 Core `InputOutcome(State, Failure, Application)`；发件返回 `ReceiptDelivery` 分类，不引用 Windows/WeChat 实现。

- [x] 先写状态语义、隐私、旧文件默认值、并发、崩溃、查询、重试/过期/容量、失败不重复输入测试，确认失败。
- [x] 实现统一锁内短事务，writer 与 sender 均锁外；持久化失败不提前更新内存或开始输入。
- [x] 全套测试通过后提交 `feat: 增加输入结果协调与有界状态回执队列`。

## Task 3: 桌面装配与独立发送循环

**Files:** `src/Connector.WeChat/ConnectionState.cs` 抽出兼容 DTO 并增加可选 ReceiptSnapshot，Program/PillWindow 用 ProbeState 别名；新增 `ReceiptSession.cs`；修改 PillWindow、TrayManager；新增 `ReceiptPump.cs` 及其 Core 测试。

**Interfaces:** `ReceiptPump.RunAsync(ReceiptCoordinator, Func<PendingReceipt,CancellationToken,Task<ReceiptDelivery>>,ct,onDelivery)`；App 仅负责 DPAPI 存取、客户端发送、NativeProbeResult 转 InputOutcome、设置和界面显示。每次重连先等待旧会话停止，不能多个轮询/发送循环并存。

- [x] 先写实际发件循环的失败/取消/会话失效与输入分离测试，确认失败。
- [x] 接入收件、`/状态`、手动补写及自动输入/回车设置；新增“已检查输入结果，解除暂停”托盘动作，明确丢弃 Uncertain 不重新输入。
- [x] 清理主动回传原始异常正文的显示/日志路径；旧 ProbeForm 保留但非启动入口，不重复改造其废弃执行路径。
- [x] 全套测试和 Release 构建通过后提交 `feat: 接入微信文本回执与暂停恢复操作`。

## Task 4: 审阅、发行和实际验证

**Files:** 更新 README、设计实施状态、`docs/verification/status-receipts.md`；发行到新的 artifacts 子目录，保留旧运行版本与加密状态备份。

- [x] 对 Task 1–3 整体执行一次独立审阅，针对 Important/Critical 先写失败测试再修复。
- [x] 运行全套测试、Release 构建及自包含发布；验证 Git 不包含凭据、上下文或运行数据。
- [x] 只读确认本项目正在运行的实例，备份 DPAPI 后在正常桌面替换为新版本。
- [ ] 用户已授权的绑定私聊内验证 `/状态`、输入结果及暂停回执。若等待手机消息，保留测试请求并继续其他验证，不伪称已通过。
- [x] 写出实际验证及未验证项，提交 `docs: 记录微信状态回执验证结果`。没有新的推送指令时不自动推送远程。
