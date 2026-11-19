using System;
using System.Collections.Generic;
using System.Globalization;

namespace MultiWallCycle
{
    /// <summary>
    /// v2.3：界面语言层（中文 / English）。
    /// - 所有界面可见文字统一走 Lang.T("key")，中英各一份，避免散落的硬编码字符串；
    /// - 首次启动按系统 UI 语言判定（中文 → 中文界面，其余 → 英文界面），之后以用户选择为准；
    /// - 语言状态存放在 config.json 的 Lang 字段（"" = 尚未判定，zh / en = 已选定）。
    /// </summary>
    public static class Lang
    {
        public const string Zh = "zh";
        public const string En = "en";

        private static string _current = Zh;

        /// <summary>当前界面语言："zh" 或 "en"。</summary>
        public static string Current { get { return _current; } }

        public static bool IsEn { get { return string.Equals(_current, En, StringComparison.OrdinalIgnoreCase); } }

        public static void Set(string lang)
        {
            _current = string.Equals(lang, En, StringComparison.OrdinalIgnoreCase) ? En : Zh;
        }

        /// <summary>
        /// 首次启动的语言判定：系统 UI 语言是中文（zh-CN / zh-Hans / zh-TW…）→ 中文界面，
        /// 其余一律英文界面。判定异常时按英文处理（非中文系统上不出现看不懂的中文界面）。
        /// </summary>
        public static string DetectSystemLanguage()
        {
            try
            {
                CultureInfo ui = CultureInfo.CurrentUICulture;
                if (ui != null && IsChinese(ui)) { return Zh; }
            }
            catch { }
            try
            {
                CultureInfo installed = CultureInfo.InstalledUICulture;
                if (installed != null && IsChinese(installed)) { return Zh; }
            }
            catch { }
            return En;
        }

        private static bool IsChinese(CultureInfo ci)
        {
            try
            {
                if (ci.TwoLetterISOLanguageName != null
                    && ci.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                // 少数环境给的是完整名称（如 zh-Hans-CN / zh-CN）
                return ci.Name != null && ci.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            return false;
        }

        /// <summary>配置里的 Lang 为空 → 按系统语言判定；否则用配置值。</summary>
        public static string Resolve(string configured)
        {
            if (string.IsNullOrEmpty(configured)) { return DetectSystemLanguage(); }
            return string.Equals(configured, En, StringComparison.OrdinalIgnoreCase) ? En : Zh;
        }

        /// <summary>取当前语言的界面文字；缺键时原样返回键名，便于开发期发现漏翻。</summary>
        public static string T(string key)
        {
            string[] pair;
            if (Map.TryGetValue(key, out pair))
            {
                return IsEn ? pair[1] : pair[0];
            }
            return key;
        }

        /// <summary>带占位符的界面文字（{0} / {1} …）。</summary>
        public static string T(string key, params object[] args)
        {
            try { return string.Format(T(key), args); }
            catch { return T(key); }
        }

        // [0] = 中文，[1] = English
        private static readonly Dictionary<string, string[]> Map = new Dictionary<string, string[]>
        {
            // ---- 通用 ----
            { "app.name", new[] { "多屏幻灯壁纸", "MultiWallCycle" } },
            { "app.title", new[] { "多屏幻灯壁纸 - 设置", "MultiWallCycle - Settings" } },
            { "btn.ok", new[] { "确定", "OK" } },
            { "btn.cancel", new[] { "取消", "Cancel" } },

            // ---- 顶部 ----
            { "chk.enable", new[] { "启用壁纸轮换", "Enable wallpaper rotation" } },
            { "chk.autostart", new[] { "开机自动启动", "Start with Windows" } },
            { "btn.rescan", new[] { "重新扫描显示器", "Rescan monitors" } },
            { "status.on", new[] { "共{0}屏｜轮换中{1}屏｜下次 {2}", "{0} monitors | {1} rotating | next {2}" } },
            { "status.off", new[] { "共{0}屏｜已暂停", "{0} monitors | paused" } },

            // ---- 底部 ----
            { "btn.save", new[] { "保存并应用", "Save && Apply" } },
            { "btn.applynow", new[] { "立即全部更换", "Change All Now" } },
            { "btn.uninstall", new[] { "卸载", "Uninstall" } },
            { "btn.lang", new[] { "English", "中文" } },   // 中文界面显示 English，英文界面显示 中文

            // ---- 托盘 ----
            { "tray.open", new[] { "打开设置", "Open settings" } },
            { "tray.now", new[] { "立即全部更换", "Change all now" } },
            { "tray.pause", new[] { "暂停全部", "Pause all" } },
            { "tray.resume", new[] { "继续全部", "Resume all" } },
            { "tray.about", new[] { "关于", "About" } },
            { "tray.exit", new[] { "退出", "Exit" } },
            { "tray.started", new[] { "已启动，可在托盘图标右键菜单暂停/继续。", "Started. Right-click the tray icon to pause or resume." } },
            { "tray.rescanned", new[] { "显示器已重新扫描，按上次生效配置绑定。", "Monitors rescanned and bound to the last applied settings." } },
            { "tray.layoutchanged", new[] { "显示器连接情况已变化，配置已重新绑定。", "Monitor connections changed; settings were rebound." } },
            { "tray.paused", new[] { "已暂停轮换。", "Rotation paused." } },
            { "tray.resumed", new[] { "已继续轮换。", "Rotation resumed." } },
            { "tray.closed", new[] { "未保存的修改已放弃，程序仍在托盘运行；右键图标可彻底退出。", "Unsaved changes discarded. The program is still running in the tray; right-click the icon to exit." } },

            // ---- 显示器卡片 ----
            { "card.title", new[] { "显示器 {0}", "Monitor {0}" } },
            { "card.model", new[] { "型号：{0}", "Model: {0}" } },
            { "card.folder", new[] { "壁纸文件夹：", "Wallpaper folder:" } },
            { "card.browse", new[] { "选择…", "Browse…" } },
            // v2.3 第二轮反馈②：壁纸文件夹新增"不使用本地文件夹"选项（清空路径，改用必应取图）
            { "card.nolocal", new[] { "不使用本地文件夹", "No local folder" } },
            { "card.nolocal.cue", new[] { "（不使用本地文件夹，仅用必应每日壁纸）", "(no local folder, Bing daily only)" } },
            { "card.interval", new[] { "更换间隔（分钟）：", "Interval (min):" } },
            { "card.mode", new[] { "更换模式：", "Rotation:" } },
            { "card.mode.seq", new[] { "顺序轮换", "Sequential" } },
            { "card.mode.random", new[] { "随机", "Random" } },
            { "card.fit", new[] { "适配方式：", "Fit:" } },
            { "card.fit.system", new[] { "保持系统设置", "System default" } },
            { "card.fit.crop", new[] { "裁剪填充", "Fill (crop)" } },
            { "card.enable", new[] { "启用此屏", "Enable this screen" } },
            { "card.follow", new[] { "跟随主显示器", "Follow main monitor" } },
            { "card.now", new[] { "立即更换", "Change now" } },
            { "card.bing", new[] { "必应每日壁纸", "Bing daily wallpaper" } },
            { "card.caption.on", new[] { "文字描述：开", "Caption: on" } },
            { "card.caption.set", new[] { "文字描述…", "Caption…" } },
            { "dialog.folder", new[] { "选择该显示器的壁纸文件夹（保存并应用后生效）", "Choose the wallpaper folder for this monitor (takes effect after you click Save and Apply)" } },

            // ---- 运行期提示 ----
            { "msg.binglink", new[] { "无法打开浏览器预览页：{0}", "Cannot open the preview page: {0}" } },
            // v2.3 第二轮反馈②：清空本地文件夹但没勾必应 → 该屏无任何来源，明确告知
            { "msg.nolocal", new[] { "已设为不使用本地文件夹。\n\n请同时勾选下方的「必应每日壁纸」，否则这一屏没有任何壁纸来源，到点不会更换。", "Switched to \"no local folder\".\n\nAlso tick \"Bing daily wallpaper\" below, otherwise this screen has no wallpaper source at all and will never change on schedule." } },
            { "msg.nomonitor", new[] { "当前无可用显示器（系统壁纸服务异常），未保存配置，稍后会尝试自动恢复。", "No monitors are available (the system wallpaper service is failing). Settings were not saved; recovery will be retried automatically." } },
            { "bing.clean", new[] { "本地文件夹中的必应壁纸已累积超过 90 天（最早 {0}）。如需控制体积，建议手动删除 Bing_*.jpg 文件（每年约 1.x GB）。", "Bing wallpapers in your local folder are older than 90 days (oldest {0}). To keep the folder small, delete the Bing_*.jpg files manually (about 1.x GB per year)." } },

            // ---- 卸载 ----
            { "uninstall.title", new[] { "卸载 多屏幻灯壁纸", "Uninstall MultiWallCycle" } },
            { "uninstall.confirm", new[]
                {
                    "将清除【多屏幻灯壁纸】程序的所有设置及开机自启等全部系统痕迹，并将执行文件删除至系统回收站。"
                    + "如需重新获取或彻底清除本程序，可通过从系统回收站内恢复或清空回收站完成。是否确定？",
                    "This will remove all settings and every system trace of 【MultiWallCycle】, including its start-up entry, "
                    + "and move the executable to the Recycle Bin. To restore or fully erase the program later, "
                    + "restore it from the Recycle Bin or empty the Recycle Bin. Continue?"
                } },
            { "uninstall.partial", new[]
                {
                    "部分项目未能清理（常见的原因为另一登录会话里还有本程序实例在运行）。\n"
                    + "程序仍会退出，请稍后手动删除残留的配置与缓存目录。",
                    "Some items could not be removed (usually because another instance of this program is running in a different logon session).\n"
                    + "The program will still exit; please delete the remaining configuration and cache folders manually."
                } },
            { "uninstall.selfail", new[]
                {
                    "清理已完成，但未能把程序本体移入回收站，请手动删除：\n{0}",
                    "Cleanup is done, but the executable could not be moved to the Recycle Bin. Please delete it manually:\n{0}"
                } },

            // ---- 关于 ----
            { "about.title", new[] { "关于 多屏幻灯壁纸", "About MultiWallCycle" } },
            // 关于弹窗按界面语言只显示一种文字（不再中英同窗，避免低分辨率屏一屏放不下）：
            // [0]=中文界面用的正文，[1]=英文界面用的正文。
            { "about.text", new[]
                {
                    "MultiWallCycle多屏幻灯壁纸是༺格桑仁青༻ & Old Newbie\n"
                    + "使用豆包和CodeBuddy辅助编写的绿色开源程序，\n"
                    + "用于多屏显示环境下每屏使用不同壁纸并定时自动更换。\n"
                    + "程序免费非商用，作者不对其使用中的任何问题负责。\n"
                    + "删除程序，请点左下角“卸载”按钮，\n"
                    + "以彻底清除全部系统残留。\n\n"
                    + "2026年秋·四川省甘孜藏族自治州·版本：2.4",
                    "MultiWallCycle (多屏幻灯壁纸) is a free, open-source program\n"
                    + "written by ༺格桑仁青༻ & Old Newbie with the help of Doubao\n"
                    + "and CodeBuddy. It gives every monitor its own wallpaper and\n"
                    + "rotates it automatically on a timer. It is free for\n"
                    + "non-commercial use; the authors accept no liability for any\n"
                    + "problem arising from its use. To remove it, click the\n"
                    + "\"Uninstall\" button at the bottom-left corner to wipe every\n"
                    + "trace it left on the system.\n\n"
                    + "Autumn 2026 · Garzê Tibetan Autonomous Prefecture, Sichuan, China · Version 2.4"
                } },

            // ---- 文字描述设置窗 ----
            { "cap.title", new[] { "文字描述设置", "Caption settings" } },
            { "cap.show", new[] { "显示文字描述", "Show caption" } },
            { "cap.font", new[] { "字体", "Font" } },
            { "cap.style", new[] { "样式", "Style" } },
            { "cap.style.normal", new[] { "常规", "Regular" } },
            { "cap.style.bold", new[] { "加粗", "Bold" } },
            { "cap.style.italic", new[] { "斜体", "Italic" } },
            { "cap.size", new[] { "大小（像素）", "Size (px)" } },
            { "cap.fontcolor", new[] { "字体颜色", "Text color" } },
            { "cap.pickcolor", new[] { "选择颜色…", "Pick color…" } },
            { "cap.position", new[] { "位置", "Position" } },
            { "cap.pos.lb", new[] { "左下角", "Bottom left" } },
            { "cap.pos.bc", new[] { "下方居中", "Bottom center" } },
            { "cap.pos.rb", new[] { "右下角", "Bottom right" } },
            { "cap.pos.lt", new[] { "左上角", "Top left" } },
            { "cap.pos.tc", new[] { "上部居中", "Top center" } },
            { "cap.pos.rt", new[] { "右上角", "Top right" } },
            { "cap.effects", new[] { "视觉效果", "Effects" } },
            { "cap.shadow", new[] { "应用阴影效果", "Drop shadow" } },
            { "cap.shadowsize", new[] { "阴影大小（像素）", "Shadow size (px)" } },
            { "cap.shadowcolor", new[] { "阴影颜色", "Shadow color" } },
            { "cap.none.title", new[] { "没有可用的文字描述", "No caption available" } },
            { "cap.none.body", new[]
                {
                    "这一屏当前这张壁纸没有可用的描述文字，所以画不出文字。\n\n"
                    + "只有必应每日壁纸（文件名形如 xxx_1920x1080.jpg，且带 EXIF 描述或程序下载记录）才有描述。\n"
                    + "如果是自己收藏的普通图片，可以在该屏勾选「必应每日壁纸」作为来源，或把壁纸目录指向必应壁纸目录再试。",
                    "The wallpaper currently on this screen has no caption text, so nothing can be drawn.\n\n"
                    + "Captions exist only for Bing daily wallpapers (file names like xxx_1920x1080.jpg with EXIF description "
                    + "or a download record).\nFor ordinary images, enable the \"Bing daily wallpaper\" source for this screen, "
                    + "or point the folder to your Bing wallpaper collection."
                }
            }
        };
    }
}
