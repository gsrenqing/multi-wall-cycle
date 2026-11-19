using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MultiWallCycle
{
    /// <summary>
    /// 文字描述设置对话框（v1.1.2，对齐"动态壁纸"嵌入描述面板）：
    /// 顶部为每屏独立的"显示文字描述"开关；下方为全局样式（字体/样式/大小/颜色/位置/阴影）。
    ///
    /// v2.3 刷新重制（DPI 相关重做，2026-11-13）：
    /// 1. 全窗关闭 AutoScaleMode.Font —— 本程序按系统 DPI 虚拟化运行，再让 WinForms 按字体缩放一次
    ///    就会与写死的行高打架，出现"文字被吃半行"。改为 AutoScaleMode.None，坐标一律用 96 DPI 逻辑像素。
    /// 2. **所有行高/控件高度都由字体度量算出来**（TextRenderer.MeasureText），不再出现 24/26 这类魔法数字；
    ///    字体换大、DPI 变高，行高会自己跟着长，不会再压字。
    /// 3. 窗体高度 = 内容实际高度 + 底部按钮条，**并钳制到当前屏幕工作区的 85%**；
    ///    内容超出时内容区自动出现滚动条（滚轮/拖动都可用），按钮条始终贴底可见。
    /// 4. 底部「确定/取消」的位置改用窗体真实客户区宽度计算（旧写法在面板还没 Dock 成型时取宽度，
    ///    算成负坐标，按钮会跑到窗口左边界外）。
    /// </summary>
    public class CaptionSettingsForm : Form
    {
        private CheckBox _chkShow;
        private ComboBox _cboFont;
        private ComboBox _cboStyle;
        private TrackBar _trkSize;
        private Label _lblSize;
        private Button _btnFontColor;
        private ComboBox _cboPos;
        private CheckBox _chkShadow;
        private TrackBar _trkShadow;
        private Label _lblShadow;
        private Button _btnShadowColor;
        private Button _btnOk;
        private Button _btnCancel;

        private const int PadX = 18;
        private const int PadY = 12;

        /// <summary>确定后：该屏是否显示文字描述（每屏独立草稿）。</summary>
        public bool ShowCaption { get; private set; }
        /// <summary>确定后：全局样式（草稿）。</summary>
        public CaptionStyle Style { get; private set; }

        public CaptionSettingsForm(bool showCaption, CaptionStyle style)
        {
            Text = Lang.T("cap.title");
            Font textFont = new Font("Microsoft YaHei UI", 9.5F);
            Font boldFont = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
            Font = textFont;
            AutoScaleMode = AutoScaleMode.None;      // v2.3：禁止按字体二次缩放
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;

            Style = (style != null) ? CloneStyle(style) : new CaptionStyle();

            // ---- 度量（一律由字体算，不写死像素） ----
            int lineH = TextRenderer.MeasureText("国Ag", textFont).Height;       // 9.5F 的实际行高
            int cboH = lineH + 8;                                                  // 组合框内容高度
            int btnH = lineH + 12;                                                 // 小按钮高度
            int chkH = lineH + 8;
            int rowGap = 6;                                                        // 标签与控件之间
            int secGap = 16;                                                       // 段与段之间
            int trkH = lineH + 26;                                                 // 轨道条（含刻度）

            int clientW = 440;
            // 预留 16px 右侧余量：内容超长出现纵向滚动条时，右侧控件不会被滚动条压住
            int contentW = clientW - PadX * 2 - 16;
            int col1X = PadX;
            int col1W = 205;
            int col2X = PadX + col1W + 14;
            int col2W = contentW - col1W - 14;

            // 底部固定按钮条
            Panel btnPanel = new Panel();
            btnPanel.Dock = DockStyle.Bottom;
            btnPanel.Height = btnH + 24;

            Panel scroll = new Panel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            scroll.BorderStyle = BorderStyle.None;
            // 与主窗口/关于弹窗一致：Fill 先 Add、Bottom 后 Add，底部条才能正确占边
            Controls.Add(scroll);
            Controls.Add(btnPanel);

            int y = PadY;

            // ---- 显示文字描述（每屏） ----
            _chkShow = new CheckBox();
            _chkShow.Text = Lang.T("cap.show");
            _chkShow.Checked = showCaption;
            _chkShow.Font = boldFont;
            _chkShow.AutoSize = true;
            _chkShow.Location = new Point(col1X, y);
            scroll.Controls.Add(_chkShow);
            y = _chkShow.Bottom + secGap;

            // ---- 字体 ----
            Label lblFont = MakeLabel(Lang.T("cap.font"), textFont, col1X, y);
            scroll.Controls.Add(lblFont);
            _cboFont = new ComboBox();
            _cboFont.DropDownStyle = ComboBoxStyle.DropDownList;
            List<string> families = new List<string>();
            foreach (FontFamily ff in FontFamily.Families)
            {
                try { families.Add(ff.Name); } catch { }
            }
            families.Sort(StringComparer.Ordinal);
            _cboFont.Items.AddRange(families.ToArray());
            int fontIdx = families.IndexOf(Style.FontFamily);
            _cboFont.SelectedIndex = (fontIdx >= 0) ? fontIdx : Math.Max(0, families.IndexOf("Microsoft YaHei UI"));
            _cboFont.Location = new Point(col1X, lblFont.Bottom + rowGap);
            _cboFont.Size = new Size(col1W, cboH);
            scroll.Controls.Add(_cboFont);

            // ---- 样式（左） / 大小（右，同一段） ----
            int yStyle = _cboFont.Bottom + secGap;
            Label lblStyle = MakeLabel(Lang.T("cap.style"), textFont, col1X, yStyle);
            scroll.Controls.Add(lblStyle);
            _cboStyle = new ComboBox();
            _cboStyle.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboStyle.Items.AddRange(new object[] {
                Lang.T("cap.style.normal"), Lang.T("cap.style.bold"), Lang.T("cap.style.italic")
            });
            _cboStyle.SelectedIndex = Math.Max(0, Math.Min(2, Style.Style));
            _cboStyle.Location = new Point(col1X, lblStyle.Bottom + rowGap);
            _cboStyle.Size = new Size(col1W, cboH);
            scroll.Controls.Add(_cboStyle);

            Label lblSize = MakeLabel(Lang.T("cap.size"), textFont, col2X, yStyle);
            scroll.Controls.Add(lblSize);
            _lblSize = new Label();
            _lblSize.Text = Style.FontSize.ToString();
            _lblSize.Font = textFont;
            _lblSize.AutoSize = true;
            _lblSize.Location = new Point(col2X + col2W - 40, yStyle);
            scroll.Controls.Add(_lblSize);
            _trkSize = new TrackBar();
            _trkSize.Minimum = 8;
            _trkSize.Maximum = 80;
            _trkSize.Value = Math.Max(8, Math.Min(80, Style.FontSize));
            _trkSize.TickFrequency = 8;
            _trkSize.Location = new Point(col2X - 6, lblSize.Bottom + rowGap - 4);
            _trkSize.Size = new Size(col2W + 6, trkH);
            _trkSize.ValueChanged += delegate { _lblSize.Text = _trkSize.Value.ToString(); };
            scroll.Controls.Add(_trkSize);

            int yAfterSize = Math.Max(_cboStyle.Bottom, _trkSize.Bottom) + secGap;

            // ---- 字体颜色（左） / 位置（右） ----
            Label lblColor = MakeLabel(Lang.T("cap.fontcolor"), textFont, col1X, yAfterSize);
            scroll.Controls.Add(lblColor);
            _btnFontColor = new Button();
            _btnFontColor.Text = Lang.T("cap.pickcolor");
            _btnFontColor.Font = textFont;
            _btnFontColor.Location = new Point(col1X, lblColor.Bottom + rowGap);
            _btnFontColor.Size = new Size(120, btnH);
            _btnFontColor.BackColor = FromHex(Style.FontColor, Color.White);
            _btnFontColor.Click += delegate
            {
                using (ColorDialog dlg = new ColorDialog())
                {
                    dlg.Color = FromHex(Style.FontColor, Color.White);
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        Style.FontColor = ToHex(dlg.Color);
                        _btnFontColor.BackColor = dlg.Color;
                    }
                }
            };
            scroll.Controls.Add(_btnFontColor);

            Label lblPos = MakeLabel(Lang.T("cap.position"), textFont, col2X, yAfterSize);
            scroll.Controls.Add(lblPos);
            _cboPos = new ComboBox();
            _cboPos.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboPos.Items.AddRange(new object[] {
                Lang.T("cap.pos.lb"), Lang.T("cap.pos.bc"), Lang.T("cap.pos.rb"),
                Lang.T("cap.pos.lt"), Lang.T("cap.pos.tc"), Lang.T("cap.pos.rt")
            });
            _cboPos.SelectedIndex = Math.Max(0, Math.Min(5, Style.Position));
            _cboPos.Location = new Point(col2X, lblPos.Bottom + rowGap);
            _cboPos.Size = new Size(col2W, cboH);
            scroll.Controls.Add(_cboPos);

            int yEffects = Math.Max(_btnFontColor.Bottom, _cboPos.Bottom) + secGap;

            // ---- 视觉效果 ----
            Label grp = MakeLabel(Lang.T("cap.effects"), boldFont, col1X, yEffects);
            scroll.Controls.Add(grp);

            _chkShadow = new CheckBox();
            _chkShadow.Text = Lang.T("cap.shadow");
            _chkShadow.Font = textFont;
            _chkShadow.Checked = Style.Shadow;
            _chkShadow.AutoSize = true;
            _chkShadow.Location = new Point(col1X, grp.Bottom + rowGap);
            _chkShadow.CheckedChanged += delegate { UpdateShadowEnabled(); };
            scroll.Controls.Add(_chkShadow);

            Label lblShadowSize = MakeLabel(Lang.T("cap.shadowsize"), textFont, col2X, _chkShadow.Top - 2);
            scroll.Controls.Add(lblShadowSize);
            _lblShadow = new Label();
            _lblShadow.Text = Style.ShadowSize.ToString();
            _lblShadow.Font = textFont;
            _lblShadow.AutoSize = true;
            _lblShadow.Location = new Point(col2X + col2W - 40, _chkShadow.Top - 2);
            scroll.Controls.Add(_lblShadow);

            _trkShadow = new TrackBar();
            _trkShadow.Minimum = 1;
            _trkShadow.Maximum = 8;
            _trkShadow.Value = Math.Max(1, Math.Min(8, Style.ShadowSize));
            _trkShadow.TickFrequency = 1;
            _trkShadow.Location = new Point(col2X - 6, _chkShadow.Bottom + rowGap - 2);
            _trkShadow.Size = new Size(col2W + 6, trkH);
            _trkShadow.ValueChanged += delegate { _lblShadow.Text = _trkShadow.Value.ToString(); };
            scroll.Controls.Add(_trkShadow);

            Label lblShadowColor = MakeLabel(Lang.T("cap.shadowcolor"), textFont, col1X,
                Math.Max(_chkShadow.Bottom, _trkShadow.Bottom) + secGap - 4);
            scroll.Controls.Add(lblShadowColor);
            _btnShadowColor = new Button();
            _btnShadowColor.Text = Lang.T("cap.pickcolor");
            _btnShadowColor.Font = textFont;
            _btnShadowColor.Location = new Point(col1X, lblShadowColor.Bottom + rowGap);
            _btnShadowColor.Size = new Size(120, btnH);
            _btnShadowColor.BackColor = FromHex(Style.ShadowColor, Color.Black);
            _btnShadowColor.Click += delegate
            {
                using (ColorDialog dlg = new ColorDialog())
                {
                    dlg.Color = FromHex(Style.ShadowColor, Color.Black);
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        Style.ShadowColor = ToHex(dlg.Color);
                        _btnShadowColor.BackColor = dlg.Color;
                    }
                }
            };
            scroll.Controls.Add(_btnShadowColor);

            // ---- 内容区所需高度 → 窗体高度（钳制到屏幕工作区 85%） ----
            int contentBottom = Math.Max(_btnShadowColor.Bottom, _trkShadow.Bottom) + PadY;
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            int bodyMax = Math.Max(200, (int)(wa.Height * 0.85) - btnPanel.Height);
            int bodyH = Math.Min(contentBottom, bodyMax);
            if (contentBottom > bodyMax)
            {
                // 内容放不下 → 内容区可纵向滚动（不设横向滚动，右侧已留了滚动条余量）
                scroll.AutoScrollMinSize = new Size(0, contentBottom);
            }
            ClientSize = new Size(clientW, btnPanel.Height + bodyH);

            // ---- 底部按钮：用真实客户区宽度定位（右对齐） ----
            int btnW = TextRenderer.MeasureText(Lang.T("btn.cancel"), textFont).Width + 44;
            _btnOk = new Button();
            _btnOk.Text = Lang.T("btn.ok");
            _btnOk.Font = textFont;
            _btnOk.Size = new Size(btnW, btnH);
            _btnOk.Location = new Point(ClientSize.Width - PadX - btnW * 2 - 10, (btnPanel.Height - btnH) / 2);
            _btnOk.DialogResult = DialogResult.OK;
            _btnOk.Click += delegate { Commit(); };
            btnPanel.Controls.Add(_btnOk);

            _btnCancel = new Button();
            _btnCancel.Text = Lang.T("btn.cancel");
            _btnCancel.Font = textFont;
            _btnCancel.Size = new Size(btnW, btnH);
            _btnCancel.Location = new Point(ClientSize.Width - PadX - btnW, (btnPanel.Height - btnH) / 2);
            _btnCancel.DialogResult = DialogResult.Cancel;
            btnPanel.Controls.Add(_btnCancel);

            UpdateShadowEnabled();
        }

        private void UpdateShadowEnabled()
        {
            bool on = _chkShadow.Checked;
            _trkShadow.Enabled = on;
            _lblShadow.Enabled = on;
            _btnShadowColor.Enabled = on;
        }

        private static string ToHex(Color c)
        {
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        private void Commit()
        {
            ShowCaption = _chkShow.Checked;
            Style.FontFamily = (_cboFont.SelectedItem != null) ? _cboFont.SelectedItem.ToString() : "Microsoft YaHei UI";
            Style.Style = Math.Max(0, Math.Min(2, _cboStyle.SelectedIndex));
            Style.FontSize = _trkSize.Value;
            Style.Position = Math.Max(0, Math.Min(5, _cboPos.SelectedIndex));
            Style.Shadow = _chkShadow.Checked;
            Style.ShadowSize = _trkShadow.Value;
        }

        private static Label MakeLabel(string text, Font font, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = font;
            l.Location = new Point(x, y);
            l.AutoSize = true;
            return l;
        }

        private static Color FromHex(string hex, Color fallback)
        {
            try
            {
                if (!string.IsNullOrEmpty(hex)) { return ColorTranslator.FromHtml(hex); }
            }
            catch { }
            return fallback;
        }

        private static CaptionStyle CloneStyle(CaptionStyle s)
        {
            CaptionStyle c = new CaptionStyle();
            c.FontFamily = s.FontFamily;
            c.Style = s.Style;
            c.FontSize = s.FontSize;
            c.FontColor = s.FontColor;
            c.Position = s.Position;
            c.Shadow = s.Shadow;
            c.ShadowSize = s.ShadowSize;
            c.ShadowColor = s.ShadowColor;
            return c;
        }
    }
}
