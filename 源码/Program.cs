using System;
using System.Threading;
using System.Windows.Forms;

namespace MultiWallCycle
{
    internal static class Program
    {
        // 注：尝试过 SetProcessDPIAware()（System DPI Aware）以消除跨屏虚拟化，但 .NET 4.8
        // WinForms 的 AutoScaleMode.Font 在 DPI aware 下会把窗口错误缩放（980→560 物理像素，
        // 布局全被截断），已回退。②⑤ 改由纯布局方案解决：卡片加宽填满右侧空白（消除横纹区域）、
        // 状态信息左移 AutoSize（跨屏虚拟化放大后也不超出竖屏物理宽）。

        /// <summary>
        /// v2.2：单实例互斥体改为「全局」（Global\），不再按登录会话隔离。
        /// 原因：Local\ 互斥体只在同一会话内互斥，实测同一台机器上 Session 1 与 Session 3
        /// 各跑一个实例（两个进程同时抢逐屏壁纸 COM），既浪费资源又互相覆盖壁纸。
        /// 全局互斥体覆盖整台机器；万一某些环境不允许创建全局对象，自动回退会话级互斥体，功能不退化。
        /// </summary>
        private static Mutex TryCreateMutex(out bool createdNew)
        {
            createdNew = false;
            try
            {
                return new Mutex(true, @"Global\MultiScreenSlideshow_9F2C7B", out createdNew);
            }
            catch (Exception ex)
            {
                Log.Warn("创建全局互斥体失败，回退会话级互斥体: " + ex.Message);
            }
            try
            {
                return new Mutex(true, @"Local\MultiScreenSlideshow_9F2C7B", out createdNew);
            }
            catch
            {
                createdNew = false;
                return null;
            }
        }

        private static bool HasArg(string[] args, string key)
        {
            for (int i = 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

#if TRACE_LOG
        /// <summary>递归输出控件树（含越界判定），供 -uigeom 几何自检使用。</summary>
        private static void DumpTree(System.Text.StringBuilder sb, System.Windows.Forms.Control parent, string indent, int clientW)
        {
            foreach (System.Windows.Forms.Control c in parent.Controls)
            {
                bool over = (c.Left < 0) || (c.Top < 0) || (c.Right > parent.ClientSize.Width)
                    || (c.Bottom > parent.ClientSize.Height);
                sb.AppendLine(indent + c.GetType().Name + " \""
                    + (c.Text ?? string.Empty).Replace("\r", " ").Replace("\n", " ")
                    + "\" " + c.Left + "," + c.Top + " " + c.Width + "x" + c.Height
                    + (over ? "  <<越界>>" : ""));
                if (c.Controls.Count > 0) { DumpTree(sb, c, indent + "  ", clientW); }
            }
        }
#endif

        [STAThread]
        private static void Main()
        {
            // v1.1.0 #6：全局异常兜底——任何线程/任何位置的未处理异常都写日志，
            // 不再让托盘程序无声崩溃（显示器插拔/会话切换时的 COM 抖动已被引擎内部捕获）。
            // v1.1.2 第三轮：处理器自身再包一层 try/catch（日志二次异常不得击穿），
            // 且 ThreadException 处理掉后不弹默认错误框、不退出，主窗口/托盘保持可用。
            // v2.2：Log 在正式版编译期即被移除，这两处处理器退化为纯粹的"吞掉异常"。
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                try { Log.Error("UI 线程异常: " + e.Exception); } catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                try { Log.Error("未处理异常: " + e.ExceptionObject); } catch { }
            };

            // v1.1.2 第三轮：开机自启注册命令带 -min（AutoStartHelper.Set），自启场景默认退托盘
            bool startMinimized = false;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], "-min", StringComparison.OrdinalIgnoreCase)) { startMinimized = true; }
            }

#if TRACE_LOG
            // 仅测试版：界面几何自检钩子。**必须放在单实例检查之前**——
            // 正式程序正在运行时，测试版会被互斥量直接挡掉，什么都测不到。
            if (HasArg(args, "-about") || HasArg(args, "-abouten")
                || HasArg(args, "-aboutgeom") || HasArg(args, "-capgeom")
                || HasArg(args, "-uigeom") || HasArg(args, "-uigeomen"))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                if (HasArg(args, "-about") || HasArg(args, "-abouten"))
                {
                    Lang.Set(HasArg(args, "-abouten") ? Lang.En : Lang.Zh);
                    AboutDialog.Show(null);
                    return;
                }

                if (HasArg(args, "-aboutgeom"))
                {
                    Lang.Set(Lang.Zh);
                    System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                    t.Interval = 600;
                    t.Tick += delegate(object s2, EventArgs e2)
                    {
                        try
                        {
                            t.Stop();
                            System.Text.StringBuilder sb = new System.Text.StringBuilder();
                            System.Drawing.Rectangle wa = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                            sb.AppendLine("workingArea=" + wa.Width + "x" + wa.Height);
                            foreach (System.Windows.Forms.Form fm in Application.OpenForms)
                            {
                                sb.AppendLine("form=\"" + fm.Text + "\" client=" + fm.ClientSize.Width
                                    + "x" + fm.ClientSize.Height + " window=" + fm.Width + "x" + fm.Height);
                                foreach (System.Windows.Forms.Control c in fm.Controls)
                                {
                                    sb.AppendLine("  " + c.GetType().Name + " dock=" + c.Dock + " "
                                        + c.Left + "," + c.Top + " " + c.Width + "x" + c.Height);
                                    foreach (System.Windows.Forms.Control cc in c.Controls)
                                    {
                                        sb.AppendLine("     " + cc.GetType().Name + " \"" + (cc.Text ?? string.Empty)
                                            + "\" " + cc.Left + "," + cc.Top + " " + cc.Width + "x" + cc.Height);
                                    }
                                }
                            }
                            System.IO.File.WriteAllText(
                                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mw_aboutgeom.txt"), sb.ToString());
                            foreach (System.Windows.Forms.Form fm in Application.OpenForms) { fm.Close(); }
                        }
                        catch { }
                    };
                    t.Start();
                    AboutDialog.Show(null);
                    t.Stop();
                    return;
                }

                if (HasArg(args, "-capgeom"))
                {
                    Lang.Set(Lang.Zh);
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    System.Drawing.Rectangle wa = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                    sb.AppendLine("workingArea=" + wa.Width + "x" + wa.Height);
                    CaptionStyle st = new CaptionStyle();
                    using (CaptionSettingsForm f = new CaptionSettingsForm(true, st))
                    {
                        sb.AppendLine("captionForm client=" + f.ClientSize.Width + "x" + f.ClientSize.Height);
                        foreach (System.Windows.Forms.Control c in f.Controls)
                        {
                            if (!(c is System.Windows.Forms.Panel)) { continue; }
                            sb.AppendLine("panel dock=" + c.Dock + " client=" + c.ClientSize.Width + "x"
                                + c.ClientSize.Height + " autoScrollMin=" + ((System.Windows.Forms.Panel)c).AutoScrollMinSize);
                            foreach (System.Windows.Forms.Control cc in c.Controls)
                            {
                                sb.AppendLine("   " + cc.GetType().Name + " \""
                                    + (cc.Text ?? string.Empty).Replace("\r", " ") + "\" "
                                    + cc.Left + "," + cc.Top + " " + cc.Width + "x" + cc.Height);
                            }
                        }
                    }
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mw_capgeom.txt"), sb.ToString());
                    return;
                }

                // v2.3 第二轮：主界面几何自检——顶部单行（重扫按钮 + 状态）、每张桥卡片的
                // 文件夹行（路径框 + 选择… + 不使用本地文件夹）是否越界。不落任何截图。
                if (HasArg(args, "-uigeom") || HasArg(args, "-uigeomen"))
                {
                    Lang.Set(HasArg(args, "-uigeomen") ? Lang.En : Lang.Zh);
                    AppConfig cfg0 = ConfigIO.Load();
                    WallpaperEngine eng = new WallpaperEngine();
                    eng.Init();
                    try { eng.RebuildMonitors(cfg0); } catch { }
                    System.Windows.Forms.Form f = new MainForm(cfg0, eng, false);
                    f.Show();
                    System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                    t.Interval = 800;
                    t.Tick += delegate(object s2, EventArgs e2)
                    {
                        try
                        {
                            t.Stop();
                            System.Text.StringBuilder sb = new System.Text.StringBuilder();
                            System.Drawing.Rectangle wa = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                            sb.AppendLine("workingArea=" + wa.Width + "x" + wa.Height);
                            sb.AppendLine("mainForm client=" + f.ClientSize.Width + "x" + f.ClientSize.Height);
                            DumpTree(sb, f, "  ", f.ClientSize.Width);
                            System.IO.File.WriteAllText(
                                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mw_uigeom.txt"), sb.ToString());
                        }
                        catch { }
                        try { f.Close(); } catch { }
                        try { eng.Shutdown(); } catch { }
                        // 探针进程直接结束：主窗体的 OnFormClosing 会 Cancel 并把窗口"藏起来"，
                        // 走 Application.Exit 会残留一个隐藏进程（实测会一直挂住并占用互斥量）。
                        Environment.Exit(0);
                    };
                    t.Start();
                    System.Windows.Forms.Application.Run(f);
                    return;
                }
            }
#endif
            bool createdNew;
            Mutex mtx = TryCreateMutex(out createdNew);
            try
            {
                if (mtx != null && !createdNew)
                {
                    // v1.1.0 #12：程序已在运行且窗口可能退在托盘——广播通知已运行实例显示窗口，
                    // 不再仅弹一个无感提示框后退出
                    AppMessages.NotifyExistingInstance();
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                AppConfig cfg = ConfigIO.Load();
                // v2.3：界面语言。配置里还没有记录（首次启动 / 旧版升级首次运行）→
                // 按系统 UI 语言自动选择：中文系统 → 中文界面，其余 → 英文界面；
                // 判定结果随后写入 config.json 的 Lang 字段（下面 ConfigIO.Save(cfg) 会落盘），
                // 之后只在用户手动切换时才改变，不再跟随系统语言变化。
                Lang.Set(Lang.Resolve(cfg.Lang));
                if (string.IsNullOrEmpty(cfg.Lang))
                {
                    cfg.Lang = Lang.Current;
                    Log.Info("首次启动自动选择界面语言: " + cfg.Lang);
                }


                WallpaperEngine engine = new WallpaperEngine();
                engine.Init();
                // v1.1.2 第三轮：COM 初始化失败不再直接退出（此前会导致拔插显示器后
                // 重启即退、程序"无法恢复"）。改为照常启动主窗口，引擎处于停用态，
                // 托盘/重扫可用，枚举与设置时自动重建 COM 恢复。
                if (!engine.ComOk)
                {
                    Log.Error("启动时 IDesktopWallpaper 不可用，已降级启动（稍后重扫可自动恢复）");
                }

                try
                {
                    engine.RebuildMonitors(cfg);
                }
                catch (Exception ex)
                {
                    Log.Error("启动时重建显示器失败（已降级，稍后自动重试）: " + ex.Message);
                }
                // v1.1.3：配置中缺 DevicePath（旧版遗留，或 v1.1.2 在兜底期把其清空）时，
                // 按当前枚举顺序回填完整 DevicePath，使"按显示器路径绑定"真正生效——
                // 缺路径时程序本就是按下标绑定，按下标回填不会比现状更差，之后即升级为按路径绑定。
                try
                {
                    if (cfg.Monitors != null)
                    {
                        int filled = 0;
                        for (int i = 0; i < cfg.Monitors.Count && i < engine.Monitors.Count; i++)
                        {
                            MonitorConfig mc = cfg.Monitors[i];
                            if (mc == null || !string.IsNullOrEmpty(mc.DevicePath)) { continue; }
                            string dp = engine.Monitors[i].DevicePath ?? string.Empty;
                            if (dp.Length > 0)
                            {
                                // 去掉可能的不可见前缀（BOM/零宽/控制字符）
                                dp = dp.Trim('\0', '\uFEFF', '\u200B', ' ', '\t', '\r', '\n');
                            }
                            // 判据取反：只要不是 Win32 设备名（\\.\DISPLAY1 或 \\.\DISPLAY1\Monitor0），
                            // 即为完整 DevicePath（\\?\DISPLAY#...）。比正向匹配前缀更稳健。
                            if (dp.Length > 0 && !dp.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
                            {
                                mc.DevicePath = dp;
                                filled++;
                            }
                        }
                        if (filled > 0)
                        {
                            Log.Info("已回填 " + filled + " 个屏幕的 DevicePath（升级为按显示器路径绑定）");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("回填 DevicePath 失败（不影响使用）: " + ex.Message);
                }

                // v2.3 第二轮反馈③：取消界面上的「启用壁纸轮换 / 开机自动启动」两个开关，
                // 改为恒定默认——程序在跑就轮换，程序在跑就（重新）登记开机自启。
                // 旧配置里若是 false，这里直接覆盖，保证升级后行为一致。
                cfg.EnableAll = true;
                cfg.AutoStart = true;

                ConfigIO.Save(cfg);          // 规范化配置后写回
                AutoStartHelper.Set(true);   // 同步注册表状态（自启项含 -min）

                // v2.2 优化③：启动成功后【不再】立即全部更换壁纸。
                // 每个屏的 NextSwitchUtc 已由 RebuildMonitors 设为 now + 设定间隔，
                // 只开始计时，到点才换（首装当天屏幕保持系统原有壁纸，属预期行为）。
                // 想立刻换图可用界面「立即全部更换」或托盘菜单同名项。
                MainForm form = new MainForm(cfg, engine, startMinimized);
                Application.Run(form);
                engine.Shutdown();
            }
            finally
            {
                if (mtx != null)
                {
                    try { if (createdNew) { mtx.ReleaseMutex(); } } catch { }
                    try { mtx.Dispose(); } catch { }
                }
            }
        }
    }
}
