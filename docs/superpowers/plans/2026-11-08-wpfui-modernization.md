# WPF-UI Fluent Design Modernization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Upgrade the UI of WeChat Input Connector using `WPF-UI` (Fluent Design 2) to deliver a native Windows 11 aesthetic with vector Fluent System Icons, CardControl layouts, ToggleSwitch controls, and polished navigation.

**Architecture:** Integrate the `WPF-UI` (v4.3.0) library into `Connector.Probe`, initialize its themes and merged resource dictionaries at startup, refactor `DesktopWindow.cs` to leverage `CardControl`, `ToggleSwitch`, `SymbolIcon`, and `Button` with Fluent appearances, and upgrade `PillWindow.cs` to replace emoji buttons with crisp vector symbols while preserving non-activating focus behavior.

**Tech Stack:** .NET 10 (`net10.0-windows`), WPF, `WPF-UI` (4.3.0 by Lepo), `QRCoder` (1.6.0), Win32 P/Invoke (`user32.dll`), Windows DPAPI.

**Spec:** Direct user request to adopt Option 1 (WPF-UI) for comprehensive UI modernization.

## Global Constraints
- Target Framework: `net10.0-windows`
- Central Package Management: Must maintain `Directory.Packages.props`
- Zero regression on existing 186 unit tests (`Connector.Tests`, `Connector.Windows.Tests`)
- Keystroke injection latency $\le 2\text{ms}$
- Floating pill must retain non-focus-stealing property (`WM_MOUSEACTIVATE` hook returning `MA_NOACTIVATE`)
- QR Code must maintain 240x240 DIPs, `NearestNeighbor` scaling, `Aliased` edge mode, and pure white quiet zone

## Review Focus
1. `DesktopWindow` must handle both connected and disconnected states cleanly with WPF-UI cards.
2. `ToggleSwitch` controls must correctly bind and trigger `input`, `enter`, and `topmost` delegates without recursive events.
3. Vector icons (`SymbolIcon`) must replace raw emoji buttons (`📋`, `📸`, `🔄`, `⚙️`, `🕒`).
4. High-DPI and dark/light contrast must remain legible and accessible.
5. All 186 unit tests must continue passing.

---

### Task 1: Package Scaffolding & Theme Initialization

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `tools/Connector.Probe/Connector.Probe.csproj`
- Modify: `tools/Connector.Probe/Program.cs`

**Steps:**
- [ ] Ensure `<PackageVersion Include="WPF-UI" Version="4.3.0" />` is in `Directory.Packages.props`.
- [ ] Ensure `<PackageReference Include="WPF-UI" />` is in `tools/Connector.Probe/Connector.Probe.csproj`.
- [ ] In `Program.cs`, initialize WPF-UI themes on the `System.Windows.Application` instance:
  ```csharp
  app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
  {
      Source = new Uri("pack://application:,,,/Wpf.Ui;component/Resources/Theme.xaml", UriKind.Absolute)
  });
  Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
  ```
- [ ] Run `powershell -ExecutionPolicy Bypass -File scripts/dev.ps1 build tools/Connector.Probe/Connector.Probe.csproj` to verify build succeeds.
- [ ] Commit: `build: integrate WPF-UI library and initialize theme resources`

---

### Task 2: DesktopWindow Fluent Modernization

**Files:**
- Modify: `tools/Connector.Probe/DesktopWindow.cs`

**Steps:**
- [ ] Update imports to include `Wpf.Ui.Controls` and `Wpf.Ui.Appearance`.
- [ ] Replace custom `ModernToggle` in `DesktopWindow` with `Wpf.Ui.Controls.ToggleSwitch`:
  - `_toggleInput = new Wpf.Ui.Controls.ToggleSwitch { Content = "开启" };`
  - `_toggleEnter = new Wpf.Ui.Controls.ToggleSwitch { Content = "开启" };`
  - `_toggleTopmost = new Wpf.Ui.Controls.ToggleSwitch { Content = "开启" };`
- [ ] Refactor Preferences Section using `Wpf.Ui.Controls.CardControl`:
  - Card 1: 自动写入当前输入框 (`SymbolRegular.Keyboard24`) + ToggleSwitch
  - Card 2: 输入完成后追加 Enter (`SymbolRegular.ArrowEnter24`) + ToggleSwitch
  - Card 3: 悬浮胶囊置顶显示 (`SymbolRegular.Pin24`) + ToggleSwitch
- [ ] Refactor WeChat Connection Card:
  - Header with `SymbolRegular.QrCode24`
  - Status indicator using `Wpf.Ui.Controls.InfoBadge` (Success for connected, Attention for waiting)
  - Scannable 240x240 crisp QR code container
  - Action buttons using `Wpf.Ui.Controls.Button`:
    - Reconnect Button with `SymbolRegular.ArrowClockwise24` and `Appearance = ControlAppearance.Secondary`
    - Screenshot Button with `SymbolRegular.Camera24` and `Appearance = ControlAppearance.Primary`
- [ ] Refactor Navigation Switcher:
  - Header bar segmented pill with `SymbolRegular.History24` ("消息历史") and `SymbolRegular.Settings24` ("连接与设置")
- [ ] Refactor History Detail Viewer:
  - Copy Button using `Wpf.Ui.Controls.Button` with `SymbolRegular.Copy24`
  - Clean card item template with `InfoBadge` status tags
- [ ] Verify build and tests.
- [ ] Commit: `feat(ui): modernize DesktopWindow with WPF-UI Fluent cards and vector icons`

---

### Task 3: PillWindow Fluent Visual Polish

**Files:**
- Modify: `tools/Connector.Probe/PillWindow.cs`

**Steps:**
- [ ] Update `PillWindow.cs` context menu and buttons to use vector `SymbolIcon` where suitable:
  - Replace emoji in menus with clean icon and label pairs.
  - Update `_verifySubmitBtn` to `Wpf.Ui.Controls.Button` with `Appearance = ControlAppearance.Primary`.
- [ ] Ensure non-activating window click behavior (`WM_MOUSEACTIVATE`) is strictly preserved.
- [ ] Verify build and tests.
- [ ] Commit: `feat(ui): polish PillWindow controls with Fluent styling`

---

### Task 4: Full Test Suite, Packaging & Local Deployment

**Files:**
- Verify: `scripts/dev.ps1 test`
- Build & Publish: `scripts/dev.ps1 publish tools/Connector.Probe/Connector.Probe.csproj -c Release -r win-x64 --self-contained true -o artifacts/desktop-hardened`
- Deploy: `scripts/install-local.ps1`

**Steps:**
- [ ] Run full test suite: `powershell -ExecutionPolicy Bypass -File scripts/dev.ps1 test` and confirm all 186 tests pass.
- [ ] Stop any running `Connector.Probe` process.
- [ ] Publish self-contained executable to `artifacts/desktop-hardened`.
- [ ] Run `scripts/install-local.ps1` to deploy and launch the new version.
- [ ] Commit all changes and push to `origin/feat/p0-connector` and `origin/main`.
