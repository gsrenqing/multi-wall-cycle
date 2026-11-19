using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace MultiWallCycle
{
    public class MainForm : Form
    {
        private const int WM_POWERBROADCAST = 0x0218;
        private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
        private const int PBT_APMRESUMESUSPEND = 0x0007;

        private readonly AppConfig _cfg;
        private readonly WallpaperEngine _engine;
        private readonly List<MonitorCard> _cards = new List<MonitorCard>();

        private FlowLayoutPanel _flow;
        private Label _lblStatus;
        private Button _btnRefresh;
        private Button _btnSave;
        private Button _btnApplyNow;
        private Button _btnUninstall;
        private Button _btnLang;
        private NotifyIcon _tray;
        private ContextMenuStrip _trayMenu;
        private ToolStripMenuItem _miPause;
        private Timer _tickTimer;
        private Timer _hwTimer;
        private string _bingCleanRemind = string.Empty;   // v1.1.2：上次 Bing 壁纸清理提醒日期（yyyy-MM-dd）
        private DateTime _lastRemindCheckDay = DateTime.MinValue;   // v1.1.2：清理提醒每日只查一次
        private CaptionStyle _captionStyle = new CaptionStyle();   // v1.1.2：文字样式草稿（全局一份）
        private bool _cardsStale = false;   // v2.4：隐藏期间显示器布局变过，下次显示时要重建卡片

        public MainForm(AppConfig cfg, WallpaperEngine engine, bool startMinimized = false)
        {
            _cfg = cfg;
            _engine = engine;
            _engine.GlobalEnabled = cfg.EnableAll;
            _bingCleanRemind = (cfg.BingCleanRemind != null) ? cfg.BingCleanRemind : string.Empty;
            _captionStyle = (cfg.Caption != null) ? cfg.Caption : new CaptionStyle();

            // v2.3：显式关闭按字体自动缩放。程序按系统 DPI 虚拟化（96 DPI 逻辑像素）运行，
            // 再让 WinForms 按字体缩放一次就会与写死的控件坐标打架（历史上多次出现"文字被吃半行"）。
            AutoScaleMode = AutoScaleMode.None;

            // v1.1.2 第三轮：开机自启（-min 参数）→ 窗口默认退入托盘，仅托盘图标常驻；
            // 双击托盘图标或右键菜单可恢复窗口。手动双击 exe 启动（无参数）正常显示窗口。
            if (startMinimized)
            {
                this.Shown += delegate
                {
                    try
                    {
                        Hide();
                        ShowInTaskbar = false;
                    }
                    catch { }
                };
            }

            BuildUi();
            // v2.3 第二轮反馈③：取消顶部「启用壁纸轮换 / 开机自动启动」两个开关——
            // 二者改为程序运行的恒定默认值：程序在跑就轮换，程序在跑就登记自启。
            // 引擎与配置在此强制为 true，界面上不再提供关闭入口（托盘"暂停/继续"仍是会话级临时开关）。
            cfg.EnableAll = true;
            cfg.AutoStart = true;
            _engine.GlobalEnabled = true;
            // 注：注册表自启登记只在 Program 启动路径里做一次（Program.cs AutoStartHelper.Set(true)）。
            // 这里不重复写，避免几何自检钩子构造窗口时把&quot;测试版路径&quot;写进 Run 键。
            BuildTray();
            BuildCards();
            UpdateStatus();

            _tickTimer = new Timer();
            _tickTimer.Interval = 1000;
            _tickTimer.Tick += delegate { OnTick(); };
            _tickTimer.Start();

            _hwTimer = new Timer();
            _hwTimer.Interval = 5000;
            _hwTimer.Tick += delegate { OnHwTick(); };
            _hwTimer.Start();
            // v2.3：取消启动时的托盘气泡提示（"已启动…"），启动/最小化后不再打扰。
        }

        // ---------------- UI 构建 ----------------
        // v1.0.2：窗口放大、字号 10F、卡片 300×240；底部按钮区重排：
        // 「保存并应用」大按钮醒目（生效不退托盘）、去掉「退出程序」（彻底退出只能托盘右键）。
        // Dock 顺序：Top/Bottom 先 Add，Fill 最后 Add。
        // v2.3：所有文字改走 Lang.T（中英双份）；底部左端新增缩小 30% 的「卸载」，
        //       原卸载位置改为语言切换按钮（中文界面显示 English，英文界面显示 中文）。

        private void BuildUi()
        {
            Text = Lang.T("app.title");
            Size = new Size(980, 700);
            MinimumSize = new Size(780, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 10F);
            Icon = IconFactory.CreateIcon();
            DoubleBuffered = true;

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            // v2.3 第二轮反馈③：去掉两个勾选框后顶部只剩一行（重扫按钮 + 状态行），
            // 高度从 86 收到 52，腾出的空间全部给卡片区。
            top.Height = 52;
            top.Padding = new Padding(10, 8, 10, 0);

            _btnRefresh = new Button();
            _btnRefresh.Text = Lang.T("btn.rescan");
            _btnRefresh.Location = new Point(Lang.IsEn ? 12 : 12, 10);
            _btnRefresh.Size = new Size(Lang.IsEn ? 170 : 150, 30);
            _btnRefresh.Click += delegate { RefreshMonitors(); };

            _lblStatus = new Label();
            // v1.1.0 实测问题⑤：竖屏最大化时右侧状态被挤出屏幕（跨 DPI 虚拟化）。
            // 状态信息左移排到「重新扫描显示器」按钮后，AutoSize 左对齐，
            // 窗口多窄都不裁剪；配合 Program.cs 的 System DPI Aware 声明彻底可见。
            _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            _lblStatus.AutoSize = true;
            _lblStatus.Location = new Point(Lang.IsEn ? 200 : 176, 17);

            top.Controls.Add(_btnRefresh);
            top.Controls.Add(_lblStatus);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 66;
            // 必须显式设置 Width：Dock 面板默认宽 200，按钮 Add 时 Anchor 按 200 计算固定距离，
            // 布局后按钮会被推到窗口右侧外（v1.0.1 起"底部按钮不可见"的真根因）
            bottom.Width = 980;
            bottom.Padding = new Padding(10, 12, 10, 8);

            _btnSave = new Button();
            _btnSave.Text = Lang.T("btn.save");
            _btnSave.Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
            _btnSave.Location = new Point(790, 11);
            _btnSave.Size = new Size(170, 44);
            _btnSave.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnSave.Click += delegate { SaveAndApply(); };

            _btnApplyNow = new Button();
            _btnApplyNow.Text = Lang.T("btn.applynow");
            _btnApplyNow.Location = new Point(640, 17);
            _btnApplyNow.Size = new Size(140, 32);
            _btnApplyNow.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            // v1.1.0 #2：立即更换只把草稿写入对应屏运行时，不再整表重建（否则顺序模式永远跳回第 1 张）
            _btnApplyNow.Click += delegate { DraftApplyCards(); _engine.ApplyNowToAll(); UpdateStatus(); };

            // v2.3 ②：原「卸载」位置（520,17,110×32）改为语言切换按钮。
            _btnLang = new Button();
            _btnLang.Text = Lang.T("btn.lang");
            _btnLang.Location = new Point(520, 17);
            _btnLang.Size = new Size(110, 32);
            _btnLang.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnLang.Click += delegate { SwitchLanguage(); };

            // v2.3 ②：卸载按钮移到最下一行最左端，并缩小 30%（110×32 → 77×22），
            // 避免占据中部标准按钮位置被误点。
            _btnUninstall = new Button();
            _btnUninstall.Text = Lang.T("btn.uninstall");
            _btnUninstall.Font = new Font("Microsoft YaHei UI", 8.25F);
            _btnUninstall.Location = new Point(12, 22);
            _btnUninstall.Size = new Size(77, 22);
            _btnUninstall.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _btnUninstall.Click += delegate { Uninstall(); };

            bottom.Controls.Add(_btnSave);
            bottom.Controls.Add(_btnApplyNow);
            bottom.Controls.Add(_btnLang);
            bottom.Controls.Add(_btnUninstall);

            _flow = new FlowLayoutPanel();
            _flow.Dock = DockStyle.Fill;
            _flow.AutoScroll = true;
            _flow.WrapContents = true;
            _flow.Padding = new Padding(10, 8, 10, 8);

            // Dock 顺序（v1.1.0 实测问题①根因）：WinForms Dock 布局按 z 序从后往前处理，
            // 最后 Add 的控件最先布局。因此 Fill 必须先 Add（z 序最低、最后布局、占剩余区），
            // Top/Bottom 后 Add（z 序高、先布局、占顶部/底部），才不会覆盖 _flow 内容。
            // 旧顺序（top/bottom 先 Add）导致 top 面板覆盖 _flow 顶部，1 号卡标题/型号被吃。
            Controls.Add(_flow);
            Controls.Add(bottom);
            Controls.Add(top);
        }

        private void BuildTray()
        {
            // v2.3：语言切换后需要重建托盘菜单（菜单项文字随之切换）。
            // v2.4（采纳审查 #9）：原来只 Dispose 了 NotifyIcon，旧的 _trayMenu 与 Icon 句柄都留着——
            // 每切一次语言漏一个菜单 + 一个 HICON。现在一起回收。
            Icon oldTrayIcon = null;
            try
            {
                if (_tray != null)
                {
                    oldTrayIcon = _tray.Icon;
                    _tray.Visible = false;
                    _tray.ContextMenuStrip = null;
                    _tray.Dispose();
                }
            }
            catch { }
            _tray = null;
            if (oldTrayIcon != null) { try { oldTrayIcon.Dispose(); } catch { } }
            try
            {
                if (_trayMenu != null)
                {
                    _trayMenu.Dispose();
                }
            }
            catch { }
            _trayMenu = null;

            _trayMenu = new ContextMenuStrip();
            ToolStripMenuItem miOpen = new ToolStripMenuItem(Lang.T("tray.open"));
            miOpen.Click += delegate { ShowWindow(); };
            ToolStripMenuItem miNow = new ToolStripMenuItem(Lang.T("tray.now"));
            miNow.Click += delegate { _engine.ApplyNowToAll(); UpdateStatus(); };
            _miPause = new ToolStripMenuItem(_engine.GlobalEnabled ? Lang.T("tray.pause") : Lang.T("tray.resume"));
            _miPause.Click += delegate { TogglePause(); };
            // v2.3 ②：托盘右键菜单增加语言切换项（中文界面显示 English，英文界面显示 中文）
            ToolStripMenuItem miLang = new ToolStripMenuItem(Lang.T("btn.lang"));
            miLang.Click += delegate { SwitchLanguage(); };
            // v2.0：托盘菜单新增「关于」，位于「退出」之上
            ToolStripMenuItem miAbout = new ToolStripMenuItem(Lang.T("tray.about"));
            miAbout.Click += delegate { ShowAbout(); };
            ToolStripMenuItem miExit = new ToolStripMenuItem(Lang.T("tray.exit"));
            miExit.Click += delegate { ShutdownApp(); };

            _trayMenu.Items.Add(miOpen);
            _trayMenu.Items.Add(miNow);
            _trayMenu.Items.Add(_miPause);
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(miLang);
            _trayMenu.Items.Add(miAbout);
            _trayMenu.Items.Add(miExit);

            _tray = new NotifyIcon();
            _tray.Icon = IconFactory.CreateIcon();
            _tray.Text = Lang.T("app.name");
            _tray.Visible = true;
            _tray.ContextMenuStrip = _trayMenu;
            _tray.DoubleClick += delegate { ShowWindow(); };
        }

        // ---------------- 语言 ----------------

        /// <summary>
        /// v2.3 ②：切换界面语言。先把界面草稿写进运行时（重建控件时不丢用户刚改的值），
        /// 再把语言写入 config.json 固定下来（之后不再跟随系统语言变化），最后整体刷新界面。
        /// </summary>
        private void SwitchLanguage()
        {
            string target = Lang.IsEn ? Lang.Zh : Lang.En;
            try { DraftApplyCards(); } catch { }
            Lang.Set(target);
            _cfg.Lang = target;
            try
            {
                AppConfig saved = ConfigIO.Load();
                saved.Lang = target;
                ConfigIO.Save(saved);
            }
            catch (Exception ex)
            {
                Log.Warn("保存界面语言失败（本次会话仍生效）: " + ex.Message);
            }
            ApplyLanguage();
        }

        /// <summary>按当前 Lang 刷新所有可见文字：标题、顶部、底部、托盘菜单、显示器卡片、状态行。</summary>
        private void ApplyLanguage()
        {
            Text = Lang.T("app.title");

            _btnRefresh.Text = Lang.T("btn.rescan");
            _btnRefresh.Location = new Point(12, 10);
            _btnRefresh.Size = new Size(Lang.IsEn ? 170 : 150, 30);
            _lblStatus.Location = new Point(Lang.IsEn ? 200 : 176, 17);

            _btnSave.Text = Lang.T("btn.save");
            _btnApplyNow.Text = Lang.T("btn.applynow");
            _btnLang.Text = Lang.T("btn.lang");
            _btnUninstall.Text = Lang.T("btn.uninstall");

            BuildTray();      // 内部会先释放旧图标与旧菜单
            BuildCards();
            UpdateStatus();
        }

        /// <summary>
        /// v2.1：托盘菜单「关于」。改用自建窗体（MessageBox 无法调字号）。
        /// v2.3：正文固定中英对照（中文界面中文在上，英文界面英文在上），文字折行按句分行。
        /// 实现移到 AboutDialog，便于独立验证弹窗排版。
        /// </summary>
        private void ShowAbout()
        {
            AboutDialog.Show(this);
        }

        // ---------------- 卡片 ----------------

        private class MonitorCard
        {
            public GroupBox Box;
            public TextBox FolderBox;
            public Button BrowseBtn;
            public Button NoLocalBtn;     // v2.3 第二轮：清空本地文件夹（仅用必应）
            public Button NowBtn;
            public NumericUpDown IntervalBox;
            public ComboBox ModeBox;
            public ComboBox FitBox;
            public CheckBox EnableBox;
            public CheckBox BingBox;      // v1.1.2
            public Button CaptionBtn;     // v1.1.2：文字描述设置按钮（替代原勾选框）
            public bool ShowCaptionDraft; // v1.1.2：该屏"显示文字描述"草稿
            public CheckBox FollowBox;    // v2.0：跟随主显示器（显示器 1 为 null，不显示该选项）
        }

        private void BuildCards()
        {
            _cards.Clear();
            // v1.1.0 #10：反复重建必须 Dispose 旧控件，否则累积 GDI 句柄
            // v2.4（采纳审查 #3）：Dispose 过程中控件会从 Controls 集合里摘除自己，
            // 原来 foreach 遍历正在被修改的集合会抛 InvalidOperationException（重建时偶发崩溃）。
            // 改成倒序按索引取。
            for (int i = _flow.Controls.Count - 1; i >= 0; i--)
            {
                try { _flow.Controls[i].Dispose(); } catch { }
            }
            _flow.Controls.Clear();
            for (int i = 0; i < _engine.Monitors.Count; i++)
            {
                CreateCard(i, _engine.Monitors[i]);
            }
        }

        private void CreateCard(int index, MonitorRuntime rt)
        {
            bool en = Lang.IsEn;

            GroupBox box = new GroupBox();
            // v1.1.0 实测问题③：GroupBox.Text 标题在部分系统/渲染条件下不可靠，改用独立 Label 显示编号。
            // v2.0：卡片加高（240→256），为新增的「跟随主显示器」选项及顶部留白腾出空间。
            box.Size = new Size(450, 256);
            box.Margin = new Padding(6);

            // 显示器编号标题（独立 Label，粗体，保证可见）
            // v2.0 修复：原 Location.Y=8 且 AutoSize，粗体 11F 的字形上部会被 Label 上边界裁掉一点
            // （"显示器"三字最顶端的笔画显示不完整）。现下移至 Y=12，并显式给出高度（不再 AutoSize），
            // 保证字体完整绘制区域不被压缩。
            Label titleLbl = new Label();
            titleLbl.Text = Lang.T("card.title", index + 1);
            titleLbl.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            titleLbl.Location = new Point(12, 12);
            titleLbl.Size = new Size(200, 22);
            titleLbl.AutoSize = false;

            // v1.1.0 实测问题③：显示硬件型号（与设备管理器一致，如 ViewSonic VX2762-2K-HD），
            // 不再显示 \\?\DISPLAY#... 输出通道线路信息；解析失败兜底显示 DevicePath 短形式。
            // v1.1.2 第四轮（#14）：兜底模式优先用 Win32 直接捕获的真实型号（rt.ModelName）。
            string friendly = WallpaperEngine.GetMonitorFriendlyName(rt.DevicePath);
            if (friendly.Length == 0 && rt.ModelName != null && rt.ModelName.Length > 0)
            {
                friendly = rt.ModelName;
            }
            Label pathLbl = new Label();
            pathLbl.Text = Lang.T("card.model", friendly.Length > 0 ? friendly : Shorten(rt.DevicePath, 40));
            pathLbl.Location = new Point(12, 36);
            pathLbl.AutoSize = true;
            pathLbl.ForeColor = Color.Gray;

            Label folderLbl = new Label();
            folderLbl.Text = Lang.T("card.folder");
            folderLbl.Location = new Point(12, 58);
            folderLbl.AutoSize = true;

            // v2.3 第二轮反馈②：壁纸文件夹必须能"取消选择"。旧版一旦选过本地文件夹，
            // 就只能不停地换另一个文件夹，没有"不用本地文件夹"的退路（只能靠勾必应绕开）。
            // 现在文件夹行改为：路径框 + 选择… + 不使用本地文件夹，三个控件同一行。
            TextBox folderBox = new TextBox();
            folderBox.Text = rt.Folder;
            folderBox.ReadOnly = true;
            folderBox.Location = new Point(12, 76);
            folderBox.Size = new Size(196, 26);
            // 路径为空时显示灰字提示（EM_SETCUEBANNER），避免"空框"被误读成加载失败
            CueBanner.Set(folderBox, Lang.T("card.nolocal.cue"));

            Button browseBtn = new Button();
            browseBtn.Text = Lang.T("card.browse");
            browseBtn.Location = new Point(214, 75);
            browseBtn.Size = new Size(en ? 64 : 56, 28);

            Button noLocalBtn = new Button();
            noLocalBtn.Text = Lang.T("card.nolocal");
            noLocalBtn.Location = new Point(en ? 284 : 276, 75);
            noLocalBtn.Size = new Size(132, 28);

            Label intervalLbl = new Label();
            intervalLbl.Text = Lang.T("card.interval");
            intervalLbl.Location = new Point(12, 106);
            intervalLbl.AutoSize = true;

            NumericUpDown num = new NumericUpDown();
            num.Minimum = 1;
            num.Maximum = 1440;
            num.Value = (rt.IntervalMinutes < 1) ? 1 : rt.IntervalMinutes;
            num.Location = new Point(222, 103);
            num.Size = new Size(66, 26);

            Label modeLbl = new Label();
            modeLbl.Text = Lang.T("card.mode");
            modeLbl.Location = new Point(12, 134);
            modeLbl.AutoSize = true;

            ComboBox modeBox = new ComboBox();
            modeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            modeBox.Items.AddRange(new object[] { Lang.T("card.mode.seq"), Lang.T("card.mode.random") });
            modeBox.SelectedIndex = (rt.Mode == 1) ? 1 : 0;
            modeBox.Location = new Point(110, 131);
            modeBox.Size = new Size(176, 26);

            Label fitLbl = new Label();
            fitLbl.Text = Lang.T("card.fit");
            fitLbl.Location = new Point(12, 162);
            fitLbl.AutoSize = true;

            ComboBox fitBox = new ComboBox();
            fitBox.DropDownStyle = ComboBoxStyle.DropDownList;
            fitBox.Items.AddRange(new object[] { Lang.T("card.fit.system"), Lang.T("card.fit.crop") });
            fitBox.SelectedIndex = (rt.Fit == 1) ? 1 : 0;
            fitBox.Location = new Point(110, 159);
            fitBox.Size = new Size(176, 26);

            CheckBox enBox = new CheckBox();
            enBox.Text = Lang.T("card.enable");
            enBox.Checked = rt.Enabled;
            enBox.Location = new Point(12, 188);
            // v2.1 第二轮修复：原先 AutoSize，在用户系统 DPI 下「启用此屏」实际约 116px 宽，
            // 会右压到 110 处的跟随复选框，把勾选框左半（方框本身）遮住 → 看起来"半个显示不出来"。
            // 固定边界锁死，保证不越界；英文文案更长，单独给宽度。
            enBox.Size = new Size(en ? 132 : 120, 24);

            // v2.0：跟随主显示器（仅显示器 2 及以后提供；显示器 1 即主显示器本身，无需该选项）
            CheckBox followBox = null;
            if (index >= 1)
            {
                followBox = new CheckBox();
                followBox.Text = Lang.T("card.follow");
                followBox.Checked = rt.FollowMain;
                // v2.1 第二轮：整体右移 1.5 个全角字符（110 → 146），宽度给足 180px
                followBox.Location = new Point(en ? 148 : 146, 188);
                followBox.Size = new Size(en ? 170 : 180, 24);
            }

            Button nowBtn = new Button();
            nowBtn.Text = Lang.T("card.now"); // 跟随复选框右移后同步让位，避免重叠
            nowBtn.Location = new Point(en ? 326 : 332, 186);
            nowBtn.Size = new Size(en ? 90 : 86, 28);
            if (en) { nowBtn.Font = new Font("Microsoft YaHei UI", 9F); }

            // v1.1.2：必应每日壁纸来源 + 文字描述设置按钮（与本地文件夹可复选）
            // v2.2 优化②：勾选框收成小方框，「必应每日壁纸」改为可点击的超链接，点开浏览器预览当日壁纸；
            // 同时取消"仅勾必应→间隔强制 1 天"，间隔完全由用户设定（必应在最近 8 天里轮换）。
            // v2.3 ④：链接改为按当前日期生成 https://bing.wdbyte.com/day/yyyyMM/dd.html（当日页不可用则回退首页）。
            CheckBox bingBox = new CheckBox();
            bingBox.Text = string.Empty;
            bingBox.Checked = rt.UseBing;
            bingBox.Location = new Point(12, 217);
            bingBox.Size = new Size(22, 22);

            LinkLabel bingLink = new LinkLabel();
            bingLink.Text = Lang.T("card.bing");
            bingLink.Location = new Point(36, 219);
            bingLink.AutoSize = true;
            bingLink.LinkBehavior = LinkBehavior.HoverUnderline;
            bingLink.LinkClicked += delegate
            {
                Cursor previous = Cursor.Current;
                try
                {
                    Cursor.Current = Cursors.WaitCursor;
                    string url = BingWallpaper.ResolvePreviewUrl();
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Log.Warn("打开必应壁纸预览页失败: " + ex.Message);
                    MessageBox.Show(this, Lang.T("msg.binglink", ex.Message),
                        Lang.T("app.name"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally
                {
                    Cursor.Current = previous;
                }
            };

            Button capBtn = new Button();
            capBtn.Text = Lang.T(rt.ShowCaption ? "card.caption.on" : "card.caption.set");
            capBtn.Location = new Point(en ? 186 : 190, 213);
            // v2.1 修复：100px 同样被 DPI 截断（截图只显示"文字描述："），加宽到 140
            capBtn.Size = new Size(en ? 132 : 140, 28);

            // v1.1.2 原联动（仅勾必应且无本地文件夹 → 间隔强制 1440）已在 v2.2 取消：
            // 必应在"最近 8 天"里按用户设定的间隔轮换，间隔不再被程序改写。

            box.Controls.Add(titleLbl);
            box.Controls.Add(pathLbl);
            box.Controls.Add(folderLbl);
            box.Controls.Add(folderBox);
            box.Controls.Add(browseBtn);
            box.Controls.Add(noLocalBtn);
            box.Controls.Add(intervalLbl);
            box.Controls.Add(num);
            box.Controls.Add(modeLbl);
            box.Controls.Add(modeBox);
            box.Controls.Add(fitLbl);
            box.Controls.Add(fitBox);
            box.Controls.Add(enBox);
            box.Controls.Add(nowBtn);
            if (followBox != null) { box.Controls.Add(followBox); }
            box.Controls.Add(bingBox);
            box.Controls.Add(bingLink);
            box.Controls.Add(capBtn);

            MonitorCard card = new MonitorCard();
            card.Box = box;
            card.FolderBox = folderBox;
            card.BrowseBtn = browseBtn;
            card.NoLocalBtn = noLocalBtn;
            card.NowBtn = nowBtn;
            card.IntervalBox = num;
            card.ModeBox = modeBox;
            card.FitBox = fitBox;
            card.EnableBox = enBox;
            card.BingBox = bingBox;
            card.CaptionBtn = capBtn;
            card.ShowCaptionDraft = rt.ShowCaption;
            card.FollowBox = followBox;

            // v2.0：勾选「跟随主显示器」后，本屏壁纸设置项一律禁用，并填入主显示器（显示器 1）的
            // 当前值，让用户直观看到"跟随的是什么"；取消勾选则恢复可自行设置。
            if (followBox != null)
            {
                Action<bool> applyFollow = delegate(bool follow)
                {
                    if (follow && _engine.Monitors.Count > 0)
                    {
                        MonitorRuntime main = _engine.Monitors[0];
                        bingBox.Checked = main.UseBing;          // 先设，避免其后联动覆盖间隔
                        folderBox.Text = main.Folder;
                        num.Value = Math.Min(1440, Math.Max(1, main.IntervalMinutes));
                        modeBox.SelectedIndex = (main.Mode == 1) ? 1 : 0;
                        fitBox.SelectedIndex = (main.Fit == 1) ? 1 : 0;
                        card.ShowCaptionDraft = main.ShowCaption;
                        capBtn.Text = Lang.T(main.ShowCaption ? "card.caption.on" : "card.caption.set");
                    }
                    folderBox.Enabled = !follow;
                    browseBtn.Enabled = !follow;
                    noLocalBtn.Enabled = !follow;
                    num.Enabled = !follow;
                    modeBox.Enabled = !follow;
                    fitBox.Enabled = !follow;
                    bingBox.Enabled = !follow;
                    bingLink.Enabled = !follow;   // v2.2：跟随主显示器时预览链接同样不参与本屏设置
                    capBtn.Enabled = !follow;
                };
                followBox.CheckedChanged += delegate { applyFollow(followBox.Checked); };
                applyFollow(followBox.Checked);   // 载入配置后的初始状态也执行一次
            }

            _cards.Add(card);
            _flow.Controls.Add(box);

            int idx = index;
            browseBtn.Click += delegate { ChooseFolder(card); };
            // v2.3 第二轮反馈②：「不使用本地文件夹」→ 清空路径（必应图自动落到程序数据目录缓存，
            // 不再需要用户指定一个文件夹充当存放地）。若同时没勾必应，明确告知该屏将没有来源。
            noLocalBtn.Click += delegate
            {
                card.FolderBox.Text = string.Empty;
                if (!card.BingBox.Checked)
                {
                    MessageBox.Show(this, Lang.T("msg.nolocal"), Lang.T("app.name"),
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };
            // v1.1.2：文字描述设置按钮 → 弹设置窗口（草稿制，确定才生效）
            capBtn.Click += delegate { OpenCaptionSettings(card); };
            // v1.1.0 #2：立即更换只改该屏草稿字段 + 触发换图，不重建整表（顺序模式保持推进）
            nowBtn.Click += delegate
            {
                ApplyCardDraft(idx);
                _engine.TriggerNow(idx);
                UpdateStatus();
            };
        }

        /// <summary>
        /// v1.1.2：打开文字描述设置窗口。确定 → 更新该屏开关草稿 + 全局样式草稿；
        /// 取消 → 本次改动全部作废。真正生效仍走「保存并应用」。
        /// </summary>
        private void OpenCaptionSettings(MonitorCard card)
        {
            using (CaptionSettingsForm frm = new CaptionSettingsForm(card.ShowCaptionDraft, _captionStyle))
            {
                if (frm.ShowDialog(this) == DialogResult.OK)
                {
                    card.ShowCaptionDraft = frm.ShowCaption;
                    _captionStyle = frm.Style;
                    card.CaptionBtn.Text = Lang.T(card.ShowCaptionDraft ? "card.caption.on" : "card.caption.set");

                    // v2.3 修复"设了文字描述却看不到"：点确定后立刻按新样式重绘该屏当前这张图，
                    // 让效果当场可见（不换图、不推进轮换进度）。没描述可画时明确告知原因。
                    int idx = _cards.IndexOf(card);
                    if (idx >= 0 && card.ShowCaptionDraft)
                    {
                        bool drawn = _engine.RepaintCaption(idx);
                        if (!drawn)
                        {
                            MessageBox.Show(this, Lang.T("cap.none.body"), Lang.T("cap.none.title"),
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                }
            }
        }

        // v1.0.2 草稿制：选择文件夹只改界面草稿，不立即进运行时；
        // 一切改动均以「保存并应用」为准生效。
        private void ChooseFolder(MonitorCard card)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = Lang.T("dialog.folder");
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    card.FolderBox.Text = dlg.SelectedPath;
                    // v2.2：不再因"必应 + 无本地文件夹"把间隔改成 1440，间隔始终以用户设定为准
                }
            }
        }

        // ---------------- 行为 ----------------
        // v1.0.2 交互语义：
        //  1. 界面控件值 = “草稿”，改动只停留在界面；
        //  2. 「保存并应用」：草稿 → 生效 + 写盘，窗口保持打开不退托盘；
        //  3. 「立即更换/立即全部更换」：用草稿预览换图（不写盘）；
        //  4. 右上角 ×：关闭窗口退托盘，同时丢弃未保存草稿、恢复上一次生效配置；
        //  5. 托盘右键「退出」：彻底退出程序。

        /// <summary>把单屏界面草稿写入对应运行时（不重建整表，v1.1.0 #2）。</summary>
        private void ApplyCardDraft(int idx)
        {
            if (idx < 0 || idx >= _cards.Count || idx >= _engine.Monitors.Count) { return; }
            MonitorCard card = _cards[idx];
            MonitorRuntime rt = _engine.Monitors[idx];
            rt.Enabled = card.EnableBox.Checked;
            rt.Folder = card.FolderBox.Text;
            rt.Mode = (card.ModeBox.SelectedIndex == 1) ? 1 : 0;
            rt.Fit = (card.FitBox.SelectedIndex == 1) ? 1 : 0;
            rt.UseBing = card.BingBox.Checked;
            rt.ShowCaption = card.ShowCaptionDraft;
            // v1.1.2：样式草稿同步到引擎（"立即更换"预览也用最新草稿样式）
            _engine.CaptionStyle = _captionStyle;
            // v2.2 优化②：间隔完全按用户设定（不再"仅勾必应→强制 1440"）
            rt.IntervalMinutes = (int)card.IntervalBox.Value;

            // v2.0：跟随主显示器 —— 本屏壁纸设置一律取主显示器（显示器 1）的当前值。
            // 界面上这些控件在勾选后已禁用，此处再兜一次底，保证"立即更换"预览也一致。
            rt.FollowMain = (card.FollowBox != null) && card.FollowBox.Checked;
            if (rt.FollowMain && idx > 0 && _engine.Monitors.Count > 0)
            {
                MonitorRuntime main = _engine.Monitors[0];
                rt.Folder = main.Folder;
                rt.IntervalMinutes = main.IntervalMinutes;
                rt.Mode = main.Mode;
                rt.Fit = main.Fit;
                rt.UseBing = main.UseBing;
                rt.ShowCaption = main.ShowCaption;
            }
        }

        /// <summary>把全部卡片草稿写入运行时（v1.1.0 #2）。</summary>
        private void DraftApplyCards()
        {
            for (int i = 0; i < _cards.Count; i++) { ApplyCardDraft(i); }
        }

        private void SaveAndApply()
        {
            // v1.1.2 第三轮：显示器枚举不可用（0 屏，COM 异常场景）时拒绝保存——
            // 否则 ReadCards 从 0 张卡片生成空 Monitors，会覆盖清空 config 中的屏配置，
            // 造成"断开显示器后配置永久丢失、重启也无法恢复"的二次灾难。
            if (_engine.Monitors.Count == 0)
            {
                Log.Error("当前无可用显示器（壁纸服务枚举异常），已阻止保存，防止清空现有屏配置");
                _tray.ShowBalloonTip(3000, Lang.T("app.name"), Lang.T("msg.nomonitor"), ToolTipIcon.Warning);
                return;
            }
            // v2.1 第二轮修复 BUG②：保存前先把界面草稿同步进运行时，确保勾选状态
            // （含「跟随主显示器」）先落到 rt，再由 ReadCards 生成配置落盘。
            DraftApplyCards();
            AppConfig cur = ReadCards();
            // v2.2：用户主动保存 → 跳过 30 秒枚举抖动冷却，立即生效
            _engine.RebuildMonitors(cur, true, true);
            ConfigIO.Save(cur);
            _engine.GlobalEnabled = cur.EnableAll;
            AutoStartHelper.Set(cur.AutoStart);
            _engine.ApplyNowToAll();
            BuildCards();
            UpdateStatus();
            // v1.1.0 实测问题④：保存后静默生效，不再弹托盘通知「配置已保存，窗口继续调整」
            Log.Info("配置已保存并应用");
        }

        private void RefreshMonitors()
        {
            AppConfig last = ConfigIO.Load();
            // v2.2：用户主动重扫 → 跳过冷却，立即按磁盘配置重建
            _engine.RebuildMonitors(last, true, true);
            // v2.3 第二轮反馈③：两个开关已取消，重扫不再同步界面勾选状态
            BuildCards();
            UpdateStatus();
            _tray.ShowBalloonTip(1000, Lang.T("app.name"), Lang.T("tray.rescanned"), ToolTipIcon.Info);
            Log.Info("显示器重新扫描，按上次生效配置绑定");
        }

        private AppConfig ReadCards()
        {
            AppConfig c = new AppConfig();
            // v2.3 第二轮反馈③：轮换与自启是恒定默认，保存时固定写 true
            c.EnableAll = true;
            c.AutoStart = true;
            c.BingCleanRemind = _bingCleanRemind;   // 保留上次清理提醒日期（v1.1.2）
            c.Lang = Lang.Current;                  // v2.3：界面语言随配置保存，不能在这里丢
            for (int i = 0; i < _cards.Count; i++)
            {
                MonitorCard card = _cards[i];
                MonitorConfig mc = new MonitorConfig();
                // v1.1.0 #3：保存时写入 DevicePath，供拔插后按路径匹配配置。
                // v1.1.3 修正：v1.1.2 在兜底态（DevicePath 为 Win32 子设备名）会把它清成空串，
                // 一旦用户在 COM 失效期保存过一次，配置就永久退化成"按下标绑定"
                // （config.json 中两条配置 DevicePath 均为空即由此而来，拔插后可能串行）。
                // 改为：兜底态优先还原为已记录的完整 DevicePath，还原不到才留空。
                string devPath = string.Empty;
                if (i < _engine.Monitors.Count)
                {
                    devPath = _engine.Monitors[i].DevicePath ?? string.Empty;
                    if (devPath.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase))
                    {
                        string known = _engine.GetKnownFullPath(_engine.Monitors[i].Rect);
                        devPath = known.Length > 0 ? known : string.Empty;
                    }
                }
                mc.DevicePath = devPath;
                mc.Enabled = card.EnableBox.Checked;
                mc.Folder = card.FolderBox.Text;
                mc.Mode = (card.ModeBox.SelectedIndex == 1) ? 1 : 0;
                mc.Fit = (card.FitBox.SelectedIndex == 1) ? 1 : 0;
                mc.UseBing = card.BingBox.Checked;
                mc.ShowCaption = card.ShowCaptionDraft;
                // v2.0：跟随主显示器（显示器 1 无此选项，存 false）
                // v2.1 第二轮 BUG②：勾选框万一缺失（极端情况卡片没建出来）时以运行时值为准，
                // 避免"保存一回、勾选就被清零"。
                bool follow = (card.FollowBox != null) && card.FollowBox.Checked;
                if (card.FollowBox == null && i < _engine.Monitors.Count)
                {
                    follow = _engine.Monitors[i].FollowMain;
                }
                mc.FollowMain = follow;
                c.Caption = _captionStyle;   // v1.1.2：文字样式全局一份随配置保存
                // v2.2：间隔始终按用户设定（取消"仅勾必应→强制 1440"）
                mc.IntervalMinutes = (int)card.IntervalBox.Value;
                // v2.2：顺序轮换进度必须沿用运行时值——ReadCards 每张都新建 MonitorConfig，
                // 不补这一句就会把进度清零，保存一次配置就等于"从第 1 张重新开始"。
                mc.SeqPos = (i < _engine.Monitors.Count) ? _engine.Monitors[i].SeqPos : -1;
                c.Monitors.Add(mc);
            }
            return c;
        }

        private void OnTick()
        {
            _engine.CheckSchedules();
            // v1.1.0 #12：窗口隐藏时不做每秒状态刷新（字符串拼接 + Label 更新全免）
            if (Visible) { UpdateStatus(); }
            // v1.1.2：Bing 累积清理提醒每日检查一次
            if (_lastRemindCheckDay != DateTime.Today)
            {
                _lastRemindCheckDay = DateTime.Today;
                CheckBingCleanRemind();
            }
        }

        /// <summary>
        /// v1.1.2：本地文件夹中的 Bing_*.jpg 最早一张距今 ≥90 天且上次提醒距今 ≥90 天 → 托盘提醒手动清理。
        /// 用户清理文件后最早日期变新，自然不再提醒；提醒日期写入 config（BingCleanRemind）。
        /// </summary>
        private void CheckBingCleanRemind()
        {
            try
            {
                DateTime today = DateTime.Today;
                DateTime earliest = DateTime.MaxValue;
                foreach (MonitorRuntime rt in _engine.Monitors)
                {
                    if (!rt.UseBing || string.IsNullOrEmpty(rt.Folder) || !Directory.Exists(rt.Folder)) { continue; }
                    foreach (string f in Directory.GetFiles(rt.Folder))
                    {
                        if (!BingWallpaper.IsBingFile(f)) { continue; }
                        string d = BingWallpaper.FileDate(f);
                        DateTime dd;
                        if (!DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out dd))
                        {
                            // 原始文件名（*_UHD.jpg）不含日期 → 用文件写入时间近似下载日
                            try { dd = File.GetLastWriteTime(f).Date; }
                            catch { continue; }
                        }
                        if (dd < earliest) { earliest = dd; }
                    }
                }
                if (earliest == DateTime.MaxValue) { return; }
                if ((today - earliest).Days < 90) { return; }

                DateTime lastRemind;
                bool due = !DateTime.TryParseExact(_bingCleanRemind, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out lastRemind) || (today - lastRemind).Days >= 90;
                if (!due) { return; }

                _bingCleanRemind = today.ToString("yyyy-MM-dd");
                try
                {
                    AppConfig c = ConfigIO.Load();
                    c.BingCleanRemind = _bingCleanRemind;
                    ConfigIO.Save(c);
                }
                catch { }
                _tray.ShowBalloonTip(5000, Lang.T("app.name"),
                    Lang.T("bing.clean", earliest.ToString("yyyy-MM-dd")), ToolTipIcon.Warning);
                Log.Info("已提醒用户清理累积的 Bing 壁纸（最早 " + earliest.ToString("yyyy-MM-dd") + "）");
            }
            catch (Exception ex)
            {
                Log.Warn("Bing 清理提醒检查失败: " + ex.Message);
            }
        }

        /// <summary>
        /// v2.1 第二轮 BUG②：判断窗口里是否存在"未保存的改动"
        /// （卡片草稿 vs 运行时、顶部两开关 vs 引擎）。
        /// 用于后台定时器重建时避免把用户刚勾选的内容刷掉。
        /// </summary>
        private bool HasUnsavedDraft()
        {
            for (int i = 0; i < _cards.Count && i < _engine.Monitors.Count; i++)
            {
                MonitorCard card = _cards[i];
                MonitorRuntime rt = _engine.Monitors[i];
                if (card.EnableBox.Checked != rt.Enabled) { return true; }
                if (!string.Equals(card.FolderBox.Text, rt.Folder)) { return true; }
                if ((int)card.IntervalBox.Value != rt.IntervalMinutes) { return true; }
                if (card.ModeBox.SelectedIndex != rt.Mode) { return true; }
                if (card.FitBox.SelectedIndex != rt.Fit) { return true; }
                if (card.BingBox.Checked != rt.UseBing) { return true; }
                if (card.ShowCaptionDraft != rt.ShowCaption) { return true; }
                bool follow = (card.FollowBox != null) && card.FollowBox.Checked;
                if (follow != rt.FollowMain) { return true; }
            }
            return false;
        }

        private void OnHwTick()
        {
            if (_engine.MonitorLayoutChanged())
            {
                Log.Info("检测到显示器布局变化，按上次生效配置重新绑定");
                AppConfig last = ConfigIO.Load();
                _engine.RebuildMonitors(last);
                // v2.1 第二轮修复 BUG②：背景重建会用磁盘上的旧配置重建卡片，
                // 把用户刚勾选、还没保存的「跟随主显示器」等草稿直接刷掉，
                // 表现为"点保存并应用后此前的勾选就没了"。
                // 窗口可见且有未保存改动时，只重绑引擎、不动画面。
                if (Visible && HasUnsavedDraft())
                {
                    Log.Info("窗口有未保存修改，本次跳过卡片重建（避免丢失草稿）");
                }
                else
                {
                    if (Visible) { BuildCards(); }
                    else { _cardsStale = true; }   // v2.4（采纳审查 #10）：隐藏时先记账，等真正显示出来再重建
                }
                UpdateStatus();
                _tray.ShowBalloonTip(1500, Lang.T("app.name"), Lang.T("tray.layoutchanged"), ToolTipIcon.Info);
            }
        }

        private void TogglePause()
        {
            _engine.GlobalEnabled = !_engine.GlobalEnabled;
            _miPause.Text = _engine.GlobalEnabled ? Lang.T("tray.pause") : Lang.T("tray.resume");
            _tray.ShowBalloonTip(1200, Lang.T("app.name"),
                Lang.T(_engine.GlobalEnabled ? "tray.resumed" : "tray.paused"), ToolTipIcon.Info);
            UpdateStatus();
        }

        private void ShowWindow()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            Activate();
            // v2.4（采纳审查 #10）：程序藏在托盘期间显示器布局变过，卡片不会跟着更新
            // （OnHwTick 里 Visible=false 时是跳过重建的），一打开就是旧屏数与旧路径。
            // 这里补一次重建；有未保存草稿时保持不动，免得把用户刚改的勾选刷掉。
            if (_cardsStale)
            {
                _cardsStale = false;
                if (!HasUnsavedDraft())
                {
                    try { BuildCards(); } catch { }
                }
            }
        }

        /// <summary>
        /// v2.2 优化①：卸载。点「卸载」→ 确认框（确定 / 取消）→ 确定后依次：
        /// ① 删注册表自启项（新值名 MultiWallCycle + 旧值名 多屏幻灯壁纸）；
        /// ② 停轮换、释放托盘、保存最后一次顺序进度；
        /// ③ 删程序生成的全部文件（config.json / bing_meta.json / log.txt / temp 缓存，
        ///    覆盖 exe 同目录、%LOCALAPPDATA%\MultiWallCycle 与旧版 %LOCALAPPDATA%\多屏幻灯壁纸）；
        /// ④ 退出进程，由外部 PowerShell 把 exe 自身移入系统回收站（可还原）。
        /// 用户壁纸文件夹里的图片属于用户内容，一律不删。
        /// v2.3：确认文案与后续提示随界面语言切换。
        /// </summary>
        private void Uninstall()
        {
            DialogResult r = MessageBox.Show(this, Uninstaller.ConfirmText, Lang.T("uninstall.title"),
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.OK) { return; }

            // 先停调度与托盘，避免删除过程中还有换图/写盘动作与清理竞争
            try { if (_tickTimer != null) { _tickTimer.Enabled = false; } } catch { }
            try { if (_hwTimer != null) { _hwTimer.Enabled = false; } } catch { }
            try { _engine.Shutdown(); } catch { }
            try { _tray.Visible = false; } catch { }

            bool cleaned = Uninstaller.Cleanup();

            // 清理失败也要继续退出：残留项由提示告知用户手动处理
            if (!cleaned)
            {
                MessageBox.Show(this, Lang.T("uninstall.partial"),
                    Lang.T("uninstall.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            string exePath = Application.ExecutablePath;
            if (!Uninstaller.ScheduleRecycleSelf(exePath))
            {
                MessageBox.Show(this, Lang.T("uninstall.selfail", exePath),
                    Lang.T("uninstall.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            try { _tray.Dispose(); } catch { }
            Application.Exit();
        }

        private void UpdateStatus()
        {
            if (_lblStatus == null) { return; }
            int enabled = 0;
            DateTime next = DateTime.MaxValue;
            foreach (MonitorRuntime rt in _engine.Monitors)
            {
                if (rt.Enabled && _engine.GlobalEnabled)
                {
                    enabled++;
                    if (rt.NextSwitchUtc < next) { next = rt.NextSwitchUtc; }
                }
            }
            // v1.1.0 实测问题⑤：竖屏（DPI 虚拟化 1.75×）最大化时逻辑可用宽仅 ~604px，
            // 状态信息须足够紧凑才能完整显示——去掉空格与冗余字（"下次更换"→"下次"）
            string s;
            if (_engine.GlobalEnabled && enabled > 0)
            {
                s = Lang.T("status.on", _engine.Monitors.Count, enabled,
                    next.ToLocalTime().ToString("HH:mm:ss"));
            }
            else
            {
                s = Lang.T("status.off", _engine.Monitors.Count);
            }
            _lblStatus.Text = s;
        }

        private static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) { return s; }
            return s.Length <= max ? s : s.Substring(0, max - 3) + "...";
        }

        private void ShutdownApp()
        {
            Log.Info("程序退出");
            // v2.2：退出前再存一次顺序轮换进度（Shutdown 内部也会落盘，此处兜底）
            try { _engine.SaveProgress(); } catch { }
            _engine.Shutdown();
            _tray.Visible = false;
            _tray.Dispose();
            Application.Exit();
        }

        // ---------------- 系统事件 ----------------

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                // 右上角 ×：丢弃未保存的草稿，恢复上一次生效配置，退入托盘
                AppConfig last = ConfigIO.Load();
                // v2.2：跳过冷却立即恢复（否则启动 30 秒内关窗可能被静默忽略）
                _engine.RebuildMonitors(last, true, true);
                if (Visible) { BuildCards(); }
                UpdateStatus();
                Log.Info("窗口关闭，未保存的更改已放弃，恢复到上次生效配置");
                // v2.3：取消"已退入托盘"的气泡提示（用户反馈最小化后弹消息碍眼且无必要）。
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            // v2.2 优化④：关机/注销前保存顺序轮换进度（用户要求"每次关机或退出时保存顺序进度"）。
            // WM_QUERYENDSESSION 先到（允许应用确认），WM_ENDSESSION 是最终通知，两个都存一次，重复写入无副作用。
            if (m.Msg == 0x0011 || m.Msg == 0x0016)
            {
                try { _engine.SaveProgress(); } catch { }
            }
            else if (m.Msg == WM_POWERBROADCAST)
            {
                int wp = m.WParam.ToInt32();
                if (wp == PBT_APMRESUMEAUTOMATIC || wp == PBT_APMRESUMESUSPEND)
                {
                    Log.Info("系统唤醒，立即重新应用壁纸");
                    _engine.ApplyNowToAll();
                    UpdateStatus();
                }
            }
            else if (m.Msg == AppMessages.WM_SHOW_APP)
            {
                // v1.1.0 #12：重复启动时由新实例广播通知，把设置窗口显示到前台
                ShowWindow();
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>
    /// v2.3：「关于」弹窗。只显示当前界面语言的一种文字（中文界面中文、英文界面英文）。
    ///
    /// v2.3 刷新重制（DPI 相关重做）：
    /// 1. 窗体尺寸**由正文实际测量结果决定**，不再写死 700×440 —— 少一个字就窄一点、少一行就矮一点，
    ///    不会出现"内容缩了、窗口还是原来那么大"的空旷感（此前在 1080×1920 竖屏上占掉近半屏）。
    /// 2. AutoScaleMode.None：本程序以 DPI 虚拟化（96 DPI 逻辑像素）运行，再叠加一次 Font 缩放会让
    ///    字号与行高错位；改为单层坐标后，系统缩放到任何 DPI 都是等比放大，不会切字。
    /// 3. 尺寸最后**钳制到当前屏幕工作区的 90%**；万一正文过高，正文区改为可滚动，按钮条永远贴底可见。
    /// </summary>
    public static class AboutDialog
    {
        public static void Show(IWin32Window owner)
        {
            // 只显示当前界面语言的正文（v2.3 调整：不再中英同窗，低分辨率屏也能一屏放下）
            string text = Lang.T("about.text");

            using (Form f = new Form())
            {
                f.Text = Lang.T("about.title");
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.AutoScaleMode = AutoScaleMode.None;
                f.Font = new Font("Microsoft YaHei UI", 10.5F);

                Rectangle wa = (owner is Control)
                    ? Screen.FromControl((Control)owner).WorkingArea
                    : Screen.FromPoint(Cursor.Position).WorkingArea;

                const int PadX = 26;
                const int PadY = 18;
                int btnH = TextRenderer.MeasureText(Lang.T("btn.ok"), f.Font).Height + 14;
                int bottomH = btnH + 24;

                int maxClientW = Math.Max(280, (int)(wa.Width * 0.9));
                int maxClientH = Math.Max(180, (int)(wa.Height * 0.9));

                // 先按"最宽允许宽度"折行测量，据此收窄窗体
                Size ts = TextRenderer.MeasureText(text, f.Font,
                    new Size(maxClientW - PadX * 2, int.MaxValue), TextFormatFlags.WordBreak);
                int clientW = Math.Min(maxClientW, ts.Width + PadX * 2 + 10);   // +10 防最后一字贴边
                int textW = Math.Max(120, clientW - PadX * 2);

                Panel body = new Panel();
                body.Dock = DockStyle.Fill;
                body.AutoScroll = true;

                Label lbl = new Label();
                lbl.Text = text;
                lbl.Font = f.Font;
                lbl.AutoSize = true;
                lbl.MaximumSize = new Size(textW - SystemInformation.VerticalScrollBarWidth, 0);
                lbl.TextAlign = ContentAlignment.MiddleCenter;
                lbl.UseMnemonic = false;
                lbl.Location = new Point(PadX, PadY);
                body.Controls.Add(lbl);
                if (lbl.Width < textW) { lbl.Left = PadX + (textW - lbl.Width) / 2; }

                int wantBodyH = lbl.Height + PadY * 2;
                int bodyH = Math.Min(wantBodyH, Math.Max(80, maxClientH - bottomH));
                f.ClientSize = new Size(clientW, bottomH + bodyH);
                if (wantBodyH > bodyH) { body.AutoScrollMinSize = new Size(clientW, wantBodyH); }

                Panel bottom = new Panel();
                bottom.Dock = DockStyle.Bottom;
                bottom.Height = bottomH;

                int okW = Math.Max(100, TextRenderer.MeasureText(Lang.T("btn.ok"), f.Font).Width + 44);
                Button ok = new Button();
                ok.Text = Lang.T("btn.ok");
                ok.Font = f.Font;
                ok.DialogResult = DialogResult.OK;
                ok.Size = new Size(okW, btnH);
                ok.Location = new Point((clientW - okW) / 2, (bottomH - btnH) / 2);
                bottom.Controls.Add(ok);

                // 先 Add 正文、后 Add 底部条：底部面板先按边布局，正文占其余区域
                f.Controls.Add(body);
                f.Controls.Add(bottom);
                f.AcceptButton = ok;
                f.CancelButton = ok;
                f.ShowDialog(owner);
            }
        }
    }
}
