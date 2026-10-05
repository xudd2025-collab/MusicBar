using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MusicBar
{
    // Explicit, reversible shortcut configuration. No player executable or
    // registry entry is modified and no running player is interrupted here.
    internal static class NetEaseLaunchIntegration
    {
        internal const string Arguments = "--remote-debugging-port=9223 --remote-debugging-address=127.0.0.1";
        public sealed class Backup
        {
            public string Path, Target, OriginalArguments, AppliedArguments, File;
            public bool Created;
        }
        internal static string MergeArguments(string arguments)
        {
            string clean = Regex.Replace(arguments ?? "", @"(?:^|\s)--remote-debugging-(?:port|address)(?:=(?:""[^""]*""|\S+)|\s+(?:""[^""]*""|\S+))", "", RegexOptions.IgnoreCase).Trim();
            return (clean.Length == 0 ? "" : clean + " ") + Arguments;
        }
        internal static List<string> ShortcutRoots()
        {
            var roots = new List<string>();
            foreach (string path in new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft\\Internet Explorer\\Quick Launch\\User Pinned\\TaskBar") })
                if (!string.IsNullOrWhiteSpace(path) && !roots.Contains(path)) roots.Add(path);
            return roots;
        }
        private static bool Allowed(string path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return false;
            string full = System.IO.Path.GetFullPath(path);
            foreach (string root in ShortcutRoots())
                if (full.StartsWith(System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        internal static bool ValidPlayer(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) &&
                System.IO.Path.GetFileName(path).Equals("cloudmusic.exe", StringComparison.OrdinalIgnoreCase);
        }
        private static object Get(object obj, string name)
        { return obj.GetType().InvokeMember(name, BindingFlags.GetProperty, null, obj, null); }
        private static void Set(object obj, string name, string value)
        { obj.GetType().InvokeMember(name, BindingFlags.SetProperty, null, obj, new object[] { value }); }
        private static object Link(object shell, string path)
        { return shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path }); }
        private static void Save(object link)
        { link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null); }
        private static void Release(object obj)
        { if (obj != null && Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj); }
        private static string BackupDirectory(string data)
        { return System.IO.Path.Combine(data, "netease-shortcut-backup"); }
        private static List<Backup> ReadBackups(string data)
        {
            string manifest = System.IO.Path.Combine(BackupDirectory(data), "manifest.json");
            if (!File.Exists(manifest)) return new List<Backup>();
            if (new FileInfo(manifest).Length > 1024 * 1024) throw new InvalidOperationException("快捷方式备份清单过大。");
            return new JavaScriptSerializer().Deserialize<List<Backup>>(File.ReadAllText(manifest, Encoding.UTF8)) ?? new List<Backup>();
        }
        private static void WriteBackups(string data, List<Backup> entries)
        {
            string folder = BackupDirectory(data); Directory.CreateDirectory(folder);
            string file = System.IO.Path.Combine(folder, "manifest.json"), temp = file + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(entries), new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(temp, file, null); else File.Move(temp, file);
        }
        internal static string Configure(string playerPath, string data)
        {
            if (!ValidPlayer(playerPath)) throw new InvalidOperationException("请选择网易云安装目录的 cloudmusic.exe。");
            playerPath = System.IO.Path.GetFullPath(playerPath);
            var entries = ReadBackups(data);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in ShortcutRoots()) Collect(root, paths, 0);
            string managed = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "网易云音乐（任务栏歌词）.lnk");
            paths.Add(managed);
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
            int configured = 0, skipped = 0;
            try
            {
                foreach (string path in paths)
                {
                    object link = null;
                    try
                    {
                        if (!Allowed(path)) continue;
                        bool exists = File.Exists(path);
                        link = Link(shell, path);
                        string target = Convert.ToString(Get(link, "TargetPath"));
                        if (exists && (!ValidPlayer(target) || !System.IO.Path.GetFullPath(target).Equals(playerPath, StringComparison.OrdinalIgnoreCase))) continue;
                        if (!exists && path != managed) continue;
                        string original = Convert.ToString(Get(link, "Arguments")), applied = MergeArguments(original);
                        if (exists && original == applied) { configured++; continue; }
                        Backup entry = entries.Find(delegate(Backup b) { return string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase); });
                        if (entry == null)
                        {
                            string backupName;
                            using (var sha = SHA256.Create()) backupName = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).Replace("-", "") + ".lnk";
                            Directory.CreateDirectory(BackupDirectory(data));
                            if (exists) File.Copy(path, System.IO.Path.Combine(BackupDirectory(data), backupName), false);
                            entry = new Backup { Path = path, Target = playerPath, OriginalArguments = original, File = backupName, Created = !exists };
                            entries.Add(entry);
                        }
                        entry.AppliedArguments = applied;
                        // Commit the recovery record before changing the shortcut.
                        WriteBackups(data, entries);
                        Set(link, "TargetPath", playerPath); Set(link, "Arguments", applied);
                        if (!exists) { Set(link, "WorkingDirectory", System.IO.Path.GetDirectoryName(playerPath)); Set(link, "IconLocation", playerPath + ",0"); }
                        Save(link); Release(link); link = null;
                        link = Link(shell, path);
                        if (Convert.ToString(Get(link, "Arguments")) != applied) throw new IOException("快捷方式参数未保存。");
                        configured++;
                    }
                    catch (UnauthorizedAccessException) { skipped++; }
                    catch (IOException) { skipped++; }
                    catch (COMException) { skipped++; }
                    catch (TargetInvocationException ex) { if (ex.InnerException is COMException) skipped++; else throw; }
                    finally { Release(link); }
                }
            }
            finally { Release(shell); }
            if (configured == 0) throw new InvalidOperationException("没有可写的网易云快捷方式，请检查桌面目录权限。");
            return "已配置 " + configured + " 个启动入口" + (skipped > 0 ? "，另有 " + skipped + " 个入口无法修改" : "") + "。桌面「网易云音乐（任务栏歌词）」可直接使用；已运行的网易云需退出并重新打开一次。";
        }
        private static void Collect(string folder, HashSet<string> paths, int depth)
        {
            if (depth > 8 || paths.Count > 4096 || !Directory.Exists(folder)) return;
            try
            {
                foreach (string path in Directory.GetFiles(folder, "*.lnk")) paths.Add(path);
                foreach (string child in Directory.GetDirectories(folder))
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) Collect(child, paths, depth + 1);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        internal static string Restore(string data)
        {
            var entries = ReadBackups(data); var remaining = new List<Backup>(); int restored = 0;
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
            try
            {
                foreach (Backup entry in entries)
                {
                    object link = null;
                    try
                    {
                        if (!Allowed(entry.Path) || System.IO.Path.GetFileName(entry.File) != entry.File) throw new IOException("备份路径无效。");
                        if (!File.Exists(entry.Path)) { restored++; continue; }
                        link = Link(shell, entry.Path);
                        if (!string.Equals(Convert.ToString(Get(link, "TargetPath")), entry.Target, StringComparison.OrdinalIgnoreCase) ||
                            Convert.ToString(Get(link, "Arguments")) != entry.AppliedArguments) { remaining.Add(entry); continue; }
                        Release(link); link = null;
                        if (entry.Created) File.Delete(entry.Path);
                        else File.Copy(System.IO.Path.Combine(BackupDirectory(data), entry.File), entry.Path, true);
                        restored++;
                    }
                    catch (IOException) { remaining.Add(entry); }
                    catch (UnauthorizedAccessException) { remaining.Add(entry); }
                    catch (COMException) { remaining.Add(entry); }
                    catch (TargetInvocationException ex) { if (ex.InnerException is COMException) remaining.Add(entry); else throw; }
                    finally { Release(link); }
                }
            }
            finally { Release(shell); }
            WriteBackups(data, remaining);
            return "已还原 " + restored + " 个启动入口" + (remaining.Count > 0 ? "；" + remaining.Count + " 个已被修改或无法还原，保留备份。" : "。");
        }
    }
}
