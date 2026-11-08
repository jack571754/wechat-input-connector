using Connector.Core;
using ProbeState = Connector.WeChat.ConnectionState;
using Connector.WeChat;
using Connector.Windows;
using QRCoder;
using System.Text.Json;
using System.IO;
using System.Net.Http;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Forms = System.Windows.Forms;

namespace Connector.Probe;

internal static class Program
{

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--input-helper")) { FocusedInputProbe.RunHelper(); return; }
        if (!ApplicationInstance.Run("Local\\WeChatInputConnector.P0", () => RunApplication(args)))
            Forms.MessageBox.Show("微信输入连接器已在运行，请查看桌面顶部或系统托盘。", "微信连接器");
    }

    private static void RunApplication(string[] args)
    {
        string? directory = null;
        try
        {
            directory = ResolveDataDirectory(args);
            DataDirectory.EnsureReady(directory);
            Directory.CreateDirectory(directory);
            AppendLog(directory, "trace.log", "启动");
            if (args.Contains("--insert-probe"))
            {
                var result = FocusedInputProbe.InsertAsync("【P0 本地输入验证】中文与 emoji 🙂\r\n第二行，不自动发送。",
                    Path.Combine(directory, "input-recovery.dpapi"), default).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(directory, "native-input-result.json"), JsonSerializer.Serialize(result));
                return;
            }
            using var tray = new TrayManager();
            var app = new System.Windows.Application();
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary());
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
            app.DispatcherUnhandledException += (s, e) =>
            {
                AppendLog(directory, "crash.log", e.Exception.ToString());
                e.Handled = false;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                AppendLog(directory, "crash.log", e.ExceptionObject?.ToString() ?? "Unknown");
            };
            app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            var window = new PillWindow(directory, tray);
            if (args.Contains("--show-history")) window.Loaded += (_, _) => window.OpenDesktop();
            window.Show();
            app.Run(window);
        }
        catch (Exception ex)
        {
            var targetDir = directory ?? AppContext.BaseDirectory;
            AppendLog(targetDir, "crash.log", ex.ToString());
        }
    }

    private static void AppendLog(string directory, string name, string message)
    {
        try
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Move(path, path + ".old", true);
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message}\n");
        }
        catch (Exception) { /* 日志故障不能覆盖原始故障或触发递归崩溃。 */ }
    }

    private static string ResolveDataDirectory(string[] args)
    {
        var index = Array.IndexOf(args, "--data-dir");
        if (index >= 0 && index + 1 < args.Length)
        {
            return Path.GetFullPath(args[index + 1]);
        }

        var target = DataDirectory.DefaultPath;
        DataDirectory.EnsurePrivate(target);
        var migration = Array.IndexOf(args, "--migrate-from");
        if (migration >= 0 && migration + 1 < args.Length)
        {
            var source = Path.GetFullPath(args[migration + 1]);
            var previous = ProtectedFile.Load<ProbeState>(Path.Combine(source, "probe-state.dpapi"));
            if (string.IsNullOrWhiteSpace(previous.Account.BotId) || string.IsNullOrWhiteSpace(previous.Account.OwnerId))
                throw new InvalidDataException("迁移数据缺少绑定身份。");
            DataDirectory.Migrate(source, target);
        }
        return target;
    }
}
