using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LolLocaleTool
{
    internal static class GuardProgram
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--guard") return ProductGuard.Run();
            if (args.Length == 1 && args[0] == "--stop")
            {
                try { ProductGuard.Stop(); return 0; }
                catch (Exception error) { ProductGuard.Log(error.Message); return 1; }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ProductGuardForm());
            return 0;
        }
    }

    internal static class ProductGuard
    {
        private const string MutexName = @"Local\LoLProductLocaleGuardV4";
        private const string StopEventName = @"Local\LoLProductLocaleGuardV4Stop";
        public static string StatusPath { get { return Path.Combine(Application.StartupPath, "product-guard-status.txt"); } }
        public static string LogPath { get { return Path.Combine(Application.StartupPath, "product-guard.log"); } }

        public static void Log(string message)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine, new UTF8Encoding(false)); }
            catch { }
        }

        private static void Status(string state, string message)
        {
            string text = state + "\n" + Process.GetCurrentProcess().Id + "\n" + message.Replace("\r", " ").Replace("\n", " ") + "\n" + DateTime.UtcNow.ToString("o") + "\n" + Path.GetFileNameWithoutExtension(Application.ExecutablePath) + "\n";
            string temporary = StatusPath + ".tmp";
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            if (File.Exists(StatusPath)) File.Replace(temporary, StatusPath, null);
            else File.Move(temporary, StatusPath);
        }

        public static string[] ReadStatus()
        {
            try
            {
                string[] lines = File.ReadAllLines(StatusPath, Encoding.UTF8);
                if (lines.Length < 3) return new string[] { "stopped", "0", "未开启保护" };
                int pid;
                if (IsRunningState(lines[0]) && int.TryParse(lines[1], out pid))
                {
                    try
                    {
                        using (Process process = Process.GetProcessById(pid))
                            if (process.HasExited || process.ProcessName != (lines.Length >= 5 ? lines[4] : Path.GetFileNameWithoutExtension(Application.ExecutablePath)))
                                return new string[] { "stopped", "0", "保护进程已退出" };
                    }
                    catch { return new string[] { "stopped", "0", "保护进程已退出" }; }
                }
                return lines;
            }
            catch { return new string[] { "stopped", "0", "未开启保护" }; }
        }

        public static int Run()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, MutexName, out created))
            {
                if (!created) return 0;
                try
                {
                    using (EventWaitHandle stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName))
                    {
                        stop.Reset();
                        Status("starting", "正在结束 Riot、改为 zh_CN 并开启保护…");
                        Log("仅处理：" + LocaleEngine.DefaultPath);
                        LocaleEngine.StopRiot(Log);
                        if (stop.WaitOne(0)) { Status("stopped", "开启操作已取消"); return 0; }
                        try
                        {
                            using (DefensiveProtection protection = new DefensiveProtection(LocaleEngine.DefaultPath, Path.Combine(Application.StartupPath, "product-recovery.snapshot"), Log))
                            {
                                protection.CheckAndRepair();
                                Status("protected", "每秒检测：两个语言值为 zh_CN，目标路径与文件锁一致");
                                Log("已开启单文件保护和每秒检测；恢复快照保存在工具目录。");
                                int failures = 0;
                                string previousError = null;
                                while (!stop.WaitOne(failures == 0 ? 1000 : Math.Min(10000, failures * 1000)))
                                {
                                    try
                                    {
                                        bool repaired = protection.CheckAndRepair();
                                        failures = 0; previousError = null;
                                        Status("protected", "每秒检测正常：zh_CN" + (repaired ? "；刚完成自动修复" : "") + "；" + DateTime.Now.ToString("HH:mm:ss"));
                                    }
                                    catch (Exception error)
                                    {
                                        protection.ReleaseLock();
                                        failures = Math.Min(failures + 1, 10);
                                        Status("recovering", "检测异常，正在重试修复：" + error.Message);
                                        if (previousError != error.Message) { Log("检测/修复异常：" + error.Message); previousError = error.Message; }
                                    }
                                }
                            }
                        }
                        finally { if (File.Exists(LocaleEngine.DefaultPath)) LocaleEngine.AllowUpdates(LocaleEngine.DefaultPath, Log); }
                        Status("stopped", "保护已停止，只读已取消，当前语言值保持不变");
                        Log("持续保护已停止。");
                    }
                    return 0;
                }
                catch (Exception error)
                {
                    Log("失败：" + error.Message);
                    try { Status("failed", error.Message); } catch { }
                    return 1;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        public static void Start(bool elevated)
        {
            string[] status = ReadStatus();
            if (IsRunningState(status[0])) return;
            ProcessStartInfo info = new ProcessStartInfo(Application.ExecutablePath, "--guard");
            info.WorkingDirectory = Application.StartupPath;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            if (elevated) { info.UseShellExecute = true; info.Verb = "runas"; }
            else { info.UseShellExecute = false; info.CreateNoWindow = true; }
            using (Process process = Process.Start(info)) { }
        }

        public static void Stop()
        {
            EventWaitHandle stop;
            if (EventWaitHandle.TryOpenExisting(StopEventName, out stop))
            {
                using (stop) stop.Set();
                for (int i = 0; i < 100; i++)
                {
                    if (!IsRunningState(ReadStatus()[0])) return;
                    Thread.Sleep(100);
                }
                throw new IOException("停止保护超时，请稍后检查或结束保护进程。");
            }
            if (File.Exists(LocaleEngine.DefaultPath)) LocaleEngine.AllowUpdates(LocaleEngine.DefaultPath, Log);
        }

        private static bool IsRunningState(string state) { return state == "protected" || state == "starting" || state == "recovering"; }
    }

    public sealed class ProductGuardForm : Form
    {
        private readonly Label state = new Label();
        private readonly Label values = new Label();
        private readonly Label detail = new Label();
        private readonly Button enable = new Button();
        private readonly Button disable = new Button();
        private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
        private bool busy;
        private string actionError;

        public ProductGuardForm()
        {
            Text = "LoL 简体中文 · 持续检测保护";
            Font = new Font("Microsoft YaHei UI", 10);
            ClientSize = new Size(780, 465);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Controls.Add(new Label { Text = "改成简体中文，并持续保护", Font = new Font(Font.FontFamily, 19, FontStyle.Bold), AutoSize = true, Location = new Point(24, 22) });
            Controls.Add(new Label { Text = "只保护一个文件；每秒检测，文件消失或语言被改回时自动修复。", AutoSize = true, Location = new Point(26, 70) });
            TextBox path = new TextBox { ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.FixedSingle, Text = LocaleEngine.DefaultPath, Location = new Point(26, 106), Size = new Size(728, 52), BackColor = Color.White };
            Controls.Add(path);
            state.SetBounds(26, 181, 728, 30); state.Font = new Font(Font.FontFamily, 13, FontStyle.Bold); Controls.Add(state);
            values.SetBounds(26, 224, 728, 30); Controls.Add(values);
            enable.Text = "改为简体并保护"; enable.SetBounds(26, 274, 354, 49);
            enable.Click += delegate
            {
                try
                {
                    actionError = null;
                    string[] previous = ProductGuard.ReadStatus();
                    ProductGuard.Start(previous[0] == "failed" && PermissionError(previous[2]));
                    state.Text = "正在开启保护…";
                }
                catch (Exception error) { actionError = error.Message; }
            };
            Controls.Add(enable);
            disable.Text = "停止保护"; disable.SetBounds(398, 274, 356, 49);
            disable.Click += async delegate
            {
                actionError = null;
                busy = true; enable.Enabled = disable.Enabled = false;
                try { await Task.Run(new Action(ProductGuard.Stop)); }
                catch (UnauthorizedAccessException)
                {
                    try
                    {
                        ProcessStartInfo info = new ProcessStartInfo(Application.ExecutablePath, "--stop") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                        using (Process process = Process.Start(info)) { }
                    }
                    catch (Exception error) { actionError = error.Message; }
                }
                catch (Exception error) { actionError = error.Message; }
                finally { busy = false; RefreshState(); }
            };
            Controls.Add(disable);
            detail.SetBounds(26, 342, 728, 45); Controls.Add(detail);
            Controls.Add(new Label { Text = "保护在后台运行，关闭窗口仍保持；重启电脑后需重新开启。\nRiot 若提示文件占用或更新失败，请点“停止保护”。", Location = new Point(26, 396), Size = new Size(728, 48), ForeColor = Color.DimGray });
            refreshTimer.Interval = 1000; refreshTimer.Tick += delegate { RefreshState(); }; refreshTimer.Start();
            Shown += delegate { RefreshState(); };
            FormClosed += delegate { refreshTimer.Stop(); refreshTimer.Dispose(); };
        }

        private void RefreshState()
        {
            if (busy) return;
            string[] status = ProductGuard.ReadStatus();
            bool protectedNow = status[0] == "protected", starting = status[0] == "starting", recovering = status[0] == "recovering";
            state.Text = protectedNow ? "持续检测保护：已开启" : recovering ? "检测到异常：正在自动修复…" : starting ? "持续保护：正在开启…" : status[0] == "failed" ? "持续保护：开启失败" : "持续保护：未开启";
            state.ForeColor = protectedNow ? Color.DarkGreen : status[0] == "failed" ? Color.DarkRed : Color.DimGray;
            bool permissionFailure = status[0] == "failed" && PermissionError(status[2]);
            detail.Text = actionError ?? (permissionFailure ? "权限不足。点击“授权并重试”，通过 Windows 管理员授权后继续。" : status[2]);
            enable.Text = protectedNow ? "保护已开启" : permissionFailure ? "授权并重试" : "改为简体并保护";
            enable.Enabled = !protectedNow && !starting && !recovering;
            disable.Enabled = protectedNow || starting || recovering;
            try
            {
                string inspected = LocaleEngine.Inspect(LocaleEngine.DefaultPath);
                var fields = new System.Collections.Generic.List<string>();
                foreach (string line in inspected.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("default_locale = ")) fields.Add(line);
                    else if (line.StartsWith("locale = ")) fields.Add("settings." + line);
                }
                values.Text = string.Join("   |   ", fields.ToArray());
            }
            catch (Exception error) { values.Text = "读取语言失败：" + error.Message; }
        }

        private static bool PermissionError(string message)
        {
            return message.IndexOf("拒绝访问", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("Access is denied", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
