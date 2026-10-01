using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LolLocaleTool
{
    // The recovery snapshot lives outside Riot's metadata directory.
    internal sealed class DefensiveProtection : IDisposable
    {
        private readonly string target;
        private readonly string recovery;
        private readonly Action<string> log;
        private FileStream guard;

        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdentity
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileIdentity identity);

        public DefensiveProtection(string targetPath, string recoveryPath, Action<string> logger)
        {
            target = Path.GetFullPath(targetPath);
            recovery = Path.GetFullPath(recoveryPath);
            if (recovery.StartsWith(Path.GetDirectoryName(target) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("恢复快照必须放在被保护目录之外。");
            log = logger;
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
        }

        private void CheckParents()
        {
            for (DirectoryInfo directory = new DirectoryInfo(Path.GetDirectoryName(target)); directory != null; directory = directory.Parent)
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("配置目录包含链接，已暂停修复以避免写入其他位置。");
            if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("配置文件是链接，已暂停修复。");
        }

        private void SaveSnapshot(byte[] bytes)
        {
            string serialized = "LOL-CN-RECOVERY-1\n" + Convert.ToBase64String(Encoding.UTF8.GetBytes(target.ToUpperInvariant())) + "\n" + Hash(bytes) + "\n" + Convert.ToBase64String(bytes) + "\n";
            if (File.Exists(recovery) && File.ReadAllText(recovery, Encoding.UTF8) == serialized) return;
            Directory.CreateDirectory(Path.GetDirectoryName(recovery));
            string temporary = recovery + ".tmp";
            using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] content = new UTF8Encoding(false).GetBytes(serialized);
                stream.Write(content, 0, content.Length); stream.Flush(true);
            }
            if (File.Exists(recovery)) File.Replace(temporary, recovery, null);
            else File.Move(temporary, recovery);
        }

        private byte[] ReadSnapshot()
        {
            if (!File.Exists(recovery)) throw new IOException("目标文件已消失且没有恢复快照。请先让 Riot 重建配置，再开启保护。");
            try
            {
                string[] lines = File.ReadAllLines(recovery, Encoding.UTF8);
                if (lines.Length != 4 || lines[0] != "LOL-CN-RECOVERY-1" ||
                    Encoding.UTF8.GetString(Convert.FromBase64String(lines[1])) != target.ToUpperInvariant())
                    throw new InvalidDataException("恢复快照格式或目标路径不符。");
                byte[] bytes = Convert.FromBase64String(lines[3]);
                if (Hash(bytes) != lines[2] || Hash(bytes) != Hash(LocaleEngine.Patch(bytes)))
                    throw new InvalidDataException("恢复快照校验失败。");
                return bytes;
            }
            catch (FormatException error) { throw new InvalidDataException("恢复快照编码错误。", error); }
        }

        private bool IsHealthy()
        {
            if (guard == null || !File.Exists(target)) return false;
            using (FileStream current = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                FileIdentity heldId, pathId;
                if (!GetFileInformationByHandle(guard.SafeFileHandle, out heldId) || !GetFileInformationByHandle(current.SafeFileHandle, out pathId))
                    throw new IOException("无法核验当前路径与保护句柄的文件标识。");
                if (heldId.Volume != pathId.Volume || heldId.IndexHigh != pathId.IndexHigh || heldId.IndexLow != pathId.IndexLow) return false;
            }
            byte[] bytes = File.ReadAllBytes(target);
            if (Hash(bytes) != Hash(LocaleEngine.Patch(bytes))) return false;
            SaveSnapshot(bytes);
            return true;
        }

        // Called every second. Returns true when repair/reacquisition was required.
        public bool CheckAndRepair()
        {
            CheckParents();
            if (IsHealthy()) return false;
            ReleaseLock();
            if (!File.Exists(target))
            {
                byte[] bytes = ReadSnapshot();
                CheckParents();
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                try
                {
                    using (FileStream stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                    log("发现配置文件/目录消失，已从外部快照重建目标 YAML。");
                }
                catch (IOException) { if (!File.Exists(target)) throw; }
            }
            try
            {
                guard = LocaleEngine.ApplyAndProtectProduct(target, log);
                if (!IsHealthy()) throw new IOException("重新保护后路径/语言核验失败。");
                log("语言及文件标识已核验，持续保护已重新建立。");
                return true;
            }
            catch { ReleaseLock(); throw; }
        }

        // Also used by integration tests to simulate loss of an old file handle.
        internal void ReleaseLock() { if (guard != null) { guard.Dispose(); guard = null; } }
        public void Dispose() { ReleaseLock(); }
    }
}
