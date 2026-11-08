# 双向反馈与远程屏幕截图实施计划 (Feedback & Screenshot Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 解决锁屏与光标丢失时的输入断联问题，实现剪贴板保底与友好状态回执，并支持微信远程发送“截图/看屏幕”指令回传高清屏幕截图。

**Architecture:** Core 层统一路由命令与格式化中文回执，WeChat 层实现 AES-128-ECB 加密与微信 CDN 上传，Windows 层提供 Win32 剪贴板保底和 GDI 屏幕捕获，UI 层在药丸悬浮窗呈现动画反馈并提供手动截图与回执配置开关。

**Tech Stack:** .NET 10, C#, Win32 API, GDI+, AES-128-ECB (PKCS7), WPF, xUnit.

**Spec:** `../specs/2026-11-08-feedback-and-screenshot-design.md`

## Global Constraints

- 绝不引入外部重量级 AI Agent 或大模型，专注轻量、确定性双向反馈。
- 保持 Direct Injection 2ms 极致输入性能，截图与图片上传全流程必须异步执行，绝不阻塞微信长轮询主循环。
- 图片上传严格符合腾讯微信 iLink Bot 协议：AES-128-ECB 密文上传，携带小写十六进制 MD5 与正确的 base_info、filekey、x-encrypted-param。
- 剪贴板写入必须使用 Win32 原生 API，避免 COM/OLE/STA 线程依赖与锁死，并具备忙碌重试机制。
- 全部 122 个现有单元测试（Connector.Tests: 86, Connector.Windows.Tests: 36）必须持续 100% 绿色通过。
- 所有对用户回执与代码注释均使用规范简体中文。

## Review Focus

1. 手机连发多条“截图”指令时，并发截图与上传不能导致内存泄漏或长轮询卡顿。
2. Windows 锁屏（Winlogon 桌面）或 UAC 隔离桌面时，屏幕截图或剪贴板写入不能抛出未处理异常崩溃，需安全处理。
3. 微信 CDN 上传网络闪退或超时时，错误必须被捕捉并作为 Retryable/提示处理，不能让主进程退出。
4. 普通文字在光标丢失时被写入剪贴板，随后光标恢复时，不应产生重复注入脏数据。
5. 微信回复上下文 `context_token` 超过 5 分钟有效期时，手动截图应有明确的失效提示。

---

### Task 1: 微信图片上传与 AES 加密协议 (`ILinkClient.SendImageAsync`)

**Files:**
- Modify: `src/Connector.WeChat/ILinkClient.cs`
- Create: `tests/Connector.Tests/ImageUploadProtocolTests.cs`

**Interfaces:**
- Consumes: `ReplyContext(AccountId, OwnerId, ContextToken, SourceMessageId, ReceivedAt)`
- Produces: `ILinkClient.SendImageAsync(ReplyContext context, string clientId, byte[] imageBytes, CancellationToken ct): Task`

- [ ] **Step 1: 编写失败测试**

在 `tests/Connector.Tests/ImageUploadProtocolTests.cs` 中编写测试：
- 模拟 `HttpMessageHandler`：
  - 拦截 `POST /ilink/bot/getuploadurl`，验证请求体包含 `rawsize`、`rawfilemd5`、`filesize`、`media_type: 1`，并返回 `{ upload_full_url: "https://novac2c.cdn.weixin.qq.com/test_upload" }`。
  - 拦截 CDN 上传请求，验证请求体为 AES-128-ECB 加密密文，返回 200 OK 附带标头 `x-encrypted-param: test_query_param`。
  - 拦截 `POST /ilink/bot/sendmessage`，验证请求体包含 `image_item`，其内有 `filekey`、`aeskey`、`media.encrypt_query_param`、`media.aes_key`。
- 测试图片字节为空、尺寸超限（> 10MB）或参数不合法时抛出 `ILinkException`。

- [ ] **Step 2: 运行测试验证失败**

运行：`powershell -ExecutionPolicy Bypass -File scripts/dev.ps1 test tests/Connector.Tests --no-restore --filter "FullyQualifiedName~ImageUploadProtocolTests"`
预期：编译失败或测试失败（找不到 `SendImageAsync`）。

- [ ] **Step 3: 在 `ILinkClient.cs` 中实现 `SendImageAsync`**

实现：
1. 校验 `_account`、`context`、`clientId`、`imageBytes`（非空且 `<= 10 * 1024 * 1024`）。
2. 计算 `rawsize = imageBytes.Length`，`rawfilemd5 = Convert.ToHexStringLower(MD5.HashData(imageBytes))`。
3. 生成 16 字节随机密钥 `aesKey`，计算小写十六进制 `aesKeyHex` 与 Base64 `aesKeyBase64`。
4. 使用 AES-128-ECB 与 PKCS7 加密 `imageBytes` 获得 `encryptedBytes`，计算 `filesize = encryptedBytes.Length`。
5. 生成随机 32 字符 `filekey`。
6. 调用 `RequestAsync` 发送 `POST /ilink/bot/getuploadurl`。提取 `upload_full_url`。
7. 发送 HTTP POST 请求至 `upload_full_url`，内容类型 `application/octet-stream`，内容为 `encryptedBytes`。
8. 从响应标头提取 `x-encrypted-param`。
9. 调用 `RequestAsync` 发送 `POST /ilink/bot/sendmessage`，装配 `image_item`。

- [ ] **Step 4: 运行测试验证通过**

运行：`powershell -ExecutionPolicy Bypass -File scripts/dev.ps1 test tests/Connector.Tests --no-restore --filter "FullyQualifiedName~ImageUploadProtocolTests"`
预期：全部通过。

- [ ] **Step 5: 提交代码**

```bash
git add src/Connector.WeChat/ILinkClient.cs tests/Connector.Tests/ImageUploadProtocolTests.cs
git commit -m "feat(wechat): add ILinkClient.SendImageAsync with AES-128-ECB encrypted CDN upload"
```

---

### Task 2: 屏幕捕获引擎与截图指令路由

**Files:**
- Create: `src/Connector.Windows/ScreenCapture.cs`
- Create: `src/Connector.Core/FeedbackCommandRouter.cs`
- Create: `tests/Connector.Windows.Tests/ScreenCaptureTests.cs`
- Create: `tests/Connector.Tests/FeedbackCommandRouterTests.cs`

**Interfaces:**
- Consumes: `System.Windows.Forms.SystemInformation` / `Screen.PrimaryScreen`
- Produces: `ScreenCapture.CapturePrimaryScreenToJpeg(int quality = 80): byte[]`
- Produces: `FeedbackCommandRouter.IsScreenshotCommand(string? text): bool`

- [ ] **Step 1: 编写失败测试**

- 在 `tests/Connector.Tests/FeedbackCommandRouterTests.cs` 中测试：
  - `"截图"`、`"截屏"`、`"看屏幕"`、`"屏幕"`、`"/截图"`、`"/截屏"`、`"/屏幕"`，包括前后空白，返回 `true`。
  - `"你好"`、`"/状态"`、`"截屏软件"`、`""` 返回 `false`。
- 在 `tests/Connector.Windows.Tests/ScreenCaptureTests.cs` 中测试：
  - `ScreenCapture.CapturePrimaryScreenToJpeg()` 返回非空字节数组，长度大于 100 字节，且前两个字节为 JPEG 幻数 `0xFF, 0xD8`。

- [ ] **Step 2: 运行测试验证失败**

运行：`powershell -ExecutionPolicy Bypass -File scripts/dev.ps1 test tests/Connector.Tests --no-restore --filter "FullyQualifiedName~FeedbackCommandRouterTests"`
预期：因缺少类而失败。

- [ ] **Step 3: 实现 `FeedbackCommandRouter.cs` 与 `ScreenCapture.cs`**

1. `FeedbackCommandRouter.cs`：
   - 静态方法 `IsScreenshotCommand(string? text)`，进行 Trim 与关键词哈希集匹配。
2. `ScreenCapture.cs`：
   - 使用 `System.Windows.Forms.SystemInformation.VirtualScreen` 或 `PrimaryScreen.Bounds` 确定屏幕矩形。
   - 创建 `Bitmap` 并使用 `Graphics.CopyFromScreen` 拷贝像素。
   - 使用 JPEG 编码器以指定质量保存到 `MemoryStream` 并返回字节数组。

- [ ] **Step 4: 运行测试验证通过**

运行：`powershell -ExecutionPolicy Bypass -File scripts/dev.ps1 test --no-restore --filter "FullyQualifiedName~FeedbackCommandRouterTests|FullyQualifiedName~ScreenCaptureTests"`
预期：全部通过。

- [ ] **Step 5: 提交代码**

```bash
git add src/Connector.Core/FeedbackCommandRouter.cs src/Connector.Windows/ScreenCapture.cs tests/Connector.Tests/FeedbackCommandRouterTests.cs tests/Connector.Windows.Tests/ScreenCaptureTests.cs
git commit -m "feat(windows): add ScreenCapture engine and FeedbackCommandRouter"
```

---

### Task 3: 锁屏与失焦剪贴板保底与友好状态回执

**Files:**
- Create: `src/Connector.Windows/ClipboardFallback.cs`
- Modify: `src/Connector.Windows/FocusedInputProbe.cs`
- Modify: `src/Connector.Core/ReceiptFormatter.cs`
- Create: `tests/Connector.Windows.Tests/ClipboardFallbackTests.cs`
- Modify: `tests/Connector.Tests/StatusReplyFormattingTests.cs`

**Interfaces:**
- Consumes: Win32 native clipboard APIs (`OpenClipboard`, `EmptyClipboard`, `SetClipboardData`, `CloseClipboard`)
- Produces: `ClipboardFallback.CopyText(string text): bool`
- Produces: `NativeProbeResult.Clipboard` = `"Copied"` 当写入成功
- Produces: `ReceiptFormatter.Input` 输出包含“已存入电脑剪贴板”提示

- [ ] **Step 1: 编写失败测试**

- 在 `ClipboardFallbackTests.cs` 中：调用 `ClipboardFallback.CopyText("测试剪贴板保底")`，验证返回 `true`，且系统剪贴板可读出该内容。
- 在 `StatusReplyFormattingTests.cs` 中：验证当 `outcome.State == EventState.Queued` 且 `outcome.Failure == InputFailure.NoInput` 时，`ReceiptFormatter.Input` 返回文本包含：“未执行输入：没有可用输入框。内容已存入电脑剪贴板，解锁或点击输入框后按 Ctrl+V 即可粘贴。”

- [ ] **Step 2: 运行测试验证失败**

运行：`powershell -ExecutionPolicy Bypass -File scripts/dev.ps1 test tests/Connector.Tests --no-restore --filter "FullyQualifiedName~StatusReplyFormattingTests"`
预期：断言失败（回执未包含剪贴板提示）。

- [ ] **Step 3: 实现 `ClipboardFallback` 并改造 `FocusedInputProbe` 与 `ReceiptFormatter`**

1. `ClipboardFallback.cs`：
   - 声明 Win32 `OpenClipboard(0)`、`EmptyClipboard()`、`SetClipboardData(13, hGlobal)`、`CloseClipboard()`。
   - 带有 5 次指数退避重试（间隔 20ms）。
   - 独立于 STA/COM 线程。
2. `FocusedInputProbe.cs`：
   - 在 `ProbeRefusal` 或无法定位输入框抛出拒绝时，调用 `ClipboardFallback.CopyText(text)`。
   - 若复制成功，`NativeProbeResult` 的 `Clipboard` 设为 `"Copied"`。
3. `ReceiptFormatter.cs`：
   - 更新 `Input` 方法的回退文字说明，友好告知用户文字已存入剪贴板。

- [ ] **Step 4: 运行测试验证通过**

运行：`powershell -ExecutionPolicy Bypass -File scripts/dev.ps1 test --no-restore --filter "FullyQualifiedName~ClipboardFallbackTests|FullyQualifiedName~StatusReplyFormattingTests"`
预期：全部通过。

- [ ] **Step 5: 提交代码**

```bash
git add src/Connector.Windows/ClipboardFallback.cs src/Connector.Windows/FocusedInputProbe.cs src/Connector.Core/ReceiptFormatter.cs tests/Connector.Windows.Tests/ClipboardFallbackTests.cs tests/Connector.Tests/StatusReplyFormattingTests.cs
git commit -m "feat(fallback): add clipboard safety net and clear status feedback for unfocused/locked state"
```

---

### Task 4: UI 集成、药丸悬浮窗反馈与设置开关

**Files:**
- Modify: `tools/Connector.Probe/ReceiptSession.cs`
- Modify: `tools/Connector.Probe/PillWindow.cs`
- Modify: `tools/Connector.Probe/SettingsWindow.cs`
- Create: `tests/Connector.Tests/FeedbackSettingsTests.cs`

**Interfaces:**
- Consumes: `FeedbackCommandRouter`, `ScreenCapture`, `ILinkClient.SendImageAsync`
- Produces: `ProbeSettings.FeedbackReplies`（布尔开关，持久化在 `.local/settings.json`）
- Produces: `PillWindow` 右键菜单：“📸 截取屏幕发微信”；UI 状态文本更新与折叠定时器。

- [ ] **Step 1: 编写失败测试**

- 在 `FeedbackSettingsTests.cs` 中测试设置模型增加 `FeedbackReplies`（默认 `true`），序列化与反序列化均保持正确。

- [ ] **Step 2: 运行测试验证失败**

运行：`powershell -ExecutionPolicy Bypass -File scripts/dev.ps1 test tests/Connector.Tests --no-restore --filter "FullyQualifiedName~FeedbackSettingsTests"`
预期：缺少 `FeedbackReplies` 属性编译失败。

- [ ] **Step 3: 实现设置开关与 UI 交互**

1. `SettingsWindow.cs` 与配置结构：
   - 增加 `FeedbackReplies` 开关项：“微信状态回执与反馈”。
2. `ReceiptSession.cs`：
   - 在 `WriteAsync` 中集成 `FeedbackCommandRouter.IsScreenshotCommand(item.Text)`：
     - 若为截图指令，调用 `ScreenCapture.CapturePrimaryScreenToJpeg()` 获取图片字节。
     - 生成唯一 `clientId`，调用 `_client.SendImageAsync(replyContext, clientId, imageBytes, ct)`。
     - 触发报告 `_report("📸 已截屏并回传微信。")`，返回 `InputOutcome(EventState.Verified, Application: "屏幕截图")`。
     - 当发生光标丢失时，若 `FeedbackReplies` 为 `true`，正常发送回执通知；若为 `false`，静默不向微信发送未对准输入框的回执。
3. `PillWindow.cs`：
   - 在右键菜单中加入“📸 截取屏幕发微信”选项。点击后异步截屏并通过最新 `LastContext` 发送。
   - 接收到截图与剪贴板保底事件时，设置 `_statusText.Text`，展开药丸并在 3 秒后平滑缩回迷你光珠。

- [ ] **Step 4: 运行测试验证通过**

运行：`powershell -ExecutionPolicy Bypass -File scripts/dev.ps1 test --no-restore`
预期：所有测试通过。

- [ ] **Step 5: 提交代码**

```bash
git add tools/Connector.Probe/ReceiptSession.cs tools/Connector.Probe/PillWindow.cs tools/Connector.Probe/SettingsWindow.cs tests/Connector.Tests/FeedbackSettingsTests.cs
git commit -m "feat(ui): add feedback replies toggle, manual screenshot context menu, and pill animations"
```

---

### Task 5: 全量回归测试、自包含构建发布与文档更新

**Files:**
- Modify: `README.md`
- Output: `artifacts/p0-current/`

- [ ] **Step 1: 运行全量测试**

运行：`powershell -ExecutionPolicy Bypass -File scripts/dev.ps1 test`
预期：所有测试项目 100% 通过（总计应超过 125 个测试），0 个失败。

- [ ] **Step 2: 自包含打包发布**

运行：`powershell -ExecutionPolicy Bypass -File scripts/dev.ps1 publish`
预期：成功生成自包含无依赖发布包到 `artifacts/p0-current/`。

- [ ] **Step 3: 更新 README.md**

在 `README.md` 中增加关于“远程截图看屏幕（发送‘截图’或‘看屏幕’）”与“光标丢失/锁屏时自动剪贴板保底与回执反馈”的功能使用说明。

- [ ] **Step 4: 提交并推送到远程仓库**

```bash
git add README.md artifacts/p0-current/
git commit -m "docs: document remote screenshot and clipboard fallback feedback features"
git push origin feat/p0-connector
```
