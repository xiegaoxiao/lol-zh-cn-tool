using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LolLocaleTool
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length != 0)
            {
                try
                {
                    string path = LocaleEngine.DefaultPath;
                    string install = null;
                    bool sync = false;
                    bool riot = false;
                    bool readOnly = false;
                    for (int i = 1; i < args.Length; i++)
                    {
                        if (args[i] == "--path" && i + 1 < args.Length) path = args[++i];
                        else if (args[i] == "--install" && i + 1 < args.Length) install = args[++i];
                        else if (args[i] == "--sync") sync = true;
                        else if (args[i] == "--riot") riot = true;
                        else if (args[i] == "--readonly") readOnly = true;
                        else throw new ArgumentException("未知参数：" + args[i]);
                    }
                    if (args[0] == "--status") Console.WriteLine(LocaleEngine.Inspect(path));
                    else if (args[0] == "--allow-updates") LocaleEngine.AllowUpdates(path, Console.WriteLine);
                    else if (args[0] == "--apply")
                    {
                        if (sync && install == null) install = LocaleEngine.FindInstallPath(path);
                        LocaleEngine.StopRiot(Console.WriteLine);
                        LocaleEngine.ApplyAll(path, install, sync, riot ? LocaleEngine.RiotSettingsPath : null, readOnly, Console.WriteLine);
                    }
                    else if (args[0] == "--restore")
                    {
                        if (sync && install == null) install = LocaleEngine.FindInstallPath(path);
                        LocaleEngine.StopRiot(Console.WriteLine);
                        if (riot && File.Exists(LocaleEngine.BackupPath(LocaleEngine.RiotSettingsPath))) LocaleEngine.Restore(LocaleEngine.RiotSettingsPath, Console.WriteLine);
                        // Restore only files with backups; unchanged game files need no restore.
                        if (sync)
                        {
                            string[] gamePaths = { Path.Combine(install, "Config", "LeagueClientSettings.yaml"), Path.Combine(install, "system.yaml") };
                            foreach (string gamePath in gamePaths)
                                if (File.Exists(LocaleEngine.BackupPath(gamePath))) LocaleEngine.Restore(gamePath, Console.WriteLine);
                        }
                        LocaleEngine.Restore(path, Console.WriteLine);
                    }
                    else throw new ArgumentException("参数：--status / --apply / --restore / --allow-updates，支持 --path 路径、--sync、--riot、--readonly、--install 游戏目录。");
                    return 0;
                }
                catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }

    public sealed class MainForm : Form
    {
        private readonly TextBox product = new TextBox();
        private readonly TextBox install = new TextBox();
        private readonly TextBox log = new TextBox();
        private readonly CheckBox sync = new CheckBox();
        private readonly CheckBox readOnly = new CheckBox();
        private readonly CheckBox riot = new CheckBox();
        private readonly Label state = new Label();
        private readonly Label productSummary = new Label();
        private readonly Button apply = new Button();
        private readonly Button restore = new Button();
        private readonly Button refresh = new Button();
        private readonly Button unlock = new Button();
        private bool busy;

        public MainForm()
        {
            Text = "LoL 简体中文配置工具";
            Font = new Font("Microsoft YaHei UI", 10);
            ClientSize = new Size(850, 681);
            MinimumSize = Size;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            Label title = new Label { Text = "LoL 简体中文配置工具", AutoSize = true, Location = new Point(24, 18), Font = new Font(Font.FontFamily, 18, FontStyle.Bold) };
            Controls.Add(title);
            AddLabel("先结束 Riot 客户端 → 备份配置 → 修改为 zh_CN → 验证语言字段", 24, 62);
            AddLabel("Riot 产品配置", 24, 103);
            product.SetBounds(24, 130, 720, 30); product.Text = LocaleEngine.DefaultPath;
            product.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; Controls.Add(product);
            product.TextChanged += delegate { productSummary.Text = "产品配置：路径已改变，请点击检查配置。"; productSummary.ForeColor = Color.DimGray; };
            Button browse = new Button { Text = "选择…", Location = new Point(755, 128), Size = new Size(70, 32), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            browse.Click += delegate
            {
                using (OpenFileDialog dialog = new OpenFileDialog { Filter = "YAML 配置|*.yaml;*.yml", FileName = "league_of_legends.live.product_settings.yaml" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) { product.Text = dialog.FileName; DetectInstall(); }
            }; Controls.Add(browse);
            AddLabel("游戏安装目录", 24, 173);
            install.SetBounds(24, 200, 720, 30); install.Anchor = product.Anchor; Controls.Add(install);
            Button folder = new Button { Text = "选择…", Location = new Point(755, 198), Size = new Size(70, 32), Anchor = browse.Anchor };
            folder.Click += delegate { using (FolderBrowserDialog dialog = new FolderBrowserDialog()) if (dialog.ShowDialog(this) == DialogResult.OK) install.Text = dialog.SelectedPath; }; Controls.Add(folder);
            sync.Text = "同时同步游戏目录的两处语言字段（保留这些文件原有属性）"; sync.Checked = true;
            sync.SetBounds(24, 246, 780, 28); Controls.Add(sync);
            riot.Text = "同时修改 Riot 启动器的两处语言字段（是否生效取决于启动器语言资源）"; riot.Checked = true;
            riot.SetBounds(24, 280, 790, 28); Controls.Add(riot);
            readOnly.Text = "额外设置产品配置只读（实验选项，可能影响 Riot 启动或更新）";
            readOnly.SetBounds(24, 314, 810, 28); Controls.Add(readOnly);
            AddLabel("已停用持续文件锁：Riot 更新需要替换这些配置，锁定会导致错误 32。", 24, 348);
            apply.Text = "应用简体中文"; apply.SetBounds(24, 384, 155, 40); apply.Click += async delegate { await RunOperation(false); }; Controls.Add(apply);
            restore.Text = "恢复原始配置"; restore.SetBounds(191, 384, 155, 40); restore.Click += async delegate { await RunOperation(true); }; Controls.Add(restore);
            refresh.Text = "检查配置"; refresh.SetBounds(358, 384, 120, 40); refresh.Click += delegate { ShowStatus(); }; Controls.Add(refresh);
            unlock.Text = "允许启动/更新"; unlock.SetBounds(490, 384, 145, 40);
            unlock.Click += delegate
            {
                try { LocaleEngine.AllowUpdates(product.Text, Append); readOnly.Checked = false; ShowStatus(); }
                catch (Exception error) { Append("取消只读失败：" + error.Message); }
            }; Controls.Add(unlock);
            productSummary.SetBounds(24, 438, 801, 28);
            productSummary.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            productSummary.Text = "产品配置：尚未检查";
            Controls.Add(productSummary);
            state.SetBounds(24, 474, 790, 28); Controls.Add(state); UpdateGuardState();
            log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical; log.SetBounds(24, 507, 801, 149);
            log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right; log.BackColor = Color.White; Controls.Add(log);
            Shown += delegate { DetectInstall(); ShowStatus(); };
            FormClosing += delegate(object sender, FormClosingEventArgs args) { if (busy) args.Cancel = true; };
        }

        private void AddLabel(string text, int x, int y) { Controls.Add(new Label { Text = text, AutoSize = true, Location = new Point(x, y) }); }
        private void DetectInstall() { try { install.Text = LocaleEngine.FindInstallPath(product.Text); } catch (Exception error) { Append(error.Message); } }
        private void Append(string message)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Append), message); return; }
            log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
        }
        private void ShowStatus()
        {
            try
            {
                string productStatus = LocaleEngine.Inspect(product.Text);
                var fields = new System.Collections.Generic.List<string>();
                foreach (string line in productStatus.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("default_locale = ") || line.StartsWith("只读：")) fields.Add(line);
                    else if (line.StartsWith("locale = ")) fields.Add("settings." + line);
                }
                productSummary.Text = "产品配置：" + string.Join("  |  ", fields.ToArray());
                productSummary.ForeColor = Color.Black;
                Append("产品配置（" + product.Text + "）：\n" + productStatus);
            }
            catch (Exception error)
            {
                productSummary.Text = "产品配置：检查失败，" + error.Message;
                productSummary.ForeColor = Color.DarkRed;
                Append(productSummary.Text);
            }
            try
            {
                if (riot.Checked) Append("Riot 启动器配置：\n" + LocaleEngine.Inspect(LocaleEngine.RiotSettingsPath));
                if (sync.Checked && Directory.Exists(install.Text))
                {
                    Append("LeagueClientSettings.yaml：\n" + LocaleEngine.Inspect(Path.Combine(install.Text, "Config", "LeagueClientSettings.yaml")));
                    Append("system.yaml：\n" + LocaleEngine.Inspect(Path.Combine(install.Text, "system.yaml")));
                }
            }
            catch (Exception error) { Append("检查失败：" + error.Message); }
        }
        private void UpdateGuardState()
        {
            state.Text = "运行模式：不持有持续文件锁。开始更新前请关闭旧版工具。";
            state.ForeColor = Color.DimGray;
            unlock.Enabled = !busy;
        }
        private async Task RunOperation(bool restoring)
        {
            if (busy) return;
            string path = product.Text, gameDirectory = install.Text;
            bool syncGame = sync.Checked, syncRiot = riot.Checked, lockProduct = readOnly.Checked;
            busy = true;
            apply.Enabled = restore.Enabled = refresh.Enabled = unlock.Enabled = product.Enabled = install.Enabled = sync.Enabled = readOnly.Enabled = riot.Enabled = false;
            try
            {
                await Task.Run(delegate
                {
                    LocaleEngine.StopRiot(Append);
                    if (restoring)
                    {
                        if (syncRiot && File.Exists(LocaleEngine.BackupPath(LocaleEngine.RiotSettingsPath))) LocaleEngine.Restore(LocaleEngine.RiotSettingsPath, Append);
                        if (syncGame)
                        {
                            string[] gamePaths = { Path.Combine(gameDirectory, "Config", "LeagueClientSettings.yaml"), Path.Combine(gameDirectory, "system.yaml") };
                            foreach (string gamePath in gamePaths)
                                if (File.Exists(LocaleEngine.BackupPath(gamePath))) LocaleEngine.Restore(gamePath, Append);
                        }
                        LocaleEngine.Restore(path, Append);
                    }
                    else LocaleEngine.ApplyAll(path, gameDirectory, syncGame, syncRiot ? LocaleEngine.RiotSettingsPath : null, lockProduct, Append);
                });
                Append(restoring ? "恢复完成。" : "修改完成。请启动游戏确认实际语言效果。");
                ShowStatus();
            }
            catch (Exception error) { Append("操作失败：" + error.Message); MessageBox.Show(this, error.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally
            {
                busy = false;
                apply.Enabled = restore.Enabled = refresh.Enabled = product.Enabled = install.Enabled = sync.Enabled = readOnly.Enabled = riot.Enabled = true;
                UpdateGuardState();
            }
        }
    }
}
