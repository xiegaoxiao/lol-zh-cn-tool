using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LolLocaleTool
{
    public sealed class LocaleEngine
    {
        public const string DefaultPath = @"C:\ProgramData\Riot Games\Metadata\league_of_legends.live\league_of_legends.live.product_settings.yaml";
        public static string RiotSettingsPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Riot Games\Riot Client\Config\RiotClientSettings.yaml"); } }
        private static readonly Regex LocaleLine = new Regex(@"^([ \t]*(default_locale|locale)[ \t]*:[ \t]*)(""[^""\r\n]*""|'[^'\r\n]*'|[A-Za-z0-9_-]+)([ \t]*(?:#[^\r\n]*)?)(\r?)$", RegexOptions.Multiline);

        public static string BackupPath(string path) { return path + ".lol-zh-cn.bak"; }
        public static string MetadataPath(string path) { return BackupPath(path) + ".meta"; }

        private sealed class Document
        {
            public Encoding Encoding;
            public byte[] Preamble;
            public string Text;
            public byte[] Encode(string text)
            {
                byte[] body = Encoding.GetBytes(text);
                byte[] result = new byte[Preamble.Length + body.Length];
                Buffer.BlockCopy(Preamble, 0, result, 0, Preamble.Length);
                Buffer.BlockCopy(body, 0, result, Preamble.Length, body.Length);
                return result;
            }
        }

        private static Document Decode(byte[] data)
        {
            Encoding encoding = new UTF8Encoding(false, true);
            int skip = 0;
            if (data.Length >= 4 && ((data[0] == 0xff && data[1] == 0xfe && data[2] == 0 && data[3] == 0) ||
                (data[0] == 0 && data[1] == 0 && data[2] == 0xfe && data[3] == 0xff)))
                throw new InvalidDataException("不支持 UTF-32 配置，请使用 UTF-8 或 UTF-16。");
            if (data.Length >= 3 && data[0] == 0xef && data[1] == 0xbb && data[2] == 0xbf) skip = 3;
            else if (data.Length >= 2 && data[0] == 0xff && data[1] == 0xfe) { encoding = new UnicodeEncoding(false, false, true); skip = 2; }
            else if (data.Length >= 2 && data[0] == 0xfe && data[1] == 0xff) { encoding = new UnicodeEncoding(true, false, true); skip = 2; }
            byte[] preamble = new byte[skip];
            Buffer.BlockCopy(data, 0, preamble, 0, skip);
            return new Document { Encoding = encoding, Preamble = preamble, Text = encoding.GetString(data, skip, data.Length - skip) };
        }

        public static byte[] Patch(byte[] original) { return Patch(original, "product"); }

        public static byte[] Patch(byte[] original, string profile)
        {
            Document document = Decode(original);
            int defaults = 0, locales = 0;
            foreach (Match match in LocaleLine.Matches(document.Text))
                if (match.Groups[2].Value == "default_locale") defaults++; else locales++;
            if ((profile == "product" && (defaults != 1 || locales != 1)) ||
                (profile == "client" && locales != 1) || (profile == "system" && defaults != 1) || (profile == "riot" && locales != 2))
                throw new InvalidDataException("语言字段数量不符合预期，未修改：" + profile);
            if (profile != "product" && profile != "client" && profile != "system" && profile != "riot")
                throw new ArgumentException("未知配置类型。");
            string patched = LocaleLine.Replace(document.Text, delegate(Match match)
            {
                if ((profile == "client" || profile == "riot") && match.Groups[2].Value != "locale") return match.Value;
                if (profile == "system" && match.Groups[2].Value != "default_locale") return match.Value;
                string old = match.Groups[3].Value;
                string value = old.StartsWith("\"") ? "\"zh_CN\"" : old.StartsWith("'") ? "'zh_CN'" : "zh_CN";
                return match.Groups[1].Value + value + match.Groups[4].Value + match.Groups[5].Value;
            });
            return document.Encode(patched);
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }

        private static string ValidatePath(string path)
        {
            string full = Path.GetFullPath(path);
            if (!File.Exists(full)) throw new FileNotFoundException("未找到配置文件。请检查路径。", full);
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("配置文件是链接，无法安全修改。");
            return full;
        }

        public static string Inspect(string path)
        {
            path = ValidatePath(path);
            Document document = Decode(File.ReadAllBytes(path));
            StringBuilder result = new StringBuilder();
            foreach (Match match in LocaleLine.Matches(document.Text))
                result.AppendLine(match.Groups[2].Value + " = " + match.Groups[3].Value);
            result.AppendLine("只读：" + (((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) ? "是" : "否"));
            result.AppendLine("原始备份：" + (File.Exists(BackupPath(path)) ? "已存在" : "尚未创建"));
            return result.ToString();
        }

        private static FileAttributes ReadBackup(string path, out byte[] bytes)
        {
            string backup = BackupPath(path);
            string meta = MetadataPath(path);
            if (!File.Exists(backup) || !File.Exists(meta))
                throw new IOException("备份不完整或不存在，无法恢复。请保留 .bak 和 .bak.meta 两个文件。");
            string[] lines = File.ReadAllLines(meta, Encoding.UTF8);
            int attributes;
            bytes = File.ReadAllBytes(backup);
            if (lines.Length != 3 || lines[0] != "LOL-LOCALE-BACKUP-1" || !int.TryParse(lines[1], out attributes) || lines[2] != Hash(bytes))
                throw new IOException("备份校验失败，已停止操作。");
            return (FileAttributes)attributes;
        }

        private static void WriteNew(string path, byte[] bytes)
        {
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }

        private static void EnsureBackup(string path, byte[] bytes, FileAttributes attributes)
        {
            if (File.Exists(BackupPath(path)) || File.Exists(MetadataPath(path)))
            {
                byte[] saved;
                ReadBackup(path, out saved);
                return;
            }
            WriteNew(BackupPath(path), bytes);
            try
            {
                WriteNew(MetadataPath(path), new UTF8Encoding(false).GetBytes("LOL-LOCALE-BACKUP-1\n" + (int)attributes + "\n" + Hash(bytes) + "\n"));
            }
            catch { File.Delete(BackupPath(path)); throw; }
        }

        private static void WriteAndVerify(string path, byte[] bytes, FileAttributes attributes)
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.SetLength(bytes.Length);
                stream.Flush(true);
            }
            File.SetAttributes(path, attributes);
            if (Hash(File.ReadAllBytes(path)) != Hash(bytes) || File.GetAttributes(path) != attributes)
                throw new IOException("写入后的校验失败，配置可能被其他程序更改。");
        }

        public static void Apply(string path, Action<string> log) { Apply(path, "product", true, log); }

        public static void Apply(string path, string profile, bool readOnly, Action<string> log)
        {
            path = ValidatePath(path);
            byte[] original = File.ReadAllBytes(path);
            byte[] patched = Patch(original, profile);
            FileAttributes attributes = File.GetAttributes(path);
            EnsureBackup(path, original, attributes);
            log("原始文件已备份：" + BackupPath(path));
            try { WriteAndVerify(path, patched, readOnly ? attributes | FileAttributes.ReadOnly : attributes); }
            catch
            {
                try { WriteAndVerify(path, original, attributes); }
                catch (Exception rollback) { throw new IOException("写入失败且自动回滚失败，请从备份恢复：" + BackupPath(path), rollback); }
                throw;
            }
            log("语言字段已改为 zh_CN，并验证文件内容" + (readOnly ? "及只读属性。" : "。"));
        }

        public static string FindInstallPath(string productPath)
        {
            Document document = Decode(File.ReadAllBytes(ValidatePath(productPath)));
            Match match = Regex.Match(document.Text, @"^product_install_full_path:[ \t]*""([^""\r\n]+)""[ \t]*\r?$", RegexOptions.Multiline);
            if (!match.Success) throw new InvalidDataException("无法识别游戏安装目录，请手动选择。");
            return Path.GetFullPath(match.Groups[1].Value);
        }

        public static FileStream Protect(string path)
        {
            return new FileStream(ValidatePath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        public static FileStream ApplyAndProtectProduct(string path, Action<string> log)
        {
            Apply(path, log);
            FileStream guard = null;
            try
            {
                guard = Protect(path);
                byte[] current = File.ReadAllBytes(path);
                if (Hash(current) != Hash(Patch(current)))
                    throw new IOException("获取保护时语言被其他程序改回，未开启保护，请重试。");
                return guard;
            }
            catch
            {
                if (guard != null) guard.Dispose();
                AllowUpdates(path, log);
                throw;
            }
        }

        public static void AllowUpdates(string path, Action<string> log)
        {
            path = ValidatePath(path);
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                throw new IOException("未能取消配置文件的只读属性。");
            log("产品配置已取消只读，语言内容和备份保持不变。其他版本工具持有的文件锁需关闭对应工具释放。");
        }

        public static void ApplyAll(string productPath, string installPath, bool syncGame, Action<string> log)
        { ApplyAll(productPath, installPath, syncGame, null, log); }

        public static void ApplyAll(string productPath, string installPath, bool syncGame, string riotPath, Action<string> log)
        { ApplyAll(productPath, installPath, syncGame, riotPath, true, log); }

        public static void ApplyAll(string productPath, string installPath, bool syncGame, string riotPath, bool productReadOnly, Action<string> log)
        {
            var pathList = new System.Collections.Generic.List<string> { productPath };
            var profileList = new System.Collections.Generic.List<string> { "product" };
            if (syncGame)
            {
                pathList.Add(Path.Combine(installPath, "Config", "LeagueClientSettings.yaml")); profileList.Add("client");
                pathList.Add(Path.Combine(installPath, "system.yaml")); profileList.Add("system");
            }
            if (riotPath != null) { pathList.Add(riotPath); profileList.Add("riot"); }
            string[] paths = pathList.ToArray();
            string[] profiles = profileList.ToArray();
            byte[][] originals = new byte[paths.Length][];
            FileAttributes[] attributes = new FileAttributes[paths.Length];
            // Validate every document and create all backups before changing any configuration.
            for (int i = 0; i < paths.Length; i++)
            {
                paths[i] = ValidatePath(paths[i]);
                originals[i] = File.ReadAllBytes(paths[i]);
                Patch(originals[i], profiles[i]);
                attributes[i] = File.GetAttributes(paths[i]);
                EnsureBackup(paths[i], originals[i], attributes[i]);
            }
            int last = -1;
            try
            {
                for (int i = 0; i < paths.Length; i++)
                {
                    last = i;
                    Apply(paths[i], profiles[i], i == 0 && productReadOnly, log);
                    if (i == 0 && !productReadOnly) AllowUpdates(paths[i], log);
                }
            }
            catch (Exception failure)
            {
                StringBuilder errors = new StringBuilder();
                for (int i = last; i >= 0; i--)
                    try { WriteAndVerify(paths[i], originals[i], attributes[i]); }
                    catch (Exception rollback) { errors.AppendLine(paths[i] + ": " + rollback.Message); }
                if (errors.Length != 0) throw new IOException("操作失败，部分文件无法回滚，请使用备份恢复：\n" + errors, failure);
                throw;
            }
        }

        public static void Restore(string path, Action<string> log)
        {
            path = ValidatePath(path);
            byte[] backup;
            FileAttributes attributes = ReadBackup(path, out backup);
            byte[] current = File.ReadAllBytes(path);
            FileAttributes currentAttributes = File.GetAttributes(path);
            try { WriteAndVerify(path, backup, attributes); }
            catch
            {
                try { WriteAndVerify(path, current, currentAttributes); }
                catch (Exception rollback) { throw new IOException("恢复失败且回滚失败，请手动检查备份。", rollback); }
                throw;
            }
            log("已恢复首次备份的完整内容及原始文件属性。备份已保留。");
        }

        public static void StopRiot(Action<string> log)
        {
            using (Process game = FirstProcess("League of Legends"))
                if (game != null) throw new InvalidOperationException("游戏对局进程正在运行，请先退出游戏再操作。");
            for (int attempt = 0; attempt < 5; attempt++)
            {
                Process[] processes = Process.GetProcessesByName("RiotClientServices");
                if (processes.Length == 0) { log("已确认 RiotClientServices.exe 未运行。"); return; }
                foreach (Process process in processes)
                {
                    using (process)
                    {
                        log("正在结束 RiotClientServices.exe 及其子进程，PID " + process.Id + "…");
                        ProcessStartInfo info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), "/PID " + process.Id + " /T /F");
                        info.UseShellExecute = false; info.CreateNoWindow = true;
                        info.RedirectStandardOutput = true; info.RedirectStandardError = true;
                        using (Process killer = Process.Start(info))
                        {
                            var output = killer.StandardOutput.ReadToEndAsync();
                            var error = killer.StandardError.ReadToEndAsync();
                            if (!killer.WaitForExit(15000)) { killer.Kill(); throw new IOException("结束客户端进程超时。"); }
                            if (killer.ExitCode != 0 && Process.GetProcessesByName("RiotClientServices").Length != 0)
                                throw new IOException("无法结束客户端进程：" + error.Result + output.Result);
                        }
                    }
                }
                Thread.Sleep(500);
            }
            throw new IOException("RiotClientServices 持续重新启动，请关闭启动器后重试。");
        }

        private static Process FirstProcess(string name)
        {
            Process[] processes = Process.GetProcessesByName(name);
            for (int i = 1; i < processes.Length; i++) processes[i].Dispose();
            return processes.Length == 0 ? null : processes[0];
        }
    }
}
