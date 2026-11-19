using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace MultiWallCycle
{
    /// <summary>
    /// 程序图标。v2.3 第二轮：优先使用编译进程序的 app.ico（多尺寸 16~256），
    /// 这样窗口、托盘、任务栏与资源管理器里看到的是同一个图标；
    /// 只有在资源缺失（用 csc 手编时漏了 /resource）时才回退到下面的运行时绘制。
    /// 返回的每个 Icon 都是独立副本，Form 与 NotifyIcon 各自持有/释放，互不影响。
    /// </summary>
    public static class IconFactory
    {
        private const string ResourceName = "app.ico";
        private static Icon _cached;

        public static Icon CreateIcon()
        {
            Icon embedded = LoadEmbedded();
            if (embedded != null) { return embedded; }

            if (_cached == null)
            {
                Bitmap bmp = new Bitmap(32, 32);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    // 左侧屏幕（蓝）
                    using (SolidBrush b1 = new SolidBrush(Color.FromArgb(37, 99, 235)))
                    {
                        g.FillRectangle(b1, 3, 7, 13, 9);
                    }
                    // 右侧屏幕（绿）
                    using (SolidBrush b2 = new SolidBrush(Color.FromArgb(16, 185, 129)))
                    {
                        g.FillRectangle(b2, 16, 14, 13, 9);
                    }
                    // 顶部时钟（白盘 + 深靛蓝轮廓/指针，浅底深底都读得出来）
                    using (SolidBrush face = new SolidBrush(Color.White))
                    {
                        g.FillEllipse(face, 21, 2, 9.2f, 9.2f);
                    }
                    using (Pen p = new Pen(Color.FromArgb(30, 41, 59), 1.2f))
                    {
                        g.DrawEllipse(p, 21, 2, 9.2f, 9.2f);
                    }
                    using (SolidBrush w = new SolidBrush(Color.FromArgb(30, 41, 59)))
                    {
                        g.FillRectangle(w, 24.6f, 4.6f, 1.2f, 2.6f);
                        g.FillRectangle(w, 25.5f, 7.0f, 3f, 1.2f);
                    }
                }
                IntPtr hIcon = bmp.GetHicon();
                _cached = Icon.FromHandle(hIcon);
                bmp.Dispose();
            }
            return (Icon)_cached.Clone();
        }

        /// <summary>读取编译期嵌入的 app.ico（csc /resource:"源码\app.ico",app.ico）。失败返回 null。</summary>
        private static Icon LoadEmbedded()
        {
            try
            {
                using (Stream s = typeof(IconFactory).Assembly.GetManifestResourceStream(ResourceName))
                {
                    if (s == null) { return null; }
                    return new Icon(s);   // Icon(Stream) 会把整段数据读进内存，流可安全释放
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
