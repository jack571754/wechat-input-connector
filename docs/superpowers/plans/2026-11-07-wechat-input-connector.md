# 微信 → Windows 文本连接器实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> 执行说明：本稿推荐当前会话原生执行，先完成 P0 再进入产品实现。此计划尚未获批，不执行下面的代码、安装、账号绑定或提交步骤。

**Goal:** 在 Windows 11 x64 上交付一个独立微信文本连接器，对符合条件的新消息自动写入当前输入框，并提供可靠的异常队列。

**Architecture:** C# 原生托盘程序通过 iLink Adapter 接收私聊；消息及游标同事务写入 SQLite，再交给串行输入调度器。Windows 输入辅助进程隔离 UI Automation 卡顿，通过同用户命名管道执行探测及受保护粘贴。

**Tech Stack:** .NET 10 LTS、Windows Forms、HttpClient、System.Text.Json、Microsoft.Data.Sqlite、Windows DPAPI、Win32/UI Automation、xUnit；安装器建议 Inno Setup。

**Spec:** `../specs/2026-11-07-wechat-input-connector-design.md`。设计稿与本计划必须一起阅读。

## Global Constraints

- 支持 Windows 11 x64 普通权限桌面会话；其他系统与架构后续按实际测试扩展。
- 用户已明确选择“自动输入：符合条件时直接写入当前输入框”。
- 首版不包含自建服务器、OpenClaw/AI Agent、手机 App、AI 润色、媒体发送、群聊、多电脑并行消费、远程命令执行、自动更新。暂不下载语音、不装本地 ASR、不接云端 ASR。
- 不抢焦点、不自动切换应用、不发送 Enter 或点击“发送”。
- 解锁、切换焦点、恢复网络、重新启动、从暂停恢复，都不自动补灌积压文字。
- Token、游标和待输入正文使用 DPAPI `CurrentUser` 保护；数据库/WAL/诊断不写入这些内容的明文。
- 待处理队列上限 500 条，正文默认最多保留 24 小时，去重元数据保留 7 天，日志滚动上限 10 MB。
- 自动输入时间窗 15 秒、未来偏差上限 5 秒、焦点探测超时 500 ms、单事件自动输入上限 10,000 个 UTF-16 代码单元。
- 每次产品改动先验证核心行为，再实现；全程简体中文界面、注释及交付说明。包名和技术标识保留标准英文。

## Review Focus

1. 扫码用户与消息发送者不一致、群消息、Bot 回声、缺失身份：不得触发输入；由任务 1、2 的协议夹具测试覆盖。
2. 正文与游标先后写入造成丢失、同文不同 ID 被误去重：必须同事务、按消息 ID 去重；由任务 2 的故障注入测试覆盖。
3. 真实已写入但回执丢失/崩溃：转 `Uncertain`，不自动重试；由任务 3、4 的崩溃恢复测试覆盖。
4. 剪贴板用户竞争、图片/文件/富文本、延迟消费：不破坏旧内容或覆盖新内容；由任务 3 的模拟竞争和实机测试覆盖。
5. 焦点瞬变、密码/管理员窗口、锁屏、终端、已选中文字、IME、快捷键按住：阻止输入并保持可见状态；由任务 3、4、6 覆盖。

## 文件与模块边界

以下路径相对未来工程根目录 `D:\project\01-自研项目\wechat-input-connector`；本轮不创建该目录。

```text
WeChatInputConnector.sln
global.json / Directory.Packages.props / NuGet.Config
src/Connector.Core/
  Contracts.cs             统一事件、焦点、收件批次及写入结果
  Services.cs              IInboxStore、IFocusProbe、ITextWriter
  InputPolicy.cs           自动/手动输入规则和状态转换
  InputCoordinator.cs      串行调度，不读取 Win32
src/Connector.WeChat/
  ILinkClient.cs           HttpClient 请求及协议 DTO
  QrLogin.cs               扫码、验证码、重定向状态机
  MessageParser.cs         身份过滤、顶层文本/语音转写提取
  PollingWorker.cs         唯一长轮询及退避
src/Connector.Storage/
  InboxStore.cs           收件箱、游标、去重和恢复事务
  DpapiProtector.cs        当前用户加密
  Migrations/001.sql       accounts、events、checkpoints
src/Connector.Windows/
  FocusProbe.cs           UIA、权限、锁屏及输入状态探测
  ClipboardWriter.cs      剪贴板快照、粘贴、消费和条件恢复
  NativeMethods.cs        必需 Win32 声明
  InputHelperHost.cs      同用户命名管道辅助进程
src/Connector.App/
  Program.cs              普通入口/--input-helper、单实例
  TrayApplication.cs      托盘及生命周期
  LoginForm.cs            二维码和验证码
  QueueForm.cs            队列与结果处理
  SettingsForm.cs         开关、改键、自启动
tests/Connector.Tests/    核心与协议/存储测试
tests/Connector.Windows.Tests/  本机 Windows 集成测试
tools/Connector.Probe/   P0 的交互式验证工具，使用同一协议模块
installer/connector.iss
docs/verification/      协议基线、实机兼容、性能与试用记录
```

核心接口在任务 1 固定，后续任务不得另造近似命名：

```csharp
record TextEvent(string AccountId, string MessageId, string SenderId,
    string Text, DateTimeOffset? CreatedAt, DateTimeOffset ReceivedAt);
record InboxBatch(string? NextCursor, IReadOnlyList<InboxItem> Items);
record InboxItem(string StorageKey, TextEvent? Event,
    EventState State, string ReasonCode);
record FocusSnapshot(nint Window, int ProcessId, string ElementId,
    string ProfileId, DateTimeOffset ObservedAt);
enum InputMode { Auto, Manual }
enum EventState { Queued, NeedsTranscript, Inserting, Dispatched, Verified,
    Uncertain, Rejected, Discarded, Expired }
enum DispatchOutcome { NotDispatched, Dispatched, Verified, Uncertain }
enum ClipboardOutcome { Untouched, Restored, UserChanged, PendingRecovery }
record WriteResult(DispatchOutcome Outcome, string ReasonCode,
    ClipboardOutcome Clipboard);
```

以上类型位于 Core。`PollBatch(string? NextCursor, IReadOnlyList<InboundMessage> Messages)` 与 `InboundMessage` 是任务 1 定义的 iLink DTO，均留在 `Connector.WeChat`。`ParseResult` 位于 Core，携带一个 `InboxItem`；正常 StorageKey 为无损消息 ID，缺少 ID 的拒绝记录使用 `rejected:` 加原始消息的 SHA-256 指纹，不生成可派发事件。批次不向 Storage 传递原始协议 DTO。

`IInboxStore`、`IFocusProbe`、`ITextWriter` 均在 Core，分别由 Storage、Windows 实现；协调器仅依赖接口。`FocusSnapshot` 仅能由成功通过策略检查的探测器产生；拒绝检查返回明确原因。协议数字 ID 用可无损读取的原始数字文本保存。Core 的 `ClipboardRecoveryRecord` 包含 EventId、允许格式的快照载荷、替换前/后序列号（均可空）、阶段及必须暂停标记，由 Storage 加密持久化；正文不得写入普通日志。

## 任务 1：P0 独立接入与输入路径验证（1–2 人日）

**Files:** 创建协议模块、核心契约、Probe 工具、最小 Windows 输入探测代码；创建 `tests/Connector.Tests/ILinkContractTests.cs`、`MessageParserTests.cs` 和 `docs/verification/p0.md`。

**Interfaces:** 产出 `ILinkClient.GetUpdatesAsync(string? cursor, CancellationToken ct): Task<PollBatch>`；`QrLogin.RunAsync(IProgress<LoginProgress>, CancellationToken): Task<LoginResult>`；`MessageParser.Parse(InboundMessage, BoundIdentity): ParseResult`。`LoginResult` 含 Token、BotId、OwnerId、BaseUrl；`BoundIdentity` 含 BotId、OwnerId。`ParseResult` 包含接受事件或拒绝/缺转写原因。

- [ ] 确认独立目录、Git 状态及 SDK；经用户确认本计划后才初始化工程、安装缺失 SDK，固定 .NET 10 的已验证补丁版本及包锁。遵循 using-git-worktrees 的环境检测规则，不改动其他工程。
- [ ] 写协议失败测试：`QrLogin_UsesPostAndLoginSpecificHeaders`、`QrLogin_HandlesVerifyCodeAndRedirect`、`QrLogin_RejectsUntrustedHost`、`Parser_RejectsOtherSenderGroupAndBotEcho`、`Parser_MissingIdentityOrMessageIdDoesNotAutoInput`。用脱敏夹具覆盖官方登录状态；断言 Token 不传向未允许域名。
- [ ] 运行 `dotnet test tests/Connector.Tests --filter "FullyQualifiedName~ILinkContractTests|FullyQualifiedName~MessageParserTests"`，确认因尚未实现行为而失败。
- [ ] 实现以上接口及协议 DTO；按固定源码提交构造请求，不直接复制第三方 README 的旧请求。支持取消、`-14` 暂停及启动/停止通知兼容探测；HTTP 重定向不能自动携带凭据跨域。
- [ ] 同一命令确认测试通过。再运行 `dotnet run --project tools/Connector.Probe -- --scenario login-and-receive`，由用户在手机完成扫码/必要验证码；不在聊天或日志展示 Token。
- [ ] 发送 100 条含唯一序号的测试文字到记事本，逐条比较目标结果；主动断网重连，记录身份字段、游标、超时和端到端耗时。Probe 只允许记事本测试窗口，不向其他前台窗口写入。
- [ ] 实测剪贴板消费确认与条件恢复；采样 30 条真实语音，记录总数、有转写数、空转写数及接收耗时。缺少完成标志/稳定 ID/身份映射或可靠输入路径即标为 P0 未通过，不进入任务 2。
- [ ] 将协议版本、合法域名、真实字段约束和输出证据写入 `p0.md`，不得以 mock 通过替代实测。完成独立提交 `feat: 验证微信独立接入与记事本输入路径`。

## 任务 2：可靠收件箱、安全存储与长轮询（1–2 人日）

**Files:** 创建 Storage 全部文件、`PollingWorker.cs`、`InboxStoreTests.cs`、`PollingWorkerTests.cs`；补齐 parser 测试。

**Interfaces:** 协议侧消费 `PollBatch` 并调用 parser，将 Core 的 `ParseResult` 组成 `InboxBatch`；Storage 不引用 WeChat。产出 Core 接口 `IInboxStore` 及其 `InboxStore` 实现，方法为 `CommitBatchAsync(string accountId, InboxBatch batch, CancellationToken ct): Task<CommitResult>`、`GetNextQueuedAsync(string accountId, CancellationToken): Task<TextEvent?>`、`SetStateAsync(string accountId, string messageId, EventState state, CancellationToken): Task`、`SaveClipboardRecoveryAsync(ClipboardRecoveryRecord record, CancellationToken): Task`、`ResolveClipboardRecoveryAsync(string eventId, ClipboardOutcome result, CancellationToken): Task`、`RecoverAsync(CancellationToken): Task`；`DpapiProtector.Protect(byte[]): byte[]` / `Unprotect(byte[]): byte[]`；`PollingWorker.RunAsync(CancellationToken): Task`。`CommitResult` 表示 Committed 或 StorageBlocked；失败不得返回可推进的游标。

- [ ] 写 `InboxStoreTests`：批次重投只产生一条事件、相同正文不同 ID 都保留、ID 大于浮点精确范围仍不冲突、模拟写盘失败后消息与游标一起回滚、非空新游标才替换、队列满 500 条暂停且游标不前移。
- [ ] 写 parser 测试：顶层条目按顺序合并且不拼引用、语音现成转写成功/缺失为 `NeedsTranscript`、过长/控制字符转人工处理；正文不静默截断或改写。
- [ ] 写恢复与保密测试：遗留 `Inserting` 转 `Uncertain`、正文超过 24 小时到期、去重元数据 7 天清理、数据库/WAL/日志找不到特定测试明文及 Token、DPAPI 当前用户往返成功；未解决剪贴板恢复记录使重启保持暂停，24 小时后删除快照但不自动解除暂停。
- [ ] 运行 `dotnet test tests/Connector.Tests --filter "FullyQualifiedName~InboxStoreTests|FullyQualifiedName~PollingWorkerTests|FullyQualifiedName~MessageParserTests"` 确认失败。
- [ ] 实现 SQLite 迁移及同事务提交、DPAPI 密文存储和恢复。数据库 `message_id` 列保存 InboxItem.StorageKey，唯一约束为 `(account_id,message_id)`；事务内不执行网络或 Windows 输入。Rejected/NeedsTranscript 同样有结果记录，队列容量及失败不允许静默丢弃。剪贴板恢复记录由主进程持有和写盘，不存于辅助进程专有内存。
- [ ] 实现唯一在途长轮询、可取消等待、2/4/8/16/30 秒加抖动退避和正常超时续订。`-14` 测试断言至少一小时内不继续请求旧会话；记录服务端超时建议的边界处理。
- [ ] 上述测试及 `dotnet build -c Release` 通过；重新跑真实断网、进程重启、重复批次和存储失败验证。提交 `feat: 持久化微信收件箱与接收游标`。

## 任务 3：受限目标识别与剪贴板执行器（1–2 人日）

**Files:** 完成 Windows 模块；创建 `FocusProbeTests.cs`、`ClipboardWriterTests.cs`、`HelperTimeoutTests.cs`；在 Windows.Tests 创建实机记事本夹具。

**Interfaces:** 产出 Core 的 `IFocusProbe.CaptureAsync(CancellationToken): Task<FocusCheck>` 及 `FocusProbe` 实现，其中 `FocusCheck` 是成功快照或拒绝原因的联合结果；Core 的 `ITextWriter.TryInsertAsync(TextEvent ev, FocusSnapshot target, CancellationToken ct): Task<WriteResult>` 及 `ClipboardWriter` 实现。辅助进程 IPC 传本次输入文本、焦点快照和剪贴板恢复握手，不传微信凭据；单次执行严格串行。主进程使用任务 2 的恢复记录接口，在快照持久化确认前不允许改写剪贴板，在替换后序列号持久化确认前不允许派发。

- [ ] 先写拒绝条件测试：密码/只读/禁用、未知控件、终端/地址栏、权限更高、锁屏、非空选区、IME 组合输入、修饰键未释放；断言未调用任何输入 API。
- [ ] 写焦点变化测试：探测后句柄/进程/ElementId 改变则零派发；辅助进程焦点探测 500 ms 超时则停止本次操作并暂停自动输入，不继续创建悬挂线程/进程。输入/消费确认 2 秒超时则进入 Uncertain/PendingRecovery，不能当成未派发重试。
- [ ] 写剪贴板竞争测试：纯文本快照往返；图片/文件/HTML/延迟渲染对象拒绝改写；获取快照后序列号改变则不覆盖；派发后用户新复制内容则不恢复旧内容；未确认消费时不使用固定延时强制恢复。
- [ ] 写恢复测试：辅助进程在替换前、替换后回执前、派发后分别崩溃，主进程均持有快照与阶段；PendingRecovery 持久化暂停。重新启动不得自动恢复未知归属剪贴板；用户确认保留当前内容后才能清理遗留记录，普通恢复开关不能跳过。
- [ ] 运行 `dotnet test tests/Connector.Tests --filter "FullyQualifiedName~FocusProbeTests|FullyQualifiedName~ClipboardWriterTests|FullyQualifiedName~HelperTimeoutTests"`，确认失败。
- [ ] 实现 Win32/UIA 探测、同用户命名管道 ACL、辅助进程生命周期及记事本目标适配。操作前后复核快照；不调用 SetForegroundWindow，不做全屏扫描，不使用 SetValue 替换全文。
- [ ] 实现剪贴板受限快照、序列号比较、`Ctrl+V` 派发与结果分类；只允许完整未派发失败返回 NotDispatched。部分派发/派发后异常返回 Uncertain，不进行另一种写入方式回退。
- [ ] 实现可回读控件的局部插入验证，日志不保存目标内容。`SendInput` 成功本身只产生 Dispatched；目标确实观察到插入才返回 Verified。
- [ ] 测试通过，再用 `dotnet test tests/Connector.Windows.Tests --filter "Category=Notepad"` 验证中文、英文、emoji、多行、选区、密码模拟控件和剪贴板恢复。记录实际结果后提交 `feat: 增加受保护的 Windows 文本输入执行器`。

## 任务 4：自动输入调度与异常恢复（0.5–1 人日）

**Files:** `InputPolicy.cs`、`InputCoordinator.cs`、`InputPolicyTests.cs`、`InputCoordinatorTests.cs`。

**Interfaces:** 消费 Core 的 `IInboxStore.GetNextQueuedAsync` / `SetStateAsync`、`IFocusProbe.CaptureAsync`、`ITextWriter.TryInsertAsync`。产出 `InputPolicy.Evaluate(TextEvent ev, InputContext context, InputMode mode): InputDecision`、`InputCoordinator.OnNewEventAsync(TextEvent ev, CancellationToken): Task`、`InsertNextManuallyAsync(CancellationToken): Task`。`InputContext` 明确承载开关、是否有积压、当前时间、会话/剪贴板状态；`InputDecision` 仅为 Allow 或 Queue(reason)。

- [ ] 写测试断言：合格新事件自动尝试一次；时间超过 15 秒/未来超过 5 秒/无时间转队列；有旧队列时新消息不越序；同文不同 ID 各一次；暂停时接收但不派发。
- [ ] 写测试断言：恢复网络、解锁、切换焦点、重启和恢复开关不排空旧队列；手动快捷键只处理最早一条 Queued，仍执行安全检查且等待修饰键释放。
- [ ] 写故障注入测试：Inserting 持久化后、派发后及终态入库前分别模拟崩溃；派发后崩溃不自动重放；Dispatched/Verified 不重复派发，Uncertain 只能由用户显式处理。
- [ ] 运行 `dotnet test tests/Connector.Tests --filter "FullyQualifiedName~InputPolicyTests|FullyQualifiedName~InputCoordinatorTests"` 确认失败。
- [ ] 实现上述策略、串行消费者及数据库合法状态转换。调度器不直接依赖 Win32；没有稳定结果的操作不得用 try-catch 改成“成功”。
- [ ] 全部测试通过；记事本真实运行复测 100 条，核对源序号、收件箱状态、目标文本和顺序。提交 `feat: 自动调度合格消息并保留异常队列`。

## 任务 5：托盘、绑定和可用便携包（0.5–1 人日）

**Files:** App 全部文件、`AppLifecycleTests.cs`、配置存储；记录 `docs/verification/p1.md`。

**Interfaces:** App 组合前四个任务的服务；`TrayApplication` 展示 Connecting/Connected/Reconnecting/SessionPaused 等连接状态、自动输入开关、队列数量与原因，不把消息正文写进系统通知。

- [ ] 写生命周期测试：第二个主进程不创建第二条轮询；`--input-helper` 不争抢主进程互斥锁；退出可取消长轮询并关闭辅助进程；快捷键冲突显示中文提示，不静默失效；配置无明文 Token。
- [ ] 运行 `dotnet test tests/Connector.Tests --filter FullyQualifiedName~AppLifecycleTests` 确认失败，然后实现 App 装配与单实例生命周期。
- [ ] 实现中文扫码窗口/验证码输入、托盘开关、队列查看、标记已处理/重新排队/丢弃、改键和默认关闭的开机启动。队列窗口操作不直接向自身或旧焦点写入，要求切到目标后按快捷键。
- [ ] 生命周期测试及 `dotnet build -c Release` 通过。运行 `dotnet publish src/Connector.App -c Release -r win-x64 --self-contained true -o artifacts/portable`，不启用未经测试的裁剪或 Native AOT。
- [ ] 普通用户实测扫码→手机发文字→记事本自动出现、暂停/恢复、队列手动插入、退出/重启；记录 P1 验收，通过后提供便携包。提交 `feat: 提供微信输入托盘程序和便携发行包`。

## 任务 6：目标应用兼容、安装和长期运行（2–4 人日）

**Files:** 按需新增 `Connector.Windows/Profiles/` 下的目标适配器；`installer/connector.iss`、`docs/verification/compatibility.md`、`soak.md`、用户说明及第三方许可。

**Interfaces:** 各目标适配器必须提供 ProfileId、允许的控件范围、消费/插入验证策略，保持任务 3 的 FocusCheck/WriteResult 语义，不让未知控件默认通过。

- [ ] 先记录各应用真实版本和控件信息，再为 Chrome 页面 input/textarea/contenteditable、微信聊天编辑区、Word 正文、Cursor 编辑区各写可重复的兼容验收用例。Cursor 终端及浏览器地址栏分别加入拒绝用例。
- [ ] 按目标新增失败测试和最小适配，每项跑通过后才加入允许列表。用 100 条序号消息及中文、emoji、多行逐项比对，不仅检查 API 返回值。只读/密码/选区/IME/焦点切换/管理员窗口重复验证。
- [ ] 安装器版本及使用条件经官方资料核实后锁定；生成每用户安装包，不要求管理员权限，测试全新无 SDK/.NET 的受支持电脑安装、绑定、重启、卸载。卸载明确提示是否保留本地数据，重新安装不重复消费旧事件。
- [ ] 至少 8 小时在线，插入断网、睡眠、锁屏、授权失效场景；记录恢复时间、重复/遗漏和所有 Uncertain。测量空闲 10 分钟合计平均 CPU ≤ 0.5%、私有内存 ≤ 120 MB、本地派发 p95 ≤ 500 ms；不达标记录根因与取舍，不能改成声称达标。
- [ ] 运行 `dotnet test -c Release`、`dotnet build -c Release` 和最终 publish；核对 Token/正文不出现在日志、配置、SQLite/WAL 明文，检查许可清单和发行哈希。
- [ ] 按 requesting-code-review 对照设计、上述五项 Review Focus 与实机记录独立审阅；缺陷修复后重跑受影响验证。提交 `feat: 完成兼容性验证与 Windows 安装交付`。

## 完成判定及后续

P0 通过才批准进入 P1；P1 对应任务 2–5，预计 3–6 人日；P2 对应任务 6。任务提交按真实工程状态执行，不在当前规划目录初始化 Git。总估算 6–12 人日，不能把 mock 通过、包能启动或请求成功当成产品验收完成。

P3 完整语音另写设计和计划：先依据样本决定是否需要下载/解密/解码及 ASR，再确认本地资源成本或云端费用/传输边界。本计划不预埋未选定 ASR 引擎或多套技术栈。
