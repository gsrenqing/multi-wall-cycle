using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace MultiWallCycle
{
    /// <summary>单个显示器的配置。列表顺序 = 系统枚举顺序 = 显示器 1,2,3...</summary>
    public class MonitorConfig
    {
        public bool Enabled { get; set; }
        public string Folder { get; set; }
        public int IntervalMinutes { get; set; }
        public int Mode { get; set; }   // 0=顺序轮换, 1=随机
        public int Fit { get; set; }    // 0=保持系统设置, 1=裁剪填充
        /// <summary>显示器设备路径（DevicePath），拔插后据此正确对应配置（v1.1.0 新增）。</summary>
        public string DevicePath { get; set; }
        /// <summary>
        /// 使用必应每日壁纸来源（v1.1.2）：与本地文件夹可复选。
        /// v2.2：不再强制 24 小时间隔——仅勾选且无本地文件夹时，按设定间隔在必应"最近 8 天"的
        /// 壁纸里轮换（顺序＝时间倒序，或随机）。
        /// </summary>
        public bool UseBing { get; set; }
        /// <summary>如有文字描述请显示（v1.1.2）：Bing 来源能对应上的标题+地点+摄影师叠加在图片右下方。</summary>
        public bool ShowCaption { get; set; }
        /// <summary>
        /// v2.0：跟随主显示器。勾选后本屏的壁纸来源、更换间隔、更换模式、适配方式、
        /// 必应每日壁纸、文字描述等全部与主显示器（显示器 1）保持一致，且每次随主显示器
        /// 一同更换为同一张图（各自按本屏分辨率适配）。仅对显示器 2 及以后有效。
        /// </summary>
        public bool FollowMain { get; set; }
        /// <summary>
        /// v2.2：顺序轮换进度（"修改时间最旧/最新两头交叉"序列中的位置，-1 = 还没开始）。
        /// 每次成功换图后写回，退出/关机时再写一次，保证重启后接着上次的位置轮换，不回到第 1 张。
        /// </summary>
        public int SeqPos { get; set; }

        public MonitorConfig()
        {
            Enabled = true;
            Folder = string.Empty;
            IntervalMinutes = 30;
            Mode = 0;
            Fit = 0;
            DevicePath = string.Empty;
            UseBing = false;
            ShowCaption = false;
            FollowMain = false;
            SeqPos = -1;
        }
    }

    /// <summary>
    /// 文字描述叠加的全局样式（v1.1.2，每屏仅"是否显示"独立，样式全局一份）。
    /// 颜色以 "#RRGGBB" 字符串存储（JavaScriptSerializer 对 Color 结构不可靠）。
    /// </summary>
    public class CaptionStyle
    {
        public string FontFamily { get; set; }   // 系统字体族名
        public int Style { get; set; }           // 0=常规 1=加粗 2=斜体
        public int FontSize { get; set; }        // 8-80（像素）
        public string FontColor { get; set; }    // 文字颜色
        public int Position { get; set; }        // 0=左下 1=下方居中 2=右下 3=左上 4=上部居中 5=右上
        public bool Shadow { get; set; }         // 应用阴影效果
        public int ShadowSize { get; set; }      // 1-8（像素偏移）
        public string ShadowColor { get; set; }  // 阴影颜色

        public CaptionStyle()
        {
            FontFamily = "Microsoft YaHei UI";
            Style = 0;
            FontSize = 18;
            FontColor = "#FFFFFF";
            Position = 2;     // 右下角（默认，贴近 Bing 原版）
            Shadow = true;
            ShadowSize = 4;
            ShadowColor = "#000000";
        }
    }

    public class AppConfig
    {
        public bool EnableAll { get; set; }
        public bool AutoStart { get; set; }
        public List<MonitorConfig> Monitors { get; set; }
        /// <summary>上次 Bing 壁纸清理提醒日期（yyyy-MM-dd，v1.1.2；90 天提醒一次）。</summary>
        public string BingCleanRemind { get; set; }
        /// <summary>文字描述叠加样式（v1.1.2，全局一份）。</summary>
        public CaptionStyle Caption { get; set; }
        /// <summary>
        /// v2.3：界面语言。"" = 尚未判定（首次启动按系统语言自动选择），"zh" = 中文界面，"en" = 英文界面。
        /// 用户在托盘菜单 / 主界面切换后即固定写入，之后不再跟随系统语言变化。
        /// </summary>
        public string Lang { get; set; }

        public AppConfig()
        {
            // v2.3 第二轮反馈③：轮换与开机自启改为恒定默认（界面上不再有开关）
            EnableAll = true;
            AutoStart = true;
            Monitors = new List<MonitorConfig>();
            BingCleanRemind = string.Empty;
            Caption = new CaptionStyle();
            Lang = string.Empty;
        }
    }

    public static class ConfigIO
    {
        private static string _configDir;

        /// <summary>
        /// 配置存储目录（v1.1.1 绿色便携混合方案）：
        /// ① exe 同目录已有 config.json → 跟随 exe（绿色便携：exe+config.json 一起拷贝即配置随身走）；
        /// ② 否则 → %LOCALAPPDATA%\MultiWallCycle（v2.3 起改用英文目录名，避免个别系统/工具对中文路径不兼容）。
        ///    首次运行时若新目录里还没有配置、而旧目录 %LOCALAPPDATA%\多屏幻灯壁纸 有，
        ///    自动把 config.json / bing_meta.json 复制过来（一次性迁移；旧目录保留，卸载时一并清理）。
        /// </summary>
        public static string ConfigDir
        {
            get
            {
                if (_configDir == null)
                {
                    string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                    if (File.Exists(Path.Combine(exeDir, "config.json")))
                    {
                        _configDir = exeDir;
                    }
                    else
                    {
                        MigrateLegacyDataDir();
                        _configDir = UserDataDir;
                    }
                }
                return _configDir;
            }
        }

        private static bool _legacyMigrated;

        /// <summary>v2.3：把旧版中文目录里的配置一次性复制到新的英文目录（不移动、不删除旧文件）。</summary>
        private static void MigrateLegacyDataDir()
        {
            if (_legacyMigrated) { return; }
            _legacyMigrated = true;
            try
            {
                string newDir = UserDataDir;
                string legacy = LegacyUserDataDir;
                if (string.Equals(newDir.TrimEnd('\\'), legacy.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { return; }
                if (!Directory.Exists(legacy)) { return; }
                Directory.CreateDirectory(newDir);

                string newCfg = Path.Combine(newDir, "config.json");
                string oldCfg = Path.Combine(legacy, "config.json");
                if (!File.Exists(newCfg) && File.Exists(oldCfg))
                {
                    File.Copy(oldCfg, newCfg, false);
                }
                string newMeta = Path.Combine(newDir, "bing_meta.json");
                string oldMeta = Path.Combine(legacy, "bing_meta.json");
                if (!File.Exists(newMeta) && File.Exists(oldMeta))
                {
                    File.Copy(oldMeta, newMeta, false);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("迁移旧数据目录失败（不影响运行，可继续使用旧配置目录）: " + ex.Message);
            }
        }

        public static string ConfigPath
        {
            get { return Path.Combine(ConfigDir, "config.json"); }
        }

        /// <summary>本机统一数据目录 %LOCALAPPDATA%\MultiWallCycle（配置/必应描述/temp 缓存都落在这里，卸载需整体清理）。</summary>
        public static string UserDataDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MultiWallCycle");
            }
        }

        /// <summary>旧版（v2.0~v2.2）数据目录 %LOCALAPPDATA%\多屏幻灯壁纸，v2.3 仅用于一次性迁移与卸载清理。</summary>
        public static string LegacyUserDataDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "多屏幻灯壁纸");
            }
        }

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    JavaScriptSerializer js = new JavaScriptSerializer();
                    AppConfig cfg = js.Deserialize<AppConfig>(File.ReadAllText(ConfigPath));
                    if (cfg == null) { cfg = new AppConfig(); }
                    if (cfg.Monitors == null) { cfg.Monitors = new List<MonitorConfig>(); }
                    // v1.1.2：旧配置无 Caption 字段 → 补默认样式
                    if (cfg.Caption == null) { cfg.Caption = new CaptionStyle(); }
                    if (cfg.BingCleanRemind == null) { cfg.BingCleanRemind = string.Empty; }
                    // v2.3：旧配置无 Lang 字段 → 留空，交由启动时按系统语言判定
                    if (cfg.Lang == null) { cfg.Lang = string.Empty; }
                    if (cfg.Lang.Length > 0
                        && !string.Equals(cfg.Lang, Lang.Zh, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(cfg.Lang, Lang.En, StringComparison.OrdinalIgnoreCase))
                    {
                        cfg.Lang = string.Empty;
                    }
                    foreach (MonitorConfig m in cfg.Monitors)
                    {
                        if (m == null) { continue; }
                        if (m.Folder == null) { m.Folder = string.Empty; }
                        if (m.DevicePath == null) { m.DevicePath = string.Empty; }
                        if (m.IntervalMinutes < 1) { m.IntervalMinutes = 1; }
                        if (m.IntervalMinutes > 1440) { m.IntervalMinutes = 1440; }
                        if (m.SeqPos < -1) { m.SeqPos = -1; }
                    }
                    return cfg;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("配置文件解析失败: " + ex.Message);
                // 损坏文件改名保留，不直接丢弃（v1.1.0 #9）
                try
                {
                    if (File.Exists(ConfigPath))
                    {
                        File.Move(ConfigPath, ConfigPath + ".bak");
                        Log.Warn("已把损坏的 config.json 改名为 config.json.bak");
                    }
                }
                catch { }
            }
            return new AppConfig();
        }

        public static void Save(AppConfig cfg)
        {
            try
            {
                SaveTo(ConfigPath, cfg);
            }
            catch (Exception ex)
            {
                Log.Error("保存配置失败(" + ConfigPath + "): " + ex.Message);
                // 只读/不可写介质回退：exe 目录写不进时改存 %LOCALAPPDATA%\MultiWallCycle（本机统一），不因介质只读而丢失配置
                string appData = UserDataDir;
                if (!string.Equals(ConfigDir, appData, StringComparison.OrdinalIgnoreCase))
                {
                    _configDir = appData;
                    try
                    {
                        SaveTo(ConfigPath, cfg);
                        Log.Warn("exe 目录不可写，配置已改存 %LOCALAPPDATA%\\MultiWallCycle\\config.json");
                    }
                    catch (Exception ex2)
                    {
                        Log.Error("保存配置到 LOCALAPPDATA 也失败: " + ex2.Message);
                        _configDir = null; // 复位，下次按真实情况重新解析
                    }
                }
            }
        }

        private static void SaveTo(string path, AppConfig cfg)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            // 原子写：先写 .tmp 再替换，避免写到一半崩溃损坏配置（v1.1.0 #9）
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, js.Serialize(cfg));
            if (File.Exists(path))
            {
                File.Replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }

        /// <summary>
        /// v2.2：只把各屏的顺序轮换进度（SeqPos）写回配置文件，其余字段原样保留。
        /// 换图后立即调用（每屏每个间隔一次，1KB 文件的原子写，开销可忽略），
        /// 保证程序崩溃/断电后也能从上次位置继续，不回到第 1 张。
        /// 匹配规则：先按 DevicePath 精确匹配，再按下标兜底（与 RebuildMonitors 的绑定顺序一致）。
        /// </summary>
        public static void UpdateSeqPositions(List<string> devicePaths, List<int> positions)
        {
            try
            {
                if (devicePaths == null || positions == null) { return; }
                if (!File.Exists(ConfigPath)) { return; }
                AppConfig cfg = Load();
                if (cfg == null || cfg.Monitors == null) { return; }
                bool changed = false;
                for (int i = 0; i < cfg.Monitors.Count; i++)
                {
                    MonitorConfig mc = cfg.Monitors[i];
                    if (mc == null) { continue; }
                    int val = -1;
                    bool found = false;
                    string dp = mc.DevicePath ?? string.Empty;
                    if (dp.Length > 0)
                    {
                        for (int j = 0; j < devicePaths.Count && j < positions.Count; j++)
                        {
                            if (string.Equals(devicePaths[j] ?? string.Empty, dp, StringComparison.OrdinalIgnoreCase))
                            {
                                val = positions[j];
                                found = true;
                                break;
                            }
                        }
                    }
                    if (!found && i < positions.Count)
                    {
                        val = positions[i];
                        found = true;
                    }
                    if (found && mc.SeqPos != val) { mc.SeqPos = val; changed = true; }
                }
                // v2.4（采纳审查 #15）：位置没变就不落盘。原写法每次换图都重写 config.json，
                // 多屏同步轮换时同一秒内连写几次，白耗磁盘也让文件的修改时间乱跳。
                if (changed) { Save(cfg); }
            }
            catch (Exception ex)
            {
                Log.Warn("保存轮换进度失败（不影响轮换）: " + ex.Message);
            }
        }
    }
}
