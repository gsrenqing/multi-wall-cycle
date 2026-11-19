using System;
using System.Diagnostics;
using System.IO;

namespace MultiWallCycle
{
    /// <summary>
    /// v2.2：正式版不再生成任何日志文件。
    /// 所有 Info/Warn/Error 均标注 [Conditional("TRACE_LOG")]：
    /// · 正式版编译（build.bat，不定义该符号）→ 调用点在编译期被整体移除，既不建文件也没有任何 IO/加锁开销；
    /// · 测试版编译（build-test.bat，/define:TRACE_LOG）→ 恢复原有的 log.txt 记录（与配置同目录，超 1MB 清空重记）。
    /// </summary>
    public static class Log
    {
        private static readonly object Sync = new object();
        private static string _logPath;

        /// <summary>日志路径（仅测试版会真正创建；正式版保留属性用于卸载时清理历史遗留的 log.txt）。</summary>
        public static string LogPath
        {
            get
            {
                if (_logPath == null)
                {
                    _logPath = Path.Combine(ConfigIO.ConfigDir, "log.txt");
                }
                return _logPath;
            }
        }

        [Conditional("TRACE_LOG")]
        public static void Info(string msg) { Write("INFO", msg); }

        [Conditional("TRACE_LOG")]
        public static void Warn(string msg) { Write("WARN", msg); }

        [Conditional("TRACE_LOG")]
        public static void Error(string msg) { Write("ERROR", msg); }

        [Conditional("TRACE_LOG")]
        private static void Write(string level, string msg)
        {
            lock (Sync)
            {
                try
                {
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 1024)
                    {
                        File.WriteAllText(LogPath, string.Empty);
                    }
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + level + "] " + msg;
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
                catch
                {
                    // 日志失败不影响主流程
                }
            }
        }
    }
}
