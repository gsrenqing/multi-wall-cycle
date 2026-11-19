using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
namespace MultiWallCycle
{
    /// <summary>单个显示器的运行时状态（配置快照 + 调度数据）。</summary>
    public class MonitorRuntime
    {
        public string DevicePath;
        public string ModelName = string.Empty;  // v1.1.2 第四轮：兜底模式真实型号（EnumDisplayDevices 子设备 DeviceString）
        public RECT Rect;
        public bool Enabled;
        public string Folder;
        public int IntervalMinutes;
        public int Mode;      // 0=顺序, 1=随机
        public int Fit;       // 0=系统设置, 1=裁剪填充
        public bool UseBing;  // v1.1.2：使用必应每日壁纸来源
        public bool ShowCaption;  // v1.1.2：Bing 描述文字叠加显示
        public bool FollowMain;   // v2.0：跟随主显示器（设置与主屏一致，随主屏同图更换）
        public DateTime NextSwitchUtc;
        public int LastIndex = -1;
        /// <summary>v2.2：顺序轮换进度（"最旧/最新两头交叉"序列下标；-1 = 从未换过）。</summary>
        public int SeqPos = -1;
        /// <summary>v2.2：上一次实际应用的源图（随机模式用来避免连续重复；文件增删时也更稳）。</summary>
        public string LastFile = string.Empty;
        /// <summary>v2.2：仅必应模式下，上一次使用的"最近 8 天"列表下标（-1 = 未开始）。</summary>
        public int BingPos = -1;
    }

    /// <summary>
    /// 核心引擎：枚举显示器、每屏独立计时调度、调用 IDesktopWallpaper 逐屏设置壁纸。
    /// v1.1.0：显示器按 DevicePath 绑定配置（拔插不失配不丢配置）；图片列表带缓存（
    /// FileSystemWatcher 只置脏标记 + 30 分钟兜底重扫 + 目录消失清缓存）；顺序模式自然排序。
    /// </summary>
    public class WallpaperEngine
    {
        private IDesktopWallpaper _dw;
        private readonly List<MonitorRuntime> _monitors = new List<MonitorRuntime>();
        private readonly Random _rnd = new Random();
        private string _tempDir;
        private string _signature = string.Empty;

        // ---- v1.1.2 第四轮修正版（#15）：壁纸辅助进程（wpworker.exe）----
        // 根因：WinForms 主进程内的 IDesktopWallpaper COM 通道可能进入持久失效状态
        // （枚举 E_FAIL、SetWallpaper 假成功——调用不抛异常但系统壁纸不更新），且无法恢复
        // （重启 Explorer/RebuildCom/重启主程序均无效）；独立控制台进程 COM 通道始终干净。
        // 因此枚举与设置均优先经辅助进程：每次操作新进程 → 干净 COM 通道 → 读回验证真成功。
        private readonly List<WorkerMonitorInfo> _lastWorkerInfos = new List<WorkerMonitorInfo>();

        // v1.1.3：RECT → 完整 DevicePath（\\?\DISPLAY#...）映射。
        // 用于①兜底态下把 Win32 子设备名还原成完整路径设置壁纸；②保存配置时保留真实绑定，
        // 不再因"兜底期保存"把 DevicePath 清成空串（v1.1.2 的实际缺陷）。
        private readonly Dictionary<string, string> _knownFullPaths = new Dictionary<string, string>();
        // 重建冷却：枚举抖动（Win32 兜底 1/2 屏波动）导致 RebuildMonitors 高频触发，
        // 新建 rt 会把 NextSwitchUtc 重置为 now+interval，自动轮换永远到不了点 → 冷却跳过。
        private DateTime _lastRebuildUtc = DateTime.MinValue;
        private const double RebuildCooldownSeconds = 30;

        private sealed class WorkerMonitorInfo
        {
            public string DevicePath;
            public RECT Rect;
        }

        /// <summary>辅助进程绝对路径（与主程序同目录）。</summary>
        private static string WorkerPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wpworker.exe");
        }

        // ---- v1.1.0 图片列表缓存 ----
        private readonly Dictionary<string, List<string>> _fileCache = new Dictionary<string, List<string>>();
        private readonly Dictionary<string, DateTime> _folderScanTime = new Dictionary<string, DateTime>();
        private readonly HashSet<string> _dirtyFolders = new HashSet<string>();
        private readonly object _cacheLock = new object();
        private readonly Dictionary<string, FileSystemWatcher> _watchers = new Dictionary<string, FileSystemWatcher>();
        private const double ForceRescanMinutes = 30;

        /// <summary>全局开关（暂停/继续）。仅影响自动轮换，不影响手动“立即更换”。</summary>
        public bool GlobalEnabled = true;

        /// <summary>文字描述叠加样式（v1.1.2，全局一份；随配置重建同步）。</summary>
        public CaptionStyle CaptionStyle = new CaptionStyle();

        public List<MonitorRuntime> Monitors { get { return _monitors; } }
        public bool ComOk { get { return _dw != null; } }

        public WallpaperEngine()
        {
            // v1.1.0 #8：临时目录移到 %LOCALAPPDATA% （避免 Program Files 无写权限）
            _tempDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "多屏幻灯壁纸", "temp");
        }

        public void Init()
        {
            try
            {
                _dw = DesktopWallpaperFactory.Create();
                Log.Info("IDesktopWallpaper COM 创建成功");
            }
            catch (Exception ex)
            {
                Log.Error("创建 IDesktopWallpaper 失败: " + ex.Message);
            }
            // v2.2：启动时收敛 temp 目录（历史上因 cap 文件漏删可能已膨胀到数 GB）
            try { CleanStaleTemp(); } catch { }
        }

        /// <summary>目标屏像素宽/高（文字 80px 限位换算用）。</summary>
        private static int RtW(MonitorRuntime rt) { return rt.Rect.Right - rt.Rect.Left; }
        private static int RtH(MonitorRuntime rt) { return rt.Rect.Bottom - rt.Rect.Top; }

        /// <summary>
        /// 释放并重建 IDesktopWallpaper COM 对象（v1.1.2 第三轮：显示器拔插后 COM 的 RPC 通道
        /// 可能进入失效状态导致枚举/设置持续抛异常，重建对象可脱离坏通道恢复）。
        /// </summary>
        public bool RebuildCom()
        {
            try
            {
                if (_dw != null)
                {
                    try { System.Runtime.InteropServices.Marshal.ReleaseComObject(_dw); } catch { }
                    _dw = null;
                }
                _dw = DesktopWallpaperFactory.Create();
                Log.Info("IDesktopWallpaper COM 已重建");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("重建 IDesktopWallpaper 失败: " + ex.Message);
                _dw = null;
                return false;
            }
        }

        /// <summary>
        /// v1.1.3 枚举优先级（见 v1.1.3\验证说明.md 的实测结论）：
        /// ① CCD —— 主路径。不依赖壁纸 COM 服务，且 QDC_ONLY_ACTIVE_PATHS 只返回活动路径，
        ///    天然排除"已断开但被系统记住"的占位显示器（Default_Monitor，即"屏幕3"）。
        /// ② 辅助进程 wpworker.exe —— 兜底，仍可拿到完整 DevicePath。
        /// ③ 进程内 COM —— 兜底，每次新建 COM 对象（等效"干净通道"），按 RECT 过滤占位屏。
        /// ④ Win32 EnumDisplayMonitors —— 最后兜底（子设备名，保证界面与调度可用）。
        /// 所有路径统一剔除 RECT 宽/高为 0 或取不到 RECT 的项，杜绝幽灵屏进入界面。
        /// </summary>
        private bool EnumerateRaw(out List<string> paths, out List<RECT> rects, out List<string> models)
        {
            paths = new List<string>();
            rects = new List<RECT>();
            models = new List<string>();

            if (TryCcdEnum(paths, rects, models)) { return true; }

            paths.Clear(); rects.Clear(); models.Clear();
            if (TryWorkerEnum(paths, rects, models)) { return true; }

            paths.Clear(); rects.Clear(); models.Clear();
            if (TryComEnum(paths, rects, models)) { return true; }

            // 前三档都失败：Win32 兜底（v1.1.2 第三轮，用子设备监视器名，非适配器名）
            paths.Clear(); rects.Clear(); models.Clear();
            return TryWin32Fallback(paths, rects, models);
        }

        /// <summary>
        /// v1.1.3 主枚举路径：CCD。返回完整 DevicePath + 真实型号 + 已按 rotation 修正的虚拟坐标。
        /// 实测与 COM 枚举对真实显示器的结果逐字符一致，且不含未连接的占位显示器。
        /// </summary>
        private bool TryCcdEnum(List<string> paths, List<RECT> rects, List<string> models)
        {
            try
            {
                List<CcdMonitorInfo> mons;
                if (!CcdMonitors.EnumMonitors(out mons)) { return false; }
                for (int i = 0; i < mons.Count; i++)
                {
                    if (!mons[i].Available) { continue; }
                    if (string.IsNullOrEmpty(mons[i].DevicePath)) { continue; }
                    if (!IsValidRect(mons[i].Rect)) { continue; }
                    paths.Add(mons[i].DevicePath);
                    rects.Add(mons[i].Rect);
                    // 型号不使用 CCD 的 FriendlyName（MainForm 优先走 GetMonitorFriendlyName
                    // 注册表解析，v1.1.2 已验证；此处留空保持一致行为）
                    models.Add(string.Empty);
                    RememberFullPath(mons[i].DevicePath, mons[i].Rect);
                }
                if (paths.Count == 0) { return false; }
                // 成功是常态，不再逐次记日志（此前 ComputeSignature 每 5 秒调用一次，会刷屏）
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("CCD 枚举失败，转用兜底: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 进程内 COM 枚举（兜底）。v1.1.3：每次新建 COM 对象，避免长期持有对象在拔插后状态陈旧
        /// （这正是 wpworker 起新进程带来的好处，进程内新建对象等效且无进程开销）。
        /// 同时剔除 GetMonitorRECT 失败或 RECT 为 0x0 的项（Default_Monitor 等占位屏）。
        /// </summary>
        private bool TryComEnum(List<string> paths, List<RECT> rects, List<string> models)
        {
            IDesktopWallpaper dw = null;
            try
            {
                dw = DesktopWallpaperFactory.Create();
                uint count;
                dw.GetMonitorDevicePathCount(out count);
                for (uint i = 0; i < count; i++)
                {
                    string id;
                    dw.GetMonitorDevicePathAt(i, out id);
                    RECT r;
                    try { dw.GetMonitorRECT(id, out r); }
                    catch { continue; }                 // 未连接占位屏：GetMonitorRECT 返回 E_FAIL
                    if (!IsValidRect(r)) { continue; }  // 0x0：无实际显示区域
                    paths.Add(id);
                    rects.Add(r);
                    models.Add(string.Empty);           // 型号由 GetMonitorFriendlyName 从 DevicePath 解析
                    RememberFullPath(id, r);
                }
                if (paths.Count > 0)
                {
                    Log.Info("显示器枚举经进程内 COM 成功，共 " + paths.Count + " 屏");
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Warn("进程内 COM 枚举失败: " + ex.Message);
                return false;
            }
            finally
            {
                if (dw != null)
                {
                    try { System.Runtime.InteropServices.Marshal.ReleaseComObject(dw); } catch { }
                }
            }
        }

        /// <summary>RECT 是否有效（宽高均大于 0）——用于剔除未连接的占位显示器。</summary>
        private static bool IsValidRect(RECT r)
        {
            return (r.Right > r.Left) && (r.Bottom > r.Top);
        }

        /// <summary>RECT → 完整 DevicePath 映射键。</summary>
        private static string RectKey(RECT r)
        {
            return r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom;
        }

        /// <summary>记住某屏的完整 DevicePath，供兜底态下按 RECT 还原（保存配置/设置壁纸时用）。</summary>
        private void RememberFullPath(string path, RECT r)
        {
            if (string.IsNullOrEmpty(path)) { return; }
            // 与 Program.cs 回填判据保持一致：非 Win32 设备名即完整 DevicePath（更稳健）
            if (path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase)) { return; }
            _knownFullPaths[RectKey(r)] = path;
        }

        /// <summary>按虚拟坐标取已知的完整 DevicePath（兜底态把 Win32 子设备名还原成完整路径）。</summary>
        public string GetKnownFullPath(RECT r)
        {
            string v;
            return _knownFullPaths.TryGetValue(RectKey(r), out v) ? v : string.Empty;
        }

        /// <summary>
        /// v2.4（采纳审查 #2）：辅助进程输出改成异步收集 + 超时 Kill。
        /// 原写法先 StandardOutput.ReadToEnd() 再 WaitForExit(timeout)——若 wpworker 卡住不退出，
        /// ReadToEnd 会一直阻塞（timeout 参数根本等不到），整个界面就冻在这儿。
        /// </summary>
        private static string ReadWorkerOutput(Process p, int timeoutMs)
        {
            StringBuilder sb = new StringBuilder();
            object gate = new object();
            try
            {
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        lock (gate) { sb.Append(e.Data).Append('\n'); }
                    }
                };
                p.BeginOutputReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    Log.Warn("辅助进程超时未退出（" + timeoutMs + "ms），已强制结束");
                }
                else
                {
                    p.WaitForExit();   // 让异步读线程把剩余缓冲吐完
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取辅助进程输出失败: " + ex.Message);
            }
            lock (gate) { return sb.ToString(); }
        }

        /// <summary>
        /// 经辅助进程（wpworker.exe）枚举显示器：完整 DevicePath + 虚拟坐标。
        /// 辅助进程是独立控制台进程，其 COM 通道始终干净（主程序进程内通道可能持久失效）。
        /// </summary>
        private bool TryWorkerEnum(List<string> paths, List<RECT> rects, List<string> models)
        {
            try
            {
                string worker = WorkerPath();
                if (!File.Exists(worker)) { return false; }
                ProcessStartInfo psi = new ProcessStartInfo(worker, "enum");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string outp = ReadWorkerOutput(p, 15000);
                    _lastWorkerInfos.Clear();
                    string[] lines = outp.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("ENUM|"))
                        {
                            string[] parts = line.Split('|');
                            if (parts.Length >= 6)
                            {
                                string dp = parts[1];
                                RECT r = new RECT();
                                int.TryParse(parts[2], out r.Left);
                                int.TryParse(parts[3], out r.Top);
                                int.TryParse(parts[4], out r.Right);
                                int.TryParse(parts[5], out r.Bottom);
                                // 判据与其他处一致：非 Win32 设备名即完整 DevicePath
                                if (!dp.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
                                {
                                    // v1.1.3：占位显示器（Default_Monitor，RECT 0x0）同样会出现在
                                    // 辅助进程枚举结果里，必须剔除，否则界面又多出"屏幕3"。
                                    if (!IsValidRect(r)) { continue; }
                                    paths.Add(dp);
                                    rects.Add(r);
                                    models.Add(string.Empty);   // 完整路径型号由 GetMonitorFriendlyName 解析
                                    _lastWorkerInfos.Add(new WorkerMonitorInfo { DevicePath = dp, Rect = r });
                                    RememberFullPath(dp, r);
                                }
                            }
                        }
                    }
                    if (paths.Count > 0)
                    {
                        Log.Info("显示器枚举经辅助进程成功，共 " + paths.Count + " 屏");
                        return true;
                    }
                    Log.Warn("辅助进程枚举未返回有效屏: " + outp.Trim());
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("辅助进程枚举失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 调用辅助进程设置壁纸并读回验证。辅助进程 Set 后立即 Get 读回对比：
        /// 读回一致才算真成功（程序进程内 COM 的 Set 假成功——读回不变——被彻底绕开）。
        /// </summary>
        private bool SetWallpaperViaWorker(string monitorId, string imgPath)
        {
            try
            {
                string worker = WorkerPath();
                if (!File.Exists(worker))
                {
                    Log.Error("壁纸辅助进程不存在: " + worker);
                    return false;
                }
                ProcessStartInfo psi = new ProcessStartInfo(worker,
                    "\"set\" \"" + monitorId + "\" \"" + imgPath + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string outp = ReadWorkerOutput(p, 20000);
                    if (outp.StartsWith("SET_OK"))
                    {
                        return true;
                    }
                    Log.Error("壁纸辅助进程设置未生效: " + outp.Trim());
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Error("调用壁纸辅助进程失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 设置壁纸（读回验证真成功）。候选 monitorID 依次尝试：
        /// ① 本屏 DevicePath（完整路径，权威且不被 DynamicTheme 等抢占）；
        /// ② 若本屏 DevicePath 是 Win32 MonitorId（兜底枚举），追加辅助进程枚举缓存中
        ///    同虚拟坐标的完整 DevicePath（按 RECT 匹配，避免换屏）。
        /// </summary>
        private bool SetWallpaperChecked(MonitorRuntime rt, string imgPath)
        {
            List<string> candidates = new List<string>();
            string primary = rt.DevicePath;
            if (!string.IsNullOrEmpty(primary)) { candidates.Add(primary); }

            // 兜底态：DevicePath 是 Win32 子设备名（\\.\DISPLAY1\Monitor0）时，
            // 追加按虚拟坐标还原出的完整 DevicePath（完整路径才是"真生效"的 monitorID）。
            if (string.IsNullOrEmpty(primary) || primary.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase))
            {
                foreach (WorkerMonitorInfo wi in _lastWorkerInfos)
                {
                    if (wi.Rect.Left == rt.Rect.Left && wi.Rect.Top == rt.Rect.Top
                        && wi.Rect.Right == rt.Rect.Right && wi.Rect.Bottom == rt.Rect.Bottom)
                    {
                        if (!candidates.Contains(wi.DevicePath)) { candidates.Add(wi.DevicePath); }
                        break;
                    }
                }
                string known = GetKnownFullPath(rt.Rect);
                if (known.Length > 0 && !candidates.Contains(known)) { candidates.Add(known); }
            }

            foreach (string raw in candidates)
            {
                // 双保险：清理不可见脏字符并校验形态。脏 monitorID 会让 COM 把 Set
                // 退化为全局设置（两屏同图），这是 v1.1.3 首测翻车的根因，务必拦截。
                string cand = CleanMonitorId(raw);
                if (!IsValidMonitorId(cand))
                {
                    Log.Warn("跳过非法 monitorID: len=" + (raw ?? "(null)").Length
                        + " 首字符=" + (string.IsNullOrEmpty(raw) ? "-" : ((int)raw[0]).ToString("X4")));
                    continue;
                }
                // v1.1.3 主路径：进程内直接设置（实测真生效，见 v1.1.3\验证说明.md）
                if (SetWallpaperInProcess(cand, imgPath)) { return true; }
                // 兜底：辅助进程（wpworker.exe 缺失时此步直接返回 false，不再阻塞主路径）
                if (SetWallpaperViaWorker(cand, imgPath)) { return true; }
            }
            return false;
        }

        /// <summary>清理 monitorID 首尾的不可见脏字符（\0 前缀/填充、BOM、零宽字符等）。</summary>
        private static string CleanMonitorId(string id)
        {
            if (string.IsNullOrEmpty(id)) { return string.Empty; }
            return id.Trim('\0', '\uFEFF', '\u200B', ' ', '\t', '\r', '\n');
        }

        /// <summary>monitorID 形态校验：必须是 \\?\（完整 DevicePath）或 \\.\（Win32 设备名）开头。</summary>
        private static bool IsValidMonitorId(string id)
        {
            if (string.IsNullOrEmpty(id)) { return false; }
            return id.StartsWith(@"\\?\") || id.StartsWith(@"\\.\");
        }

        /// <summary>
        /// v1.1.3：进程内设置壁纸并读回验证。
        /// 关键三点（2026-11-01 实测坐实）：
        /// ① monitorID 必须是完整 DevicePath（\\?\DISPLAY#...）；Win32 适配器名（\\.\DISPLAY1）
        ///    是"假成功"——读回一致但桌面不渲染，这正是 v1.1.2「换壁纸无效」的真根因；
        /// ② 每次新建 COM 对象 —— 等效复刻 wpworker"起新进程"带来的干净通道，
        ///    避免长期持有对象在显示器拔插后状态陈旧，且无进程启动开销；
        /// ③ 【防全局化】传入的 monitorID 若带脏字符（如 CCD 读取偏移错误产生的 \0 前缀），
        ///    COM 匹配不到该显示器，会把 Set 退化为"默认显示器"= 全局设置，导致所有屏
        ///    显示同一张图（v1.1.3 首测「两屏共用壁纸」的根因）。故设置前强制校验
        ///    monitorID 以 \\?\ 或 \\.\ 开头，非法则拒绝执行并转兜底。
        /// </summary>
        private bool SetWallpaperInProcess(string monitorId, string imgPath)
        {
            if (!IsValidMonitorId(monitorId))
            {
                Log.Warn("monitorID 非法（拒绝设置以防全局化）: len="
                    + (monitorId ?? "(null)").Length + " 首字符="
                    + (string.IsNullOrEmpty(monitorId) ? "-" : ((int)monitorId[0]).ToString("X4")));
                return false;
            }
            IDesktopWallpaper dw = null;
            try
            {
                dw = DesktopWallpaperFactory.Create();
                dw.SetWallpaper(monitorId, imgPath);
                string readback;
                dw.GetWallpaper(monitorId, out readback);
                if (string.Equals(readback, imgPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                Log.Warn("进程内设置壁纸读回不一致（疑似假成功），monitorID=" + monitorId
                    + " 读回=" + (readback ?? "(空)"));
                return false;
            }
            catch (Exception ex)
            {
                Log.Warn("进程内设置壁纸异常，转用辅助进程: " + ex.Message);
                return false;
            }
            finally
            {
                if (dw != null)
                {
                    try { System.Runtime.InteropServices.Marshal.ReleaseComObject(dw); } catch { }
                }
            }
        }

        /// <summary>
        /// COM 枚举不可用时的 Win32 兜底（v1.1.2 第三轮 + 第四轮修复）：
        /// IDesktopWallpaper 的枚举接口在拔插显示器后可能持续 E_FAIL（系统级，重启程序无效）。
        /// 实测结论（v1.1.2 第四轮，#15）：SetWallpaper 用适配器名 "\\.\DISPLAY1" 是假成功
        /// （COM 记账读回生效但桌面不渲染，用户看到"换壁纸无效"）；必须用子设备监视器名
        /// "\\.\DISPLAY1\Monitor0"（EnumDisplayDevices 子设备）才真正生效。
        /// 同时子设备 DeviceString 即真实硬件型号（ViewSonic VX2762-2K-HD 等），回传 models
        /// 供界面显示（解决 #14 兜底型号显示异常）。
        /// </summary>
        private bool TryWin32Fallback(List<string> paths, List<RECT> rects, List<string> models)
        {
            List<Win32MonitorInfo> mons;
            if (Win32Monitors.EnumMonitors(out mons))
            {
                if (mons.Count > 0)
                {
                    for (int i = 0; i < mons.Count; i++)
                    {
                        paths.Add(mons[i].MonitorId);
                        rects.Add(mons[i].Rect);
                        models.Add(mons[i].ModelName);
                    }
                    Log.Warn("COM 枚举失败，已用 Win32 兜底恢复 " + mons.Count
                        + " 屏配置（子设备监视器名按拓扑顺序映射）");
                    return true;
                }
                Log.Error("COM 枚举失败且 Win32 检测到 0 屏（无活动显示器，保留旧配置等待恢复）");
            }
            paths.Clear();
            rects.Clear();
            models.Clear();
            return false;
        }

        public string ComputeSignature()
        {
            List<string> paths;
            List<RECT> rects;
            List<string> models;
            bool ok = EnumerateRaw(out paths, out rects, out models);
            if (!ok) { return string.Empty; }
            string sig = string.Empty;
            for (int i = 0; i < paths.Count; i++)
            {
                sig += paths[i] + "@" + rects[i].Left + "," + rects[i].Top + ","
                    + rects[i].Right + "," + rects[i].Bottom + ";";
            }
            return sig;
        }

        /// <summary>显示器布局是否变化（数量/设备路径/位置）。</summary>
        public bool MonitorLayoutChanged()
        {
            string sig = ComputeSignature();
            // v1.1.3：枚举失败时 ComputeSignature 返回空串。v1.1.2 会把空串当成"布局变了"
            // （与旧签名不等），从而触发重建 + 弹出「显示器连接情况已变化」气泡；
            // 枚举恢复后又会再触发一次。这里改为失败即视为"未变化"，不更新签名、不误报。
            if (sig.Length == 0) { return false; }
            if (sig != _signature)
            {
                _signature = sig;
                return true;
            }
            return false;
        }

        /// <summary>
        /// v1.1.0 #2/#3：按 DevicePath 合并重建运行时。
        /// - 已连接的屏按 DevicePath 匹配配置（找不到用下标兜底，兼容旧版无 DevicePath 的 config）；
        /// - 保留已存在屏的 LastIndex / NextSwitchUtc（顺序模式保存后不跳回第 1 张、关窗口不推迟轮换）；
        /// - 不再 RemoveRange 删除"消失屏"的配置（拔掉再插回时按 DevicePath 恢复）。
        /// </summary>
        public void RebuildMonitors(AppConfig cfg, bool preserveState = true, bool force = false)
        {
            // v1.1.2 第四轮修正版（#15）：枚举抖动（Win32 兜底 1/2 屏波动）会高频触发本方法，
            // 新建 rt 会把 NextSwitchUtc 重置为 now+interval，使自动轮换永远到不了点（"到点不换"）。
            // 加冷却：抖动期保留现有配置与调度，等枚举稳定后再重建。
            // v2.2 修正：用户明确点「保存并应用」/「重新扫描」/关窗恢复时必须立即生效，
            // 否则启动后 30 秒内的保存会被冷却静默跳过（配置已写盘、运行时不生效，重启才回来）。
            if (!force && DateTime.UtcNow - _lastRebuildUtc < TimeSpan.FromSeconds(RebuildCooldownSeconds))
            {
                Log.Warn("显示器重建请求过于频繁（枚举抖动），" + RebuildCooldownSeconds
                    + " 秒冷却内跳过重建，保留现有配置与调度");
                return;
            }
            _lastRebuildUtc = DateTime.UtcNow;

            List<string> paths;
            List<RECT> rects;
            List<string> models;
            bool ok = EnumerateRaw(out paths, out rects, out models);

            // v1.1.2 第三轮：枚举失败（COM 失效且重建仍失败）时保留现有屏列表，
            // 不把界面打成"共0屏"全空；等下一次成功枚举恢复。
            if (!ok)
            {
                Log.Error("显示器枚举失败，保留上次 " + _monitors.Count + " 屏配置（稍后自动重试）");
                if (cfg.Caption != null) { CaptionStyle = cfg.Caption; }
                return;
            }

            Dictionary<string, MonitorRuntime> old = new Dictionary<string, MonitorRuntime>();
            foreach (MonitorRuntime rt in _monitors)
            {
                if (!string.IsNullOrEmpty(rt.DevicePath) && !old.ContainsKey(rt.DevicePath))
                {
                    old[rt.DevicePath] = rt;
                }
            }

            Dictionary<string, MonitorConfig> cfgByPath = new Dictionary<string, MonitorConfig>();
            foreach (MonitorConfig mc in cfg.Monitors)
            {
                if (mc != null)
                {
                    // 自愈历史脏数据：清理旧版本可能写入的 \0 前缀等不可见字符
                    mc.DevicePath = CleanMonitorId(mc.DevicePath ?? string.Empty);
                    if (mc.DevicePath.Length > 0 && !cfgByPath.ContainsKey(mc.DevicePath))
                    {
                        cfgByPath[mc.DevicePath] = mc;
                    }
                }
            }

            _monitors.Clear();
            for (int i = 0; i < paths.Count; i++)
            {
                MonitorRuntime rt = new MonitorRuntime();
                rt.DevicePath = paths[i];
                rt.Rect = rects[i];
                // v1.1.2 第四轮（#14）：兜底模式直接回填真实型号（EnumDisplayDevices 子设备 DeviceString）
                rt.ModelName = (i < models.Count) ? (models[i] ?? string.Empty) : string.Empty;

                MonitorConfig mc;
                if (!cfgByPath.TryGetValue(paths[i], out mc))
                {
                    mc = (i < cfg.Monitors.Count && cfg.Monitors[i] != null)
                        ? cfg.Monitors[i] : new MonitorConfig();
                }

                rt.Enabled = mc.Enabled;
                rt.Folder = mc.Folder;
                rt.IntervalMinutes = mc.IntervalMinutes;
                rt.Mode = mc.Mode;
                rt.Fit = mc.Fit;
                rt.UseBing = mc.UseBing;
                rt.ShowCaption = mc.ShowCaption;
                rt.FollowMain = mc.FollowMain;
                rt.SeqPos = mc.SeqPos;   // v2.2：恢复上次保存的顺序轮换进度

                // v2.0：跟随主显示器 —— 壁纸来源、间隔、模式、适配、必应、文字全部继承主显示器（显示器 1）。
                // 主显示器自身不参与跟随（配置里即便误设也忽略，避免自引用）。
                // 注意：只同步"壁纸设置"，不含「启用此屏」，用户仍可单独停用某一屏。
                if (i > 0 && rt.FollowMain && _monitors.Count > 0)
                {
                    MonitorRuntime main = _monitors[0];
                    rt.Folder = main.Folder;
                    rt.IntervalMinutes = main.IntervalMinutes;
                    rt.Mode = main.Mode;
                    rt.Fit = main.Fit;
                    rt.UseBing = main.UseBing;
                    rt.ShowCaption = main.ShowCaption;
                }

                // v1.1.2：仅勾选 Bing 且无本地文件夹 → 间隔强制 24 小时（每天一张）
                // v2.2 优化②：取消该规则——必应接口能取到"最近 8 天"，改由设定间隔在这 8 张里轮换，
                // 间隔完全由用户设定（1~1440 分钟），不再被强制成 1440。

                if (preserveState && old.ContainsKey(paths[i]))
                {
                    rt.LastIndex = old[paths[i]].LastIndex;
                    rt.NextSwitchUtc = old[paths[i]].NextSwitchUtc;
                    rt.SeqPos = old[paths[i]].SeqPos;          // v2.2：运行时进度比磁盘新，优先用运行时
                    rt.LastFile = old[paths[i]].LastFile;
                    rt.BingPos = old[paths[i]].BingPos;
                }
                else
                {
                    rt.NextSwitchUtc = DateTime.UtcNow.AddMinutes(rt.IntervalMinutes);
                }
                _monitors.Add(rt);
            }

            _signature = ComputeSignature();
            if (cfg.Caption != null) { CaptionStyle = cfg.Caption; }   // v1.1.2：样式随配置同步
            Log.Info("显示器枚举完成，共 " + _monitors.Count + " 个屏幕");
        }

        /// <summary>每秒调用：检查各屏是否到点，到点则更换；并执行每日一次的 Bing 图入库检查。</summary>
        public void CheckSchedules()
        {
            if (!GlobalEnabled) { return; }
            BingDailyCheck();
            DateTime now = DateTime.UtcNow;
            for (int i = 0; i < _monitors.Count; i++)
            {
                MonitorRuntime rt = _monitors[i];
                if (!rt.Enabled) { continue; }
                // v2.0：跟随屏不由自己的计时器驱动，一律随主显示器同图更换（见 SyncFollowers）
                if (i > 0 && rt.FollowMain) { continue; }
                if (!rt.UseBing && string.IsNullOrEmpty(rt.Folder)) { continue; }
                if (now >= rt.NextSwitchUtc)
                {
                    SwitchOne(i, rt);
                }
            }
        }

        /// <summary>所有启用屏立即更换一次（忽略全局暂停，手动操作始终生效）。</summary>
        public void ApplyNowToAll()
        {
            for (int i = 0; i < _monitors.Count; i++)
            {
                MonitorRuntime rt = _monitors[i];
                if (!rt.Enabled) { continue; }
                // v2.0：跟随屏随主显示器一同更换，不单独触发
                if (i > 0 && rt.FollowMain) { continue; }
                if (!rt.UseBing && string.IsNullOrEmpty(rt.Folder)) { continue; }
                SwitchOne(i, rt);
            }
        }

        public void TriggerNow(int index)
        {
            if (index < 0 || index >= _monitors.Count) { return; }
            MonitorRuntime rt = _monitors[index];
            if (!rt.Enabled)
            {
                Log.Warn("显示器 " + (index + 1) + " 未启用，跳过立即更换");
                return;
            }
            if (!rt.UseBing && string.IsNullOrEmpty(rt.Folder))
            {
                Log.Warn("显示器 " + (index + 1) + " 未设置壁纸来源（本地文件夹或必应），跳过立即更换");
                return;
            }
            SwitchOne(index, rt);
        }

        private void SwitchOne(int index, MonitorRuntime rt)
        {
            // v1.1.3：删除原 `if (_dw == null) return;`。
            // 该判空在 v1.1.2 中的后果：只要 COM 初始化失败过一次（_dw 为 null），
            // 即使辅助进程完全可用，换壁纸也彻底静默失效，且 return 前不刷新 NextSwitchUtc
            // （表现为每秒空转、"重启也恢复不了"）。v1.1.3 换壁纸走 SetWallpaperChecked
            // （进程内新建 COM 对象 / 辅助进程兜底），不再依赖长期持有的 _dw 实例。

            // v1.1.2：仅勾选必应（无本地文件夹）→ 每天一张，走 Bing 专用流程
            if (rt.UseBing && string.IsNullOrEmpty(rt.Folder))
            {
                SwitchBingOnly(index, rt);
                return;
            }

            List<string> files = GetImages(rt.Folder);
            if (files.Count == 0)
            {
                Log.Warn("显示器 " + (index + 1) + " 文件夹无可用图片: " + rt.Folder);
                rt.NextSwitchUtc = DateTime.UtcNow.AddMinutes(rt.IntervalMinutes);
                return;
            }

            // v2.2 优化④：顺序模式改为"按修改时间最旧/最新两头交叉"取图（并保存进度）；
            // 随机模式仍随机，但改为避开"上一张实际应用的源图"（文件增删时比按下标更稳）。
            string src;
            int seqPos = -1;
            if (rt.Mode == 1)
            {
                src = PickRandom(rt, files);
            }
            else
            {
                List<string> order = BuildRotationOrder(files);
                seqPos = (rt.SeqPos + 1) % order.Count;
                if (seqPos < 0) { seqPos = 0; }
                src = order[seqPos];
            }

            string final = ApplyFit(rt, src, index);

            // v1.1.2：本地文件夹轮换到带标题的图且开启文字 → 叠加描述
            // v2.4：取消"文件名必须像必应图"这道门（以前 IsBingFile 把本地文件夹里
            // 自己拍的照片、小红书存的图全挡在外面，于是"勾了本地文件夹就一个标题都没有"）。
            // 现在任何源图都走同一条链：meta（日期键/文件名键）→ EXIF（0x010E/0x9C9B）→ XMP dc:title。
            // v2.1：跟随主显示器的屏一律不渲染文字描述（仅由主显示器显示，用户要求）
            string caption = string.Empty;
            if (rt.ShowCaption && !(index > 0 && rt.FollowMain))
            {
                caption = BingWallpaper.GetCaptionForFile(src);
                if (caption.Length > 0)
                {
                    final = BingWallpaper.RenderCaption(final, index, caption, _tempDir, CaptionStyle,
                        RtW(rt), RtH(rt));
                    // v2.2：带文字的产物名是 screenN_cap_*.jpg，旧清理只认 screenN_*.jpg，
                    // 导致每换一次就永久留一张（实测累积 1324 个文件 / 2.6GB）。此处按"该屏只留最新一张"清理。
                    CleanOldCapTemp(index, final);
                }
                else
                {
                    // v2.3：开关开着却无描述时不再静默跳过，写一条日志便于现场排查
                    //（本地图既不在 meta 里、也没有 EXIF 描述，就是这种情况）。
                    Log.Info("显示器 " + (index + 1) + " 已开启文字描述，但这张图没有可用描述: "
                        + Path.GetFileName(src));
                }
            }

            try
            {
                // v1.1.2 第四轮修正版（#15）：经辅助进程设置（读回验证真成功），
                // 绕开主进程内可能持久失效的 COM 通道（Set 假成功不渲染）。
                if (SetWallpaperChecked(rt, final))
                {
                    rt.LastIndex = seqPos;
                    rt.LastFile = src;
                    if (seqPos >= 0)
                    {
                        rt.SeqPos = seqPos;      // v2.2：推进并立即落盘，重启/关机后接着轮换
                        PersistProgress();
                    }
                    rt.NextSwitchUtc = DateTime.UtcNow.AddMinutes(rt.IntervalMinutes);
                    Log.Info("显示器 " + (index + 1) + " 已更换壁纸: " + Path.GetFileName(src)
                        + (caption.Length > 0 ? " [带文字描述]" : ""));
                    // v2.0：主显示器换图后，所有"跟随主显示器"的屏用同一张源图同步更换
                    if (index == 0) { SyncFollowers(src); }
                }
                else
                {
                    rt.NextSwitchUtc = DateTime.UtcNow.AddMinutes(1);
                    Log.Error("显示器 " + (index + 1) + " 设置壁纸未生效（辅助进程读回验证未通过），1 分钟后重试");
                }
            }
            catch (Exception ex)
            {
                Log.Error("显示器 " + (index + 1) + " 设置壁纸异常: " + ex.Message);
                rt.NextSwitchUtc = DateTime.UtcNow.AddMinutes(1);
            }
        }

        /// <summary>
        /// v2.0：主显示器换图后，把所有「跟随主显示器」的屏换成同一张源图。
        /// 各屏按自身分辨率重新适配（裁剪填充 / 文字叠加），因此内容一致且各自显示正确。
        /// </summary>
        private void SyncFollowers(string src)
        {
            for (int i = 1; i < _monitors.Count; i++)
            {
                MonitorRuntime frt = _monitors[i];
                if (!frt.FollowMain || !frt.Enabled) { continue; }
                try
                {
                    string ffinal = ApplyFit(frt, src, i);
                    // v2.1：跟随屏一律不渲染文字描述——即使主显示器开了文字，
                    // 跟随屏也只显示纯图（竖屏下横图文字会溢出屏幕，用户要求由主屏独占文字）
                    if (SetWallpaperChecked(frt, ffinal))
                    {
                        frt.NextSwitchUtc = DateTime.UtcNow.AddMinutes(frt.IntervalMinutes);
                        Log.Info("显示器 " + (i + 1) + " 跟随主显示器更换壁纸: " + Path.GetFileName(src));
                    }
                    else
                    {
                        frt.NextSwitchUtc = DateTime.UtcNow.AddMinutes(1);
                        Log.Error("显示器 " + (i + 1) + " 跟随换图未生效，1 分钟后重试");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("显示器 " + (i + 1) + " 跟随换图异常: " + ex.Message);
                    frt.NextSwitchUtc = DateTime.UtcNow.AddMinutes(1);
                }
            }
        }

        /// <summary>
        /// v2.2 优化②：仅必应来源（无本地文件夹）——在"最近 8 天"的壁纸里按设定间隔轮换。
        /// · 顺序：接口返回本身就是时间倒序（今天 → 昨天 → … → 7 天前），逐张推进；
        /// · 随机：随机取一张且避开上一次那张。
        /// 图片按需下载到 temp\bing_日期.jpg 缓存复用（同一张当天不重复下载）；
        /// 文字描述直接取该条的 copyright（标题+地点+摄影师），与原"每日一张"完全一致。
        /// </summary>
        private void SwitchBingOnly(int index, MonitorRuntime rt)
        {
            try
            {
                if (!Directory.Exists(_tempDir)) { Directory.CreateDirectory(_tempDir); }

                List<BingEntry> entries = GetRecentBingEntries();
                if (entries.Count == 0)
                {
                    Log.Warn("显示器 " + (index + 1) + " 必应近 8 天列表获取失败，1 分钟后重试");
                    rt.NextSwitchUtc = DateTime.UtcNow.AddMinutes(1);
                    return;
                }

                int pos;
                if (rt.Mode == 1)
                {
                    pos = _rnd.Next(entries.Count);
                    if (entries.Count > 1 && pos == rt.BingPos) { pos = (pos + 1) % entries.Count; }
                }
                else
                {
                    pos = (rt.BingPos + 1) % entries.Count;
                }

                // 从选中的那张开始依次兜底尝试，避免某一张下载失败时本屏长时间空转
                BingEntry chosen = null;
                string cache = null;
                int tries = 0;
                int p = pos;
                while (tries < entries.Count)
                {
                    BingEntry e = entries[p];
                    if (e == null || string.IsNullOrEmpty(e.Date)) { p = (p + 1) % entries.Count; tries++; continue; }
                    string path = Path.Combine(_tempDir, "bing_" + e.Date + ".jpg");
                    if (File.Exists(path) && new FileInfo(path).Length >= 20 * 1024)
                    {
                        chosen = e; cache = path; pos = p; break;
                    }
                    if (BingWallpaper.TryDownloadEntry(e, _bingHost, _tempDir, out path))
                    {
                        chosen = e; cache = path; pos = p;
                        CleanOldBingCache();
                        break;
                    }
                    p = (p + 1) % entries.Count;
                    tries++;
                }
                if (chosen == null || cache == null)
                {
                    Log.Warn("显示器 " + (index + 1) + " 必应近 8 天壁纸全部获取失败，1 分钟后重试");
                    rt.NextSwitchUtc = DateTime.UtcNow.AddMinutes(1);
                    return;
                }

                string caption = chosen.Copyright;
                if (string.IsNullOrEmpty(caption)) { caption = BingWallpaper.GetCaption(chosen.Date); }

                string final = ApplyFit(rt, cache, index);
                // v2.1：跟随主显示器的屏一律不渲染文字描述（仅由主显示器显示）
                if (rt.ShowCaption && caption.Length > 0 && !(index > 0 && rt.FollowMain))
                {
                    final = BingWallpaper.RenderCaption(final, index, caption, _tempDir, CaptionStyle,
                        RtW(rt), RtH(rt));
                    CleanOldCapTemp(index, final);
                }

                // v1.1.2 第四轮修正版（#15）：经辅助进程设置（读回验证真成功）
                if (SetWallpaperChecked(rt, final))
                {
                    rt.BingPos = pos;
                    rt.LastFile = cache;
                    rt.NextSwitchUtc = DateTime.UtcNow.AddMinutes(rt.IntervalMinutes);
                    Log.Info("显示器 " + (index + 1) + " 已更换壁纸: Bing " + chosen.Date
                        + (caption.Length > 0 ? " [带文字描述]" : ""));
                    // v2.0：主显示器走必应流程换图后同样同步跟随屏
                    if (index == 0) { SyncFollowers(cache); }
                }
                else
                {
                    rt.NextSwitchUtc = DateTime.UtcNow.AddMinutes(1);
                    Log.Error("显示器 " + (index + 1) + " 必应壁纸设置未生效（辅助进程读回验证未通过），1 分钟后重试");
                }
            }
            catch (Exception ex)
            {
                Log.Error("显示器 " + (index + 1) + " 必应壁纸设置失败: " + ex.Message);
                rt.NextSwitchUtc = DateTime.UtcNow.AddMinutes(1);
            }
        }

        // ---- v2.2：必应"最近 8 天"列表（按天缓存，避免每次换图都请求接口） ----
        private List<BingEntry> _recentBing = new List<BingEntry>();
        private DateTime _recentBingDay = DateTime.MinValue;
        private string _bingHost = "https://cn.bing.com";

        private List<BingEntry> GetRecentBingEntries()
        {
            if (_recentBingDay == DateTime.Today && _recentBing.Count > 0) { return _recentBing; }
            List<BingEntry> list;
            string host;
            if (BingWallpaper.TryGetRecent(8, out list, out host) && list.Count > 0)
            {
                _recentBing = list;
                if (!string.IsNullOrEmpty(host)) { _bingHost = host; }
                _recentBingDay = DateTime.Today;
            }
            else
            {
                Log.Warn("必应近 8 天列表获取失败，沿用上一次成功获取的列表（若无则本次更换失败）");
            }
            return _recentBing;
        }

        /// <summary>
        /// v1.1.2：每日一次——双勾（必应+本地文件夹）的屏把当日 Bing 原图（4K）存入本地文件夹，
        /// 文件名用 Bing 官方原始文件名（如 AlbaceteSpain_ZH-CN1597281896_UHD.jpg），描述映射按文件名保存，
        /// 使 Bing 图参与按设定间隔的本地轮换并可按文件叠加文字。已存在当天图则跳过。
        /// </summary>
        private DateTime _lastBingCheckDate = DateTime.MinValue;
        private DateTime _nextBingRetry = DateTime.MinValue;
        private void BingDailyCheck()
        {
            DateTime today = DateTime.Today;
            if (_lastBingCheckDate == today) { return; }
            // v2.4（采纳审查 #8）：原写法先写 _lastBingCheckDate 再尝试下载，
            // 一旦当天网络抽风（或必应接口临时失效），这一天就再也不重试了——只能等第二天。
            // 现在失败时按下一次冷却（1 小时后）再试，成功才记为"今天已完成"。
            if (DateTime.Now < _nextBingRetry) { return; }
            // v2.0：每日必应原图只入库到【第一个生效的】本地文件夹。
            // 此前每个勾选"必应+本地文件夹"的屏都会各下一份，同一张图在多屏文件夹里重复存储；
            // 现在只下第一份，其余勾选必应的屏照常轮换使用，不再重复下载。
            for (int i = 0; i < _monitors.Count; i++)
            {
                MonitorRuntime rt = _monitors[i];
                if (!rt.Enabled) { continue; }
                if (!rt.UseBing || string.IsNullOrEmpty(rt.Folder)) { continue; }
                string cap;
                string fname;
                if (BingWallpaper.TryDownloadToday(rt.Folder, true, out cap, out fname))
                {
                    _lastBingCheckDate = today;   // v2.4：只有真成功才算"今天已完成"
                    MarkDirty(rt.Folder);   // 入库后置脏，列表重扫自然包含新图
                    Log.Info("必应当日壁纸已入库（仅第一个生效文件夹，其余屏不重复存储）: 显示器 "
                        + (i + 1) + " ← " + fname);
                }
                else
                {
                    _nextBingRetry = DateTime.Now.AddHours(1);
                    Log.Warn("必应当日壁纸入库失败，1 小时后重试，目标文件夹: " + rt.Folder);
                }
                return;   // 只入库第一个生效的文件夹
            }
            // 没有任何"必应+本地文件夹"的屏：本轮无需入库，直接记今天已完成，避免空转
            _lastBingCheckDate = today;
        }

        /// <summary>temp 下 bing_ 缓存只保留最近 10 张（v2.2：轮换池是 8 天，留 10 张含跨天缓冲）。</summary>
        private void CleanOldBingCache()
        {
            try
            {
                if (!Directory.Exists(_tempDir)) { return; }
                List<string> caches = new List<string>(Directory.GetFiles(_tempDir, "bing_*.jpg"));
                if (caches.Count <= 10) { return; }
                caches.Sort();
                for (int i = 0; i < caches.Count - 10; i++)
                {
                    try { File.Delete(caches[i]); } catch { }
                }
            }
            catch { }
        }

        /// <summary>v2.2：随机取一张（避开上一次实际应用的源图；文件增删时比按下标更稳）。</summary>
        private string PickRandom(MonitorRuntime rt, List<string> files)
        {
            int idx = _rnd.Next(files.Count);
            if (files.Count > 1 && string.Equals(files[idx], rt.LastFile, StringComparison.OrdinalIgnoreCase))
            {
                idx = (idx + 1) % files.Count;
            }
            return files[idx];
        }

        /// <summary>
        /// v2.2 优化④：顺序轮换的取图序列 —— 以"文件修改时间"排序后，最旧与最新两头交叉：
        /// 第 1 张=最旧，第 2 张=最新，第 3 张=第 2 旧，第 4 张=第 2 新……直到中间的图片，
        /// 之后从头再轮。例：8 张 a0(最旧)…a7(最新) → a0, a7, a1, a6, a2, a5, a3, a4。
        /// 修改时间相同（同一批拷贝/下载）时按文件名自然排序稳定区分，保证序列可复现。
        /// </summary>
        private static List<string> BuildRotationOrder(List<string> files)
        {
            List<string> sorted = new List<string>(files.Count);
            Dictionary<string, DateTime> times = new Dictionary<string, DateTime>(files.Count);
            foreach (string f in files)
            {
                DateTime t;
                try { t = File.GetLastWriteTime(f); }
                catch { t = DateTime.MinValue; }
                times[f] = t;
                sorted.Add(f);
            }
            sorted.Sort(delegate(string a, string b)
            {
                DateTime ta = times[a], tb = times[b];
                int c = ta.CompareTo(tb);
                if (c != 0) { return c; }
                return NaturalCompare(a, b);
            });

            List<string> order = new List<string>(sorted.Count);
            int lo = 0, hi = sorted.Count - 1;
            while (lo <= hi)
            {
                order.Add(sorted[lo]);
                lo++;
                if (lo <= hi)
                {
                    order.Add(sorted[hi]);
                    hi--;
                }
            }
            return order;
        }

        /// <summary>v2.2：把当前各屏的顺序轮换进度写回 config.json（按 DevicePath 匹配）。</summary>
        private void PersistProgress()
        {
            try
            {
                List<string> paths = new List<string>(_monitors.Count);
                List<int> positions = new List<int>(_monitors.Count);
                foreach (MonitorRuntime rt in _monitors)
                {
                    paths.Add(rt.DevicePath ?? string.Empty);
                    positions.Add(rt.SeqPos);
                }
                ConfigIO.UpdateSeqPositions(paths, positions);
            }
            catch { }
        }

        /// <summary>v2.2：退出/关机前再落盘一次顺序进度（配置不存在时自然跳过）。</summary>
        public void SaveProgress()
        {
            PersistProgress();
        }

        /// <summary>
        /// v1.1.0 #7：带缓存的图片列表。
        /// - FileSystemWatcher 事件只置脏标记（不增量维护，防 8KB 缓冲区溢出错乱）；
        /// - 每 30 分钟强制重扫兜底（网盘/网络位置可能不触发 watcher）；
        /// - 目录不存在时清空缓存返回空（U 盘/移动硬盘拔掉防黑屏）。
        /// </summary>
        private List<string> GetImages(string folder)
        {
            if (string.IsNullOrEmpty(folder)) { return new List<string>(); }

            if (!Directory.Exists(folder))
            {
                lock (_cacheLock)
                {
                    _fileCache.Remove(folder);
                    _folderScanTime.Remove(folder);
                    _dirtyFolders.Remove(folder);
                }
                return new List<string>();
            }

            EnsureWatcher(folder);

            bool dirty;
            lock (_cacheLock) { dirty = _dirtyFolders.Remove(folder); }

            DateTime now = DateTime.UtcNow;
            DateTime last;
            lock (_cacheLock) { last = _folderScanTime.ContainsKey(folder) ? _folderScanTime[folder] : DateTime.MinValue; }
            bool force = (now - last).TotalMinutes >= ForceRescanMinutes;

            lock (_cacheLock)
            {
                if (!dirty && !force && _fileCache.ContainsKey(folder))
                {
                    return _fileCache[folder];
                }
            }

            List<string> files = LoadImages(folder);
            lock (_cacheLock)
            {
                _fileCache[folder] = files;
                _folderScanTime[folder] = now;
            }
            return files;
        }

        private void EnsureWatcher(string folder)
        {
            lock (_cacheLock)
            {
                if (_watchers.ContainsKey(folder)) { return; }
            }
            try
            {
                FileSystemWatcher w = new FileSystemWatcher(folder);
                w.Filter = "*.*";
                w.IncludeSubdirectories = false;
                w.Changed += delegate { MarkDirty(folder); };
                w.Created += delegate { MarkDirty(folder); };
                w.Deleted += delegate { MarkDirty(folder); };
                w.Renamed += delegate { MarkDirty(folder); };
                w.Error += delegate { MarkDirty(folder); };   // 缓冲区溢出：宁可重扫不可错乱
                w.EnableRaisingEvents = true;
                lock (_cacheLock) { _watchers[folder] = w; }
            }
            catch
            {
                // 目录不支持 watcher（网络/压缩盘）：30 分钟兜底重扫仍生效
            }
        }

        private void MarkDirty(string folder)
        {
            lock (_cacheLock) { _dirtyFolders.Add(folder); }
        }

        /// <summary>全量扫描 + 自然排序 + 过滤隐藏/0字节（v1.1.0 #4）。</summary>
        private static List<string> LoadImages(string folder)
        {
            List<string> result = new List<string>();
            try
            {
                string[] all = Directory.GetFiles(folder);
                foreach (string f in all)
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".jpg" || ext == ".jpeg" || ext == ".png"
                        || ext == ".bmp" || ext == ".gif" || ext == ".webp")
                    {
                        FileInfo fi = new FileInfo(f);
                        if ((fi.Attributes & FileAttributes.Hidden) != 0) { continue; }
                        if (fi.Length == 0) { continue; }
                        result.Add(f);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取文件夹失败: " + folder + " - " + ex.Message);
            }
            // 数字感知自然排序：2.jpg < 10.jpg
            result.Sort(NaturalCompare);
            return result;
        }

        private static int NaturalCompare(string x, string y)
        {
            string a = Path.GetFileName(x);
            string b = Path.GetFileName(y);
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) { i++; }
                    while (j < b.Length && char.IsDigit(b[j])) { j++; }
                    string nx = a.Substring(si, i - si);
                    string ny = b.Substring(sj, j - sj);
                    int cmp = nx.Length.CompareTo(ny.Length);
                    if (cmp != 0) { return cmp; }
                    cmp = string.CompareOrdinal(nx, ny);
                    if (cmp != 0) { return cmp; }
                }
                else
                {
                    int cmp = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (cmp != 0) { return cmp; }
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }

        /// <summary>
        /// 按显示器宽高比做居中裁剪（fill 语义），生成唯一名临时 jpg。
        /// 适配方式为“保持系统设置”时直接返回原图路径。
        /// v1.1.0 #5/#11：webp 走 WIC 解码；应用 EXIF 方向；唯一文件名防"同路径不重绘"。
        /// </summary>
        private string ApplyFit(MonitorRuntime rt, string src, int index)
        {
            if (rt.Fit != 1) { return src; }
            try
            {
                if (!Directory.Exists(_tempDir)) { Directory.CreateDirectory(_tempDir); }
                int w = rt.Rect.Right - rt.Rect.Left;
                int h = rt.Rect.Bottom - rt.Rect.Top;
                if (w <= 0 || h <= 0) { return src; }
                double targetRatio = (double)w / h;

                using (Bitmap srcBmp = LoadSourceBitmap(src))
                {
                    ApplyExifOrientation(srcBmp);
                    double srcRatio = (double)srcBmp.Width / srcBmp.Height;
                    int cropW, cropH;
                    if (srcRatio > targetRatio)
                    {
                        cropH = srcBmp.Height;
                        cropW = (int)(srcBmp.Height * targetRatio);
                    }
                    else
                    {
                        cropW = srcBmp.Width;
                        cropH = (int)(srcBmp.Width / targetRatio);
                    }
                    cropW = Math.Max(1, Math.Min(cropW, srcBmp.Width));
                    cropH = Math.Max(1, Math.Min(cropH, srcBmp.Height));
                    int x = (srcBmp.Width - cropW) / 2;
                    int y = (srcBmp.Height - cropH) / 2;
                    Rectangle crop = new Rectangle(x, y, cropW, cropH);

                    string outPath = Path.Combine(_tempDir,
                        "screen" + (index + 1) + "_" + DateTime.Now.ToString("yyyyMMddHHmmssfff") + ".jpg");
                    using (Bitmap cropped = srcBmp.Clone(crop, PixelFormat.Format24bppRgb))
                    {
                        ImageCodecInfo codec = GetJpegCodec();
                        if (codec != null)
                        {
                            // v2.4（采纳审查 #12）：using 释放非托管参数块
                            using (EncoderParameters ep = new EncoderParameters(1))
                            {
                                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
                                cropped.Save(outPath, codec, ep);
                            }
                        }
                        else
                        {
                            cropped.Save(outPath, ImageFormat.Jpeg);
                        }
                    }
                    CleanOldTemp(index, outPath);
                    return outPath;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("裁剪失败，使用原图: " + ex.Message);
                return src;
            }
        }

        /// <summary>
        /// v2.3：只重绘"当前这一张"壁纸的文字描述（不换图、不动轮换进度）。
        /// 用途：用户改完字体/颜色/位置后，点「确定」立刻能看到效果——
        /// 此前必须"保存并应用"并等到下一次更换才生效，很容易让人以为功能坏了。
        /// 返回 false 表示没有可用描述（本地图无 EXIF、meta 里也没有），由调用方给出提示。
        /// </summary>
        public bool RepaintCaption(int index)
        {
            try
            {
                if (index < 0 || index >= Monitors.Count) { return false; }
                MonitorRuntime rt = Monitors[index];
                if (!rt.ShowCaption) { return false; }
                if (index > 0 && rt.FollowMain) { return false; }
                string src = rt.LastFile;
                if (string.IsNullOrEmpty(src) || !File.Exists(src)) { return false; }
                // v2.4：以前这里有道 IsBingFile 门直接 return false —— 本地图点「文字描述」永远是"没反应"。
                // 现在与换图走同一条取词链（meta → EXIF → XMP），只要有标题就重绘。
                string caption = BingWallpaper.GetCaptionForFile(src);
                if (caption.Length == 0) { return false; }
                string final = ApplyFit(rt, src, index);
                final = BingWallpaper.RenderCaption(final, index, caption, _tempDir, CaptionStyle,
                    RtW(rt), RtH(rt));
                CleanOldCapTemp(index, final);
                bool ok = SetWallpaperChecked(rt, final);
                if (ok) { Log.Info("显示器 " + (index + 1) + " 已按新样式重绘文字描述"); }
                return ok;
            }
            catch (Exception ex)
            {
                Log.Warn("重绘文字描述失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>统一图片加载：webp 走 WIC（Win10 1809+/Win11 系统内置解码器），其余走 GDI+。</summary>
        private static Bitmap LoadSourceBitmap(string src)
        {
            if (Path.GetExtension(src).ToLowerInvariant() == ".webp")
            {
                try { return DecodeWebP(src); }
                catch
                {
                    // 旧系统无 WebP 解码器：回退 GDI+（同样会失败，由调用方 catch 回退原图并记日志）
                }
            }
            return new Bitmap(src);
        }

        /// <summary>WIC 解码 webp → 内存中转 PNG → GDI+ Bitmap。</summary>
        private static Bitmap DecodeWebP(string src)
        {
            BitmapDecoder dec = BitmapDecoder.Create(
                new Uri(src, UriKind.Absolute),
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);
            BitmapFrame frame = dec.Frames[0];
            using (MemoryStream ms = new MemoryStream())
            {
                PngBitmapEncoder enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(frame));
                enc.Save(ms);
                ms.Position = 0;
                return new Bitmap(ms);
            }
        }

        /// <summary>应用 EXIF 方向（0x0112），避免竖拍照片裁剪后躺倒（v1.1.0 #11）。</summary>
        private static void ApplyExifOrientation(Bitmap bmp)
        {
            try
            {
                PropertyItem[] props = bmp.PropertyItems;
                if (props == null) { return; }
                foreach (PropertyItem p in props)
                {
                    if (p.Id == 0x0112 && p.Value != null && p.Value.Length >= 2)
                    {
                        int orient = BitConverter.ToUInt16(p.Value, 0);
                        switch (orient)
                        {
                            case 2: bmp.RotateFlip(RotateFlipType.RotateNoneFlipX); break;
                            case 3: bmp.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
                            case 4: bmp.RotateFlip(RotateFlipType.Rotate180FlipX); break;
                            case 5: bmp.RotateFlip(RotateFlipType.Rotate90FlipX); break;
                            case 6: bmp.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
                            case 7: bmp.RotateFlip(RotateFlipType.Rotate270FlipX); break;
                            case 8: bmp.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
                        }
                        break;
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 滚动清理裁剪临时图：该屏只保留最新一张（v1.1.0 #8）。
        /// v2.2：只匹配 screenN_数字.jpg，绝不误删同屏的 screenN_cap_*.jpg（带文字产物由 CleanOldCapTemp 负责）。
        /// </summary>
        private void CleanOldTemp(int index, string keep)
        {
            try
            {
                if (!Directory.Exists(_tempDir)) { return; }
                string prefix = "screen" + (index + 1) + "_";
                foreach (string f in Directory.GetFiles(_tempDir, prefix + "*.jpg"))
                {
                    if (string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)) { continue; }
                    if (Path.GetFileName(f).StartsWith(prefix + "cap_", StringComparison.OrdinalIgnoreCase)) { continue; }
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// v2.2 新增：带文字描述的产物 screenN_cap_*.jpg 也按"该屏只留最新一张"滚动清理。
        /// 此前只清理 screenN_*.jpg，cap 文件永远匹配不到 → 每次换图永久留一张，
        /// 实测累积 1324 个文件 / 2.68GB，与本程序"自管目录体积恒定"的承诺冲突。
        /// </summary>
        private void CleanOldCapTemp(int index, string keep)
        {
            try
            {
                if (!Directory.Exists(_tempDir)) { return; }
                string pattern = "screen" + (index + 1) + "_cap_*.jpg";
                foreach (string f in Directory.GetFiles(_tempDir, pattern))
                {
                    if (string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)) { continue; }
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// v2.2 新增：启动时给 temp 目录做一次收敛（把历史遗留的膨胀一次清掉，之后每屏恒定只留最新一张）。
        /// · screenN / screenN_cap 前缀：每组只保留修改时间最新的一张；
        /// · bing_ 缓存：交给 CleanOldBingCache（保留最近 10 张）；
        /// · 其它遗留文件（含旧的 _dl_* 下载中转）：超过 1 天即删。
        /// 只处理 _tempDir 内部，绝不触碰用户的壁纸文件夹。
        /// </summary>
        public void CleanStaleTemp()
        {
            try
            {
                if (!Directory.Exists(_tempDir)) { return; }
                Dictionary<string, List<string>> groups = new Dictionary<string, List<string>>();
                List<string> others = new List<string>();
                foreach (string f in Directory.GetFiles(_tempDir))
                {
                    string name = Path.GetFileName(f);
                    string lower = name.ToLowerInvariant();
                    if (lower.StartsWith("bing_")) { others.Add(f); continue; }   // 由 CleanOldBingCache 管
                    int us = lower.IndexOf("screen", StringComparison.Ordinal);
                    if (us < 0) { others.Add(f); continue; }
                    int sep = lower.IndexOf('_');
                    if (sep < 0) { others.Add(f); continue; }
                    string key = lower.Substring(us, sep + 1 - us);   // screenN_
                    if (!groups.ContainsKey(key)) { groups[key] = new List<string>(); }
                    groups[key].Add(f);
                }

                foreach (KeyValuePair<string, List<string>> kv in groups)
                {
                    List<string> items = kv.Value;
                    items.Sort(delegate(string a, string b)
                    {
                        DateTime ta, tb;
                        try { ta = File.GetLastWriteTime(a); } catch { ta = DateTime.MinValue; }
                        try { tb = File.GetLastWriteTime(b); } catch { tb = DateTime.MinValue; }
                        return tb.CompareTo(ta);   // 新的在前
                    });
                    for (int i = 1; i < items.Count; i++)
                    {
                        try { File.Delete(items[i]); } catch { }
                    }
                }

                DateTime cutoff = DateTime.Now.AddDays(-1);
                foreach (string f in others)
                {
                    try
                    {
                        string name = Path.GetFileName(f).ToLowerInvariant();
                        if (name.StartsWith("bing_")) { continue; }
                        if (File.GetLastWriteTime(f) < cutoff) { File.Delete(f); }
                    }
                    catch { }
                }
                CleanOldBingCache();
            }
            catch { }
        }

        private static ImageCodecInfo GetJpegCodec()
        {
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
            {
                if (c.MimeType == "image/jpeg") { return c; }
            }
            return null;
        }

        public void Shutdown()
        {
            try
            {
                // v2.2：退出前落盘一次顺序轮换进度（正常关闭/关机都会走到这里）
                SaveProgress();
                foreach (FileSystemWatcher w in _watchers.Values)
                {
                    try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
                }
                _watchers.Clear();
                _dw = null;
            }
            catch { }
        }

        /// <summary>
        /// 解析显示器硬件型号（与设备管理器"监视器"节点下的名称一致）。
        /// v1.1.0 实测问题③：界面此前显示 DevicePath 输出通道线路信息（如 \\?\DISPLAY#GWD0161#...），
        /// 用户要求显示与设备管理器一致的硬件型号（如 ViewSonic VX2762-2K-HD / Generic Monitor (ARZOPA)）。
        /// 读取注册表 FriendlyName（回退 DeviceDesc），解析资源字符串 fallback 并填充 %1 参数。
        /// 解析失败返回空串，由调用方兜底显示 DevicePath。
        /// </summary>
        public static string GetMonitorFriendlyName(string devicePath)
        {
            try
            {
                if (string.IsNullOrEmpty(devicePath)) { return string.Empty; }
                // \\?\DISPLAY#GWD0161#5&2ab32792&0&UID516#{e6f07b5f-...}
                string s = devicePath;
                if (s.StartsWith(@"\\?\")) { s = s.Substring(4); }
                // 去掉尾部 #{class-guid}
                int hashIdx = s.LastIndexOf('#');
                if (hashIdx >= 0) { s = s.Substring(0, hashIdx); }
                s = s.Replace('#', '\\');   // DISPLAY\GWD0161\5&2ab32792&0&UID516
                if (!s.StartsWith("DISPLAY\\", StringComparison.OrdinalIgnoreCase)) { return string.Empty; }

                string key = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Enum\" + s;
                object val = Microsoft.Win32.Registry.GetValue(key, "FriendlyName", null);
                if (val == null) { val = Microsoft.Win32.Registry.GetValue(key, "DeviceDesc", null); }
                if (val == null) { return string.Empty; }
                string text = val.ToString();

                // 资源字符串：@driver,#resid;fallback → 取分号后的 fallback 文本
                if (text.StartsWith("@"))
                {
                    int semi = text.IndexOf(';');
                    if (semi >= 0) { text = text.Substring(semi + 1); }
                }

                // fallback 可能形如 "Generic Monitor (%1);(ARZOPA)"：取首段，%1 用末尾括号内容填充
                string tail = string.Empty;
                string head = text;
                int semi2 = text.IndexOf(';');
                if (semi2 >= 0)
                {
                    tail = text.Substring(semi2 + 1).Trim();
                    head = text.Substring(0, semi2);
                }
                string result = head.Trim();
                if (result.Contains("%1"))
                {
                    string fill = string.Empty;
                    int lp = tail.LastIndexOf('(');
                    int rp = tail.LastIndexOf(')');
                    if (lp >= 0 && rp > lp) { fill = tail.Substring(lp + 1, rp - lp - 1).Trim(); }
                    if (fill.Length == 0)
                    {
                        // 兜底：用硬件 ID 的制造商段（如 GWD0161）
                        int d = s.IndexOf('\\');
                        if (d >= 0) { fill = s.Substring(d + 1); }
                        int d2 = fill.IndexOf('\\');
                        if (d2 >= 0) { fill = fill.Substring(0, d2); }
                    }
                    result = result.Replace("%1", fill);
                }
                result = result.Trim();
                return (result.Length > 0) ? result : string.Empty;
            }
            catch { return string.Empty; }
        }
    }
}
