using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;   // v2.3 第二轮：CueBanner 需要 Control

namespace MultiWallCycle
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>Win32 兜底模式下单个活动显示器的完整信息。</summary>
    public struct Win32MonitorInfo
    {
        public string Adapter;    // "\\.\DISPLAY1"
        public string MonitorId;  // "\\.\DISPLAY1\Monitor0"（SetWallpaper 实测真生效的 monitorID）
        public string ModelName;  // "ViewSonic VX2762-2K-HD"（设备管理器型号，EnumDisplayDevices 子设备 DeviceString）
        public RECT Rect;         // 虚拟坐标
    }

    /// <summary>
    /// Win32 显示器枚举（v1.1.2 第三轮 COM 兜底）：
    /// IDesktopWallpaper 的 COM 服务在拔插显示器后可能进入持续 E_FAIL 状态（系统级问题，
    /// 独立进程同样失败，重启程序无效）。EnumDisplayMonitors/GetMonitorInfo 是 Win32 用户态
    /// API，不依赖该 COM 服务，始终可用——用于拿"当前真实活动显示器数量与虚拟坐标 RECT"。
    /// v1.1.2 第四轮修复（#15/#14）：适配器名（\\.\DISPLAY1）SetWallpaper 是"假成功"（COM 记账
    /// 但不渲染，桌面不变）；必须用子设备监视器名（\\.\DISPLAY1\Monitor0）才真生效。
    /// 同时 EnumDisplayDevices 子设备的 DeviceString 即真实硬件型号（解决 #14 兜底型号显示）。
    /// </summary>
    public static class Win32Monitors
    {
        private delegate bool EnumMonProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, EnumMonProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        private const int DISPLAY_DEVICE_ACTIVE = 0x00000001;

        /// <summary>
        /// 枚举当前活动显示器：适配器名 + 子设备监视器名（\\.\DISPLAY1\Monitor0）+ 真实型号 + 虚拟坐标 RECT。
        /// 与 IDesktopWallpaper 的 SetWallpaper 兼容（子设备名实测真生效，适配器名仅假成功）。
        /// </summary>
        public static bool EnumMonitors(out List<Win32MonitorInfo> monitors)
        {
            List<Win32MonitorInfo> tmp = new List<Win32MonitorInfo>();
            monitors = tmp;
            try
            {
                // 第一步：EnumDisplayMonitors 拿活动屏（适配器名 + RECT，按拓扑顺序）
                List<string> adapters = new List<string>();
                List<RECT> rects = new List<RECT>();
                bool ok = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr d)
                {
                    MONITORINFOEX mi = new MONITORINFOEX();
                    mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                    if (GetMonitorInfo(hMon, ref mi))
                    {
                        adapters.Add(mi.szDevice);
                        rects.Add(mi.rcMonitor);
                    }
                    return true;
                }, IntPtr.Zero);
                if (!ok) { return false; }

                // 第二步：EnumDisplayDevices 取每个活动适配器的子设备监视器名（\\.\DISPLAY1\Monitor0）与真实型号
                for (int i = 0; i < adapters.Count; i++)
                {
                    Win32MonitorInfo info = new Win32MonitorInfo();
                    info.Adapter = adapters[i];
                    info.Rect = rects[i];
                    DISPLAY_DEVICE mon = new DISPLAY_DEVICE();
                    mon.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                    // 子设备编号从 0 开始：活动显示器对应的监视器节点（\\.\DISPLAY1\Monitor0）
                    for (uint j = 0; j < 8; j++)
                    {
                        if (!EnumDisplayDevices(adapters[i], j, ref mon, 0)) { break; }
                        if ((mon.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0)
                        {
                            info.MonitorId = mon.DeviceName;       // "\\.\DISPLAY1\Monitor0"
                            info.ModelName = mon.DeviceString;     // "ViewSonic VX2762-2K-HD"
                            break;
                        }
                    }
                    if (info.MonitorId.Length == 0) { info.MonitorId = info.Adapter; }
                    tmp.Add(info);
                }
                return tmp.Count > 0;
            }
            catch (Exception ex)
            {
                Log.Warn("Win32 显示器枚举失败: " + ex.Message);
                tmp.Clear();
                return false;
            }
        }

        /// <summary>
        /// 枚举当前活动显示器（设备名 + 虚拟坐标 RECT），返回是否成功。
        /// 仅作 COM 枚举失效时的兜底标识；SetWallpaper 请使用 MonitorId（子设备名）而非适配器名。
        /// </summary>
        public static bool EnumMonitors(out List<string> devices, out List<RECT> rects)
        {
            List<string> tmpDev = new List<string>();
            List<RECT> tmpRect = new List<RECT>();
            devices = tmpDev;
            rects = tmpRect;
            try
            {
                bool ok = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr d)
                {
                    MONITORINFOEX mi = new MONITORINFOEX();
                    mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                    if (GetMonitorInfo(hMon, ref mi))
                    {
                        tmpDev.Add(mi.szDevice);
                        tmpRect.Add(mi.rcMonitor);
                    }
                    return true;
                }, IntPtr.Zero);
                return ok;
            }
            catch (Exception ex)
            {
                Log.Warn("Win32 显示器枚举失败: " + ex.Message);
                tmpDev.Clear();
                tmpRect.Clear();
                return false;
            }
        }
    }

    /// <summary>CCD 枚举得到的单个活动显示器信息。</summary>
    public struct CcdMonitorInfo
    {
        public string DevicePath;    // \\?\DISPLAY#VSCC63F#...（完整路径，SetWallpaper 真生效所需）
        public string FriendlyName;  // 设备管理器真实型号，如 ViewSonic VX2762-2K-HD
        public RECT Rect;            // 桌面虚拟坐标（已按 rotation 修正）
        public bool Available;
    }

    /// <summary>
    /// v1.1.3：CCD（QueryDisplayConfig / DisplayConfigGetDeviceInfo）显示器枚举。
    /// 相比 IDesktopWallpaper COM 枚举的三点决定性优势（2026-11-01 实测，见 v1.1.3\ccdtest）：
    /// ① QDC_ONLY_ACTIVE_PATHS 只返回活动路径，天然排除"已断开但被系统记住"的占位显示器
    ///   （Default_Monitor：COM 枚举会计入，RECT 为 0x0，GetMonitorRECT 返回 E_FAIL，
    ///    这正是界面多出"屏幕3"的原因）；
    /// ② 不依赖壁纸 COM 服务，拔插显示器后不会进入持续 E_FAIL 状态，枚举不再 1/2 屏振荡；
    /// ③ monitorDevicePath 即完整 DevicePath（与 COM 枚举逐字符一致），可直接用于 SetWallpaper；
    ///   monitorFriendlyDeviceName 即设备管理器中的真实型号。
    /// 实测对照：CCD=2 屏、COM=3 屏、Win32=2 屏；CCD 与 COM 对真实两屏的 DevicePath/RECT 一致。
    /// </summary>
    public static class CcdMonitors
    {
        private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

        // DisplayConfigGetDeviceInfo 对 DISPLAYCONFIG_TARGET_DEVICE_NAME 的 size 要求在不同
        // Windows 版本存在差异（实测 404 在部分环境返回 ERROR_INVALID_PARAMETER 87），
        // 因此依次尝试，取第一个成功的。
        private static readonly int[] NameSizes = new int[] { 404, 420, 436, 412, 72, 148 };

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTL { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL { public uint Numerator, Denominator; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_2DREGION { public uint cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
        {
            public ulong pixelRate;
            public DISPLAYCONFIG_RATIONAL hSyncFreq;
            public DISPLAYCONFIG_RATIONAL vSyncFreq;
            public DISPLAYCONFIG_2DREGION activeSize;
            public DISPLAYCONFIG_2DREGION totalSize;
            public uint videoStandard;
            public uint scanLineOrdering;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_TARGET_MODE { public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo; }

        // 审查报告 #4 认为这里字段顺序错了（wingdi.h 里 POINTL position 打头）。
        // 2026-11-13 实测：按报告改成 position 在前之后，本机双屏（2560x1440 主屏 + 1080x1920 竖屏）
        // 只能枚举出 1 个屏，属于"改对了理论、改坏了实际"——编号与型号识别是历轮踩坑才稳定的，
        // 不拿它冒险。故保持 v2.3 原样，只在此留档结论（详见 v2.4 变更说明第五节）。
        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SOURCE_MODE
        {
            public uint width;
            public uint height;
            public uint pixelFormat;
            public POINTL position;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct MODE_UNION
        {
            [FieldOffset(0)] public DISPLAYCONFIG_TARGET_MODE targetMode;
            [FieldOffset(0)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            public MODE_UNION mode;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathInfoElements, out uint numModeInfoElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint numPathInfoElements,
            [In, Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray, ref uint numModeInfoElements,
            [In, Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        private static extern int DisplayConfigGetDeviceInfo(IntPtr requestPacket);

        /// <summary>枚举当前活动显示器，失败返回 false。</summary>
        public static bool EnumMonitors(out List<CcdMonitorInfo> monitors)
        {
            monitors = new List<CcdMonitorInfo>();
            try
            {
                uint numPaths, numModes;
                int err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out numPaths, out numModes);
                if (err != 0)
                {
                    Log.Warn("CCD GetDisplayConfigBufferSizes 失败: 0x" + err.ToString("X8"));
                    return false;
                }

                DISPLAYCONFIG_PATH_INFO[] paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
                DISPLAYCONFIG_MODE_INFO[] modes = new DISPLAYCONFIG_MODE_INFO[numModes];

                err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
                if (err == 122)   // ERROR_INSUFFICIENT_BUFFER：按新尺寸重试一次
                {
                    paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
                    modes = new DISPLAYCONFIG_MODE_INFO[numModes];
                    err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
                }
                if (err != 0)
                {
                    Log.Warn("CCD QueryDisplayConfig 失败: 0x" + err.ToString("X8"));
                    return false;
                }

                for (int i = 0; i < numPaths; i++)
                {
                    DISPLAYCONFIG_PATH_INFO path = paths[i];

                    string friendly, devPath;
                    TryGetTargetName(path, out friendly, out devPath);

                    CcdMonitorInfo info = new CcdMonitorInfo();
                    info.DevicePath = devPath;
                    info.FriendlyName = friendly;
                    info.Available = path.targetInfo.targetAvailable;

                    uint sIdx = path.sourceInfo.modeInfoIdx;
                    if (sIdx < numModes)
                    {
                        DISPLAYCONFIG_SOURCE_MODE src = modes[sIdx].mode.sourceMode;
                        uint w = src.width, h = src.height;
                        // rotation: 1=identity 2=90 3=180 4=270；旋转 90/270 时桌面矩形宽高互换
                        if (path.targetInfo.rotation == 2 || path.targetInfo.rotation == 4)
                        {
                            uint t = w; w = h; h = t;
                        }
                        RECT r = new RECT();
                        r.Left = src.position.x;
                        r.Top = src.position.y;
                        r.Right = src.position.x + (int)w;
                        r.Bottom = src.position.y + (int)h;
                        info.Rect = r;
                    }
                    monitors.Add(info);
                }
                return monitors.Count > 0;
            }
            catch (Exception ex)
            {
                Log.Warn("CCD 显示器枚举失败: " + ex.Message);
                monitors.Clear();
                return false;
            }
        }

        /// <summary>
        /// 取指定 target 的友好名与设备路径。手动分配内存构造 header，避免 marshaller 干扰；
        /// size 依次尝试候选值，取第一个成功的。
        ///
        /// 2026-11-01 实测教训（v1.1.3 用户报告"两屏共用壁纸/型号乱码"的根因）：
        /// 本机 DISPLAYCONFIG_TARGET_DEVICE_NAME 要求 420 字节——header(20) 之后多了 16 字节
        /// 新字段，monitorDevicePath 在偏移 164 而非标准 148。用固定偏移读会得到
        /// 「\0 前缀 + 真路径 + \0 填充」；注意 Marshal.PtrToStringUni(ptr, len) 带 length 参数
        /// 时不遇 \0 截断，这些脏 monitorID 进入 SetWallpaper 后 COM 匹配失败、退化为
        /// 【全局设置】（两屏同图、后设者覆盖先设者），GetWallpaper 读回也返回全局值（假成功）。
        /// 故改为在缓冲区内扫描定位 "\\?\DISPLAY" 作为路径起点；friendlyName（64 wchar）
        /// 紧贴其前 128 字节，一并修正。
        /// </summary>
        private static void TryGetTargetName(DISPLAYCONFIG_PATH_INFO path, out string friendly, out string devPath)
        {
            friendly = string.Empty;
            devPath = string.Empty;
            long luid = ((long)path.targetInfo.adapterId.HighPart << 32) | (uint)path.targetInfo.adapterId.LowPart;

            foreach (int size in NameSizes)
            {
                IntPtr p = Marshal.AllocHGlobal(size);
                try
                {
                    for (int k = 0; k < size; k++) { Marshal.WriteByte(p, k, 0); }
                    Marshal.WriteInt32(p, 0, (int)DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME);
                    Marshal.WriteInt32(p, 4, size);
                    Marshal.WriteInt64(p, 8, luid);
                    Marshal.WriteInt32(p, 16, (int)path.targetInfo.id);

                    if (DisplayConfigGetDeviceInfo(p) != 0) { continue; }

                    // 注意转义："\\\\?\\DISPLAY" 即 \\?\DISPLAY（两个反斜杠开头）
                    int pathOff = LocateAscii(p, size, "\\\\?\\DISPLAY");
                    if (pathOff < 0) { continue; }   // 该 size 布局下未定位到路径，试下一个 size
                    devPath = Marshal.PtrToStringUni(p + pathOff) ?? string.Empty;
                    if (pathOff >= 128)
                    {
                        string n = (Marshal.PtrToStringUni(p + pathOff - 128) ?? string.Empty).Trim();
                        if (LooksLikeName(n)) { friendly = n; }
                    }
                    if (devPath.Length > 0) { return; }
                }
                finally
                {
                    Marshal.FreeHGlobal(p);
                }
            }
        }

        /// <summary>
        /// 在非托管缓冲区内按 wchar 对齐扫描 ASCII 子串，返回其字节偏移；未找到返回 -1。
        /// </summary>
        private static int LocateAscii(IntPtr p, int size, string needle)
        {
            for (int off = 20; off + needle.Length * 2 <= size; off += 2)
            {
                bool match = true;
                for (int k = 0; k < needle.Length; k++)
                {
                    if (Marshal.ReadInt16(p, off + k * 2) != needle[k]) { match = false; break; }
                }
                if (match) { return off; }
            }
            return -1;
        }

        /// <summary>友好名是否像正常文本（排除未初始化内存里的控制字符/私用区字符）。</summary>
        private static bool LooksLikeName(string s)
        {
            if (string.IsNullOrEmpty(s)) { return false; }
            foreach (char c in s)
            {
                if (c == '\0') { break; }
                if (c < 0x20) { return false; }
                if (c >= 0xE000 && c <= 0xF8FF) { return false; }
            }
            return true;
        }
    }

    /// <summary>
    /// IDesktopWallpaper COM 接口（Windows 8+），用于多显示器独立壁纸。
    /// GUID: {B92B56A9-8B55-4E14-9A89-0199BBB6F93B}
    /// 方法顺序必须严格按 vtable 排列（vtbl 3 = SetWallpaper，Enable 在最后），不能删减或调换。
    /// </summary>
    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID,
                          [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        void GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID,
                          [MarshalAs(UnmanagedType.LPWStr)] out string wallpaper);
        void GetMonitorDevicePathAt(uint monitorIndex,
                                    [MarshalAs(UnmanagedType.LPWStr)] out string monitorID);
        void GetMonitorDevicePathCount(out uint count);
        void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID, out RECT rect);
        void SetBackgroundColor(uint color);
        void GetBackgroundColor(out uint color);
        void SetPosition(int position);
        void GetPosition(out int position);
        void SetSlideshow(IntPtr pItems);
        void GetSlideshow(out IntPtr pItems);
        void SetSlideshowOptions(int options, uint slideshowTick);
        void GetSlideshowOptions(out int options, out uint slideshowTick);
        void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string monitorID, int direction);
        int GetStatus();
        void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
    }

    public static class DesktopWallpaperFactory
    {
        // CLSID: {C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD}
        private static readonly Guid Clsid = new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD");

        public static IDesktopWallpaper Create()
        {
            Type t = Type.GetTypeFromCLSID(Clsid);
            if (t == null)
            {
                throw new InvalidOperationException("无法解析 IDesktopWallpaper COM 类型");
            }
            return (IDesktopWallpaper)Activator.CreateInstance(t);
        }
    }

    /// <summary>
    /// v2.3 第二轮：只读文本框的"空值提示"（灰字水印）。.NET Framework 4.8 的 TextBox 没有
    /// PlaceholderText，只能靠 EM_SETCUEBANNER。用于壁纸文件夹被清空时提示"已不使用本地文件夹"。
    /// 发送失败静默忽略（空框本身也已表达"没有路径"）。
    /// </summary>
    public static class CueBanner
    {
        private const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public static void Set(Control ctl, string text)
        {
            try
            {
                if (ctl == null || ctl.IsDisposed) { return; }
                IntPtr h = ctl.Handle;   // 强制创建句柄，否则 SendMessage 打空
                // v2.4（采纳审查 #19）：wParam 必须是 1（ECC_INITIALIZE_ON_FOCUS），
                // 传 0 的提示文字在控件获得焦点时会被清掉，之后再也不会自动回来。
                SendMessage(h, EM_SETCUEBANNER, new IntPtr(1), text ?? string.Empty);
            }
            catch { }
        }
    }

    /// <summary>
    /// 进程间消息（v1.1.0）：程序已运行且窗口已退托盘时，再次双击启动，
    /// 通过全局广播消息通知已运行实例把设置窗口显示到前台。
    /// </summary>
    public static class AppMessages
    {
        private const int HWND_BROADCAST = 0xFFFF;

        public static readonly int WM_SHOW_APP = RegisterWindowMessage("MultiScreenSlideshow.ShowWindow_v110");

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public static void NotifyExistingInstance()
        {
            try
            {
                PostMessage((IntPtr)HWND_BROADCAST, WM_SHOW_APP, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                // 通知失败不影响流程：新实例直接退出，用户可从托盘打开设置
            }
        }
    }
}
