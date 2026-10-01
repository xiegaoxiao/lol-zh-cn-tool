using System;
using System.IO;
using System.Text;
using LolLocaleTool;

class EngineTests
{
    static int checks;
    static void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
    static void Equal(byte[] actual, byte[] expected, string label) { Check(Convert.ToBase64String(actual) == Convert.ToBase64String(expected), label); }
    static void Reject(Action action, string label)
    {
        try { action(); } catch (IOException) { checks++; return; } catch (InvalidDataException) { checks++; return; } catch (DecoderFallbackException) { checks++; return; } catch (UnauthorizedAccessException) { checks++; return; }
        throw new Exception(label);
    }
    static byte[] Encode(string text, Encoding encoding)
    {
        byte[] preamble = encoding.GetPreamble(), body = encoding.GetBytes(text), bytes = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length); Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length); return bytes;
    }
    static void Log(string message) { }
    static int Main(string[] args)
    {
        string root = Path.Combine(args[0], "run-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string source = "# 保留注释\r\nlocale_data:\r\n    default_locale: \"zh_TW\" # 默认\r\nsettings:\r\n    locale: 'en_US'\r\nother_locale: en_US\r\n";
            string expected = source.Replace("\"zh_TW\"", "\"zh_CN\"").Replace("'en_US'", "'zh_CN'");
            Encoding[] encodings = { new UTF8Encoding(false), new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true) };
            foreach (Encoding encoding in encodings) Equal(LocaleEngine.Patch(Encode(source, encoding)), Encode(expected, encoding), "Encoding/newline preservation");
            Equal(LocaleEngine.Patch(Encoding.UTF8.GetBytes("default_locale: zh_TW\nlocale: en_US")), Encoding.UTF8.GetBytes("default_locale: zh_CN\nlocale: zh_CN"), "Unquoted values without final newline");
            Reject(delegate { LocaleEngine.Patch(Encoding.UTF8.GetBytes("locale: en_US\n")); }, "Missing field rejected");
            Reject(delegate { LocaleEngine.Patch(Encoding.UTF8.GetBytes("default_locale: en_US\nlocale: en_US\nlocale: zh_TW\n")); }, "Duplicate field rejected");
            Reject(delegate { LocaleEngine.Patch(new byte[] { 0xff, 0, 0 }); }, "Invalid encoding rejected");
            string product = Path.Combine(root, "product.yaml"); byte[] original = Encode(source, new UTF8Encoding(true)); File.WriteAllBytes(product, original);
            FileAttributes attributes = File.GetAttributes(product);
            LocaleEngine.Apply(product, Log); Check((File.GetAttributes(product) & FileAttributes.ReadOnly) != 0, "Read-only applied");
            byte[] appliedBytes = File.ReadAllBytes(product);
            LocaleEngine.AllowUpdates(product, Log);
            Equal(File.ReadAllBytes(product), appliedBytes, "Unlock preserves locale bytes");
            Check((File.GetAttributes(product) & FileAttributes.ReadOnly) == 0, "Update mode clears read-only");
            Equal(File.ReadAllBytes(LocaleEngine.BackupPath(product)), original, "Update mode preserves backup");
            LocaleEngine.Apply(product, Log); Equal(File.ReadAllBytes(LocaleEngine.BackupPath(product)), original, "Repeated apply preserves first backup");
            LocaleEngine.Restore(product, Log); Equal(File.ReadAllBytes(product), original, "Restore original bytes"); Check(File.GetAttributes(product) == attributes, "Restore attributes");
            string game = Path.Combine(root, "game"); Directory.CreateDirectory(Path.Combine(game, "Config"));
            string client = Path.Combine(game, "Config", "LeagueClientSettings.yaml"), system = Path.Combine(game, "system.yaml"), riot = Path.Combine(root, "riot.yaml");
            byte[] clientOriginal = Encoding.UTF8.GetBytes("globals:\n  locale: en_US\n  volume: 50\n");
            File.WriteAllBytes(client, clientOriginal); File.WriteAllText(system, "TW2:\n  default_locale: zh_TW\n  region: TW2\n");
            File.WriteAllText(riot, "install:\n  globals:\n    locale: en_US\n  localization:\n    locale: en_US\n");
            File.SetAttributes(product, File.GetAttributes(product) | FileAttributes.ReadOnly);
            LocaleEngine.ApplyAll(product, game, true, riot, false, Log);
            Check((File.GetAttributes(product) & FileAttributes.ReadOnly) == 0, "Compatible apply clears previous read-only");
            using (FileStream writeProbe = new FileStream(system, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                Check(writeProbe.CanWrite, "Compatible apply does not hold system lock");
            LocaleEngine.ApplyAll(product, game, true, riot, Log);
            Check(File.ReadAllText(client) == "globals:\n  locale: zh_CN\n  volume: 50\n", "Game preferences preserved");
            Check((File.GetAttributes(client) & FileAttributes.ReadOnly) == 0, "Game preferences remain writable");
            Check(File.ReadAllText(system) == "TW2:\n  default_locale: zh_CN\n  region: TW2\n", "System region preserved");
            Check(File.ReadAllText(riot).Split(new string[] { "zh_CN" }, StringSplitOptions.None).Length == 3, "Both Riot fields patched");
            LocaleEngine.Restore(product, Log);
            byte[] before = File.ReadAllBytes(product);
            File.WriteAllText(client, "broken: config\n");
            Reject(delegate { LocaleEngine.ApplyAll(product, game, true, Log); }, "Preflight detects broken game config"); Equal(File.ReadAllBytes(product), before, "Preflight leaves product untouched");
            File.WriteAllBytes(client, clientOriginal);
            using (FileStream heldWriter = new FileStream(system, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(delegate { LocaleEngine.ApplyAll(product, game, true, Log); }, "Batch write conflict rejected");
            Equal(File.ReadAllBytes(product), before, "Batch rollback restores product"); Equal(File.ReadAllBytes(client), clientOriginal, "Batch rollback restores game client");
            string protectedPath = Path.Combine(root, "protected.yaml"); File.WriteAllBytes(protectedPath, original);
            using (FileStream guard = LocaleEngine.ApplyAndProtectProduct(protectedPath, Log))
            {
                Equal(File.ReadAllBytes(protectedPath), LocaleEngine.Patch(original), "Apply-and-protect locks zh_CN bytes");
                using (FileStream otherFile = new FileStream(client, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                    Check(otherFile.CanWrite, "Product protection leaves other files writable");
                File.SetAttributes(protectedPath, FileAttributes.Normal);
                Reject(delegate { File.WriteAllText(protectedPath, "overwrite"); }, "Guard denies writing even after clearing read-only");
                Reject(delegate { File.Delete(protectedPath); }, "Guard denies deleting");
                Reject(delegate { File.Move(protectedPath, protectedPath + ".moved"); }, "Guard denies renaming");
                string replacement = Path.Combine(root, "replacement.yaml"); File.WriteAllText(replacement, "replacement");
                Reject(delegate { File.Replace(replacement, protectedPath, null); }, "Guard denies replacement");
            }
            File.WriteAllText(protectedPath, "unlocked"); Check(File.ReadAllText(protectedPath) == "unlocked", "Writes allowed after guard release");
            string monitoredDirectory = Path.Combine(root, "monitored"); Directory.CreateDirectory(monitoredDirectory);
            string monitoredPath = Path.Combine(monitoredDirectory, "product.yaml"), snapshot = Path.Combine(root, "recovery.snapshot");
            File.WriteAllBytes(monitoredPath, original);
            using (DefensiveProtection monitor = new DefensiveProtection(monitoredPath, snapshot, Log))
            {
                Check(monitor.CheckAndRepair(), "Monitor establishes initial protection");
                Equal(File.ReadAllBytes(monitoredPath), LocaleEngine.Patch(original), "Monitor sets both CN values");
                Check(File.Exists(snapshot), "Recovery snapshot stored outside target directory");
                Check(!monitor.CheckAndRepair(), "Healthy check retains existing lock");
                monitor.ReleaseLock();
                File.SetAttributes(monitoredPath, FileAttributes.Normal);
                File.WriteAllBytes(monitoredPath, original);
                Check(monitor.CheckAndRepair(), "Monitor corrects language reversion");
                Equal(File.ReadAllBytes(monitoredPath), LocaleEngine.Patch(original), "Reverted values repaired");
                monitor.ReleaseLock(); File.SetAttributes(monitoredPath, FileAttributes.Normal); File.Delete(monitoredPath);
                Check(monitor.CheckAndRepair(), "Deleted file recreated");
                Equal(File.ReadAllBytes(monitoredPath), LocaleEngine.Patch(original), "Deleted file restored from snapshot");
                Reject(delegate { File.Delete(monitoredPath); }, "Restored file is protected again");
                monitor.ReleaseLock();
                string changedSource = source + "should_repair: true\r\n";
                File.SetAttributes(monitoredPath, FileAttributes.Normal); File.WriteAllBytes(monitoredPath, Encode(changedSource, new UTF8Encoding(true)));
                Check(monitor.CheckAndRepair(), "Monitor reacquires replaced file");
                byte[] latestGood = File.ReadAllBytes(monitoredPath);
                Check(Encoding.UTF8.GetString(latestGood).Contains("should_repair: true"), "Non-language fields preserved on repair");
                monitor.ReleaseLock();
                foreach (string file in Directory.GetFiles(monitoredDirectory)) File.SetAttributes(file, FileAttributes.Normal);
                if (!Path.GetFullPath(monitoredDirectory).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test directory");
                Directory.Delete(monitoredDirectory, true);
                Check(monitor.CheckAndRepair(), "Deleted parent directory recreated");
                Equal(File.ReadAllBytes(monitoredPath), latestGood, "Latest snapshot survives removal of entire target directory");
                monitor.ReleaseLock(); File.SetAttributes(monitoredPath, FileAttributes.Normal); File.Delete(monitoredPath);
                string validSnapshot = File.ReadAllText(snapshot); File.AppendAllText(snapshot, "tampered");
                Reject(delegate { monitor.CheckAndRepair(); }, "Invalid recovery snapshot rejected");
                Check(!File.Exists(monitoredPath), "Invalid snapshot never recreates target");
                File.WriteAllText(snapshot, validSnapshot);
                Check(monitor.CheckAndRepair(), "Repair resumes after transient recovery failure");
                Equal(File.ReadAllBytes(monitoredPath), latestGood, "Recovered content remains correct");
            }
            string missingTarget = Path.Combine(root, "no-source", "product.yaml");
            using (DefensiveProtection noSnapshot = new DefensiveProtection(missingTarget, Path.Combine(root, "missing.snapshot"), Log))
                Reject(delegate { noSnapshot.CheckAndRepair(); }, "No invented configuration without recovery source");
            File.AppendAllText(LocaleEngine.BackupPath(product), "corrupt");
            Reject(delegate { LocaleEngine.Restore(product, Log); }, "Corrupted backup rejected");
            Equal(File.ReadAllBytes(product), before, "Corrupt backup does not modify target");
            Console.WriteLine("PASS: " + checks + " checks (encoding, exact edits, backups, rollback, sharing protection)"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            // Test data is restricted to this verified unique subdirectory.
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(args[0]) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
        }
    }
}
