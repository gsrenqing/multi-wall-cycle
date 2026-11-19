using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MultiWallCycle
{
    /// <summary>
    /// 卸载（v2.2 引入，v2.3 随界面语言双语化并适配改名）。
    /// 需要清理的东西恰好分三类：
    /// ① 注册表 —— HKCU\Software\Microsoft\Windows\CurrentVersion\Run 下的自启值
    ///    「MultiWallCycle」，以及旧版残留值「多屏幻灯壁纸」；
    /// ② 程序生成的文件 —— config.json / bing_meta.json / log.txt / temp 缓存，
    ///    可能落在四处：当前配置目录、exe 同目录（绿色便携模式）、
    ///    %LOCALAPPDATA%\MultiWallCycle（v2.3 新目录）、%LOCALAPPDATA%\多屏幻灯壁纸（v2.2 及以前）；
    /// ③ exe 自身 —— 运行中的 exe 无法直接删除，改为本进程退出后由隐藏的 PowerShell 调
    ///    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(..., SendToRecycleBin) 移入系统回收站
    ///    （绝不用 File.Delete / del，那是永久删除，用户明确要求可恢复）。
    /// 用户的壁纸文件夹（图片本身）一律不碰。
    /// </summary>
    public static class Uninstaller
    {
        /// <summary>确认文案随界面语言切换（中文版为 v2.2 原文，英文版为对应译文）。</summary>
        public static string ConfirmText
        {
            get { return Lang.T("uninstall.confirm"); }
        }

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "MultiWallCycle";
        private const string LegacyRunValueName = "多屏幻灯壁纸";

        /// <summary>本程序会写出的所有文件名（含原子写留下的 .tmp 与损坏备份 .bak）。</summary>
        private static readonly string[] ProductFiles = new string[] {
            "config.json", "config.json.tmp", "config.json.bak",
            "bing_meta.json", "bing_meta.json.tmp",
            "log.txt", "log.txt.tmp",
            "wpworker.exe"          // v1.1.x 的辅助进程，v2.1 起已不再生成，老用户机器上可能残留
        };

        /// <summary>列出将被清理的位置（界面提示/文档核对用），随界面语言输出。</summary>
        public static List<string> CollectTargets()
        {
            bool en = Lang.IsEn;
            List<string> targets = new List<string>();
            targets.Add(@"HKCU\" + RunKeyPath + @"\" + RunValueName + (en ? " (start-up entry)" : "（开机自启项）"));
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string cfgDir = ConfigIO.ConfigDir;
            if (!string.Equals(cfgDir.TrimEnd('\\'), exeDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                targets.Add(Path.Combine(cfgDir, "config.json / bing_meta.json / log.txt"));
            }
            targets.Add(Path.Combine(exeDir, en ? "config.json / bing_meta.json / log.txt (portable mode)"
                                                : "config.json / bing_meta.json / log.txt（绿色便携模式）"));
            targets.Add(ConfigIO.UserDataDir + (en ? @"\ (config, Bing metadata, temp cache - whole folder)"
                                                   : @"\（配置、必应描述、temp 缓存整个目录）"));
            string legacy = ConfigIO.LegacyUserDataDir;
            if (Directory.Exists(legacy))
            {
                targets.Add(legacy + (en ? @"\ (leftovers from v2.2 and earlier)" : @"\（v2.2 及更早版本的残留目录）"));
            }
            targets.Add(Application.ExecutablePath + (en ? " (this executable, moved to the Recycle Bin)" : "（本程序 exe，移入回收站）"));
            return targets;
        }

        /// <summary>执行全部清理（不含删除自身 exe）。返回 false 表示有项目清理失败（已尽量继续）。</summary>
        public static bool Cleanup()
        {
            bool ok = true;

            // ① 注册表自启项
            try { RemoveAutoStartValues(); }
            catch (Exception ex) { ok = false; Log.Warn("删除自启项失败: " + ex.Message); }

            // ② 各位置的产品文件
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string cfgDir = ConfigIO.ConfigDir;
            List<string> dirs = new List<string>();
            dirs.Add(cfgDir);
            if (!dirs.Contains(exeDir)) { dirs.Add(exeDir); }

            foreach (string dir in dirs)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { continue; }
                foreach (string name in ProductFiles)
                {
                    DeleteFileQuietly(Path.Combine(dir, name));
                }
            }

            // ③ 本机数据目录整体删除（v2.3 新目录 + v2.2 旧目录都要清）
            if (!DeleteDataDir(ConfigIO.UserDataDir)) { ok = false; }
            if (!DeleteDataDir(ConfigIO.LegacyUserDataDir)) { ok = false; }
            return ok;
        }

        /// <summary>整体删除一个数据目录；失败时退一步把目录里的文件删干净。返回 false 表示有残留。</summary>
        private static bool DeleteDataDir(string dataDir)
        {
            try
            {
                if (!Directory.Exists(dataDir)) { return true; }
                Directory.Delete(dataDir, true);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("删除数据目录失败（可能被其它会话的实例占用）: " + dataDir + " -> " + ex.Message);
                try
                {
                    string tempDir = Path.Combine(dataDir, "temp");
                    if (Directory.Exists(tempDir))
                    {
                        foreach (string f in Directory.GetFiles(tempDir)) { DeleteFileQuietly(f); }
                    }
                    foreach (string name in ProductFiles)
                    {
                        DeleteFileQuietly(Path.Combine(dataDir, name));
                    }
                }
                catch { }
                return false;
            }
        }

        /// <summary>
        /// 删除 Run 项下本程序的自启值：新值名、旧值名，以及任何仍指向本程序/旧程序名的值
        /// （兼容历史上换过值名的情况）。
        /// </summary>
        private static void RemoveAutoStartValues()
        {
            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (run == null) { return; }
                foreach (string known in new[] { RunValueName, LegacyRunValueName })
                {
                    if (run.GetValue(known) != null)
                    {
                        try { run.DeleteValue(known, false); } catch { }
                    }
                }
                List<string> names = new List<string>(run.GetValueNames());
                foreach (string name in names)
                {
                    if (string.Equals(name, RunValueName, StringComparison.OrdinalIgnoreCase)) { continue; }
                    if (string.Equals(name, LegacyRunValueName, StringComparison.OrdinalIgnoreCase)) { continue; }
                    object val = run.GetValue(name);
                    string s = (val == null) ? string.Empty : val.ToString();
                    if (s.IndexOf("多屏幻灯壁纸", StringComparison.OrdinalIgnoreCase) >= 0
                        || s.IndexOf("MultiWallCycle", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        try { run.DeleteValue(name, false); } catch { }
                    }
                }
            }
        }

        private static void DeleteFileQuietly(string path)
        {
            try
            {
                if (File.Exists(path)) { File.Delete(path); }
            }
            catch { }
        }

        /// <summary>
        /// 安排"本进程退出后把 exe 移入回收站"：隐藏窗口启动 PowerShell，延时 3 秒后调用
        /// Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile 的 RecycleOption.SendToRecycleBin。
        /// exe 正在运行时有文件锁，必须先退出再删，所以只能交给外部进程延时执行。
        /// </summary>
        public static bool ScheduleRecycleSelf(string exePath)
        {
            try
            {
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    Log.Warn("待删除的 exe 不存在: " + exePath);
                    return false;
                }
                string psPath = exePath.Replace("'", "''");
                string cmd = "Start-Sleep -Milliseconds 3000;"
                    + " Add-Type -AssemblyName Microsoft.VisualBasic;"
                    + " try { [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile('" + psPath
                    + "','OnlyErrorDialogs','SendToRecycleBin') } catch { }";

                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe");
                psi.Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + cmd + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("安排把 exe 移入回收站失败: " + ex.Message);
                return false;
            }
        }
    }
}
