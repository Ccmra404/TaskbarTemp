using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace TaskbarTemp
{
    internal enum IconStyle { Number, Pill, Ring, Mono }

    internal static class IconRenderer
    {
        public static Color Accent(double t)
        {
            if (t < 55) return Color.FromArgb(255, 52, 199, 123);
            if (t < 70) return Color.FromArgb(255, 58, 160, 240);
            if (t < 82) return Color.FromArgb(255, 233, 168, 51);
            return Color.FromArgb(255, 226, 74, 74);
        }

        public static Color ColorFor(float? t) => t.HasValue ? Accent(t.Value) : Color.FromArgb(255, 150, 150, 150);

        public static Icon Render(string text, Color accent, int size, IconStyle style)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                switch (style)
                {
                    case IconStyle.Pill:
                        DrawPill(g, text, accent, size);
                        break;
                    case IconStyle.Ring:
                        DrawRing(g, text, accent, size);
                        break;
                    case IconStyle.Mono:
                        DrawMono(g, text, accent, size);
                        break;
                    default:
                        DrawNumber(g, text, accent, size);
                        break;
                }
            }

            IntPtr h = bmp.GetHicon();
            try
            {
                using var tmp = Icon.FromHandle(h);
                return (Icon)tmp.Clone();
            }
            finally
            {
                DestroyIcon(h);
                bmp.Dispose();
            }
        }

        // 纯数字 + 柔和阴影（浅色/深色任务栏都能看清）
        private static void DrawNumber(Graphics g, string text, Color accent, int size)
        {
            float fs = text.Length >= 3 ? size * 0.60f : size * 0.78f;
            using var font = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel);
            var sz = g.MeasureString(text, font);
            float x = (size - sz.Width) / 2f;
            float y = (size - sz.Height) / 2f - size * 0.06f;

            // 阴影（多方向偏移，模拟 1px 描边）
            float o = Math.Max(0.7f, size / 22f);
            using (var shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            {
                float[][] offsets = { new[] { -o, 0f }, new[] { o, 0f }, new[] { 0f, -o }, new[] { 0f, o },
                                      new[] { -o, -o }, new[] { o, -o }, new[] { -o, o }, new[] { o, o } };
                foreach (var off in offsets)
                    g.DrawString(text, font, shadow, x + off[0], y + off[1]);
            }

            using var br = new SolidBrush(accent);
            g.DrawString(text, font, br, x, y);
        }

        // Windows 11 原生风格：两行排版（小 CPU 标 + 大数字）、无底板
        // 深色任务栏 → 白色字；浅色任务栏 → 深灰字（随系统主题自动）
        private static Color DetectGlyphColor()
        {
            try
            {
                object v = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "SystemUsesLightTheme", 1);
                bool lightTheme = v is int i && i == 1;
                return lightTheme ? Color.FromArgb(255, 26, 26, 30) : Color.FromArgb(255, 245, 245, 245);
            }
            catch { return Color.FromArgb(255, 245, 245, 245); }
        }

        public static Color MonoGlyphColor => DetectGlyphColor();

        private static void DrawMono(Graphics g, string text, Color accent, int size)
        {
            Color glyph = MonoGlyphColor;

            float numFs = text.Length >= 3 ? size * (size <= 16 ? 0.54f : 0.60f) : size * (size <= 16 ? 0.76f : 0.80f);

            using var numFont = new Font("Segoe UI Variable Small Semibold", numFs, FontStyle.Regular, GraphicsUnit.Pixel);
            var nsz = g.MeasureString(text, numFont);

            float numX = (size - nsz.Width) / 2f;
            float numY = (size - nsz.Height) / 2f - size * 0.04f;

            using (var nb = new SolidBrush(glyph))
                g.DrawString(text, numFont, nb, numX, numY);
        }

        // 系统托盘真实图标尺寸（按主屏 DPI 缩放，渲染才锐利）
        public static int TrayIconSize
        {
            get
            {
                int sm = GetSystemMetrics(49); // SM_CXSMICON
                return sm >= 16 ? sm : 16;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        // 渐变圆角胶囊 + 白字
        private static void DrawPill(Graphics g, string text, Color accent, int size)
        {
            Color light = Color.FromArgb(255,
                Math.Min(255, accent.R + 45), Math.Min(255, accent.G + 45), Math.Min(255, accent.B + 45));
            Color dark = Color.FromArgb(255,
                Math.Max(0, accent.R - 40), Math.Max(0, accent.G - 40), Math.Max(0, accent.B - 40));

            var rect = new Rectangle(0, 0, size - 1, size - 1);
            using var path = Rounded(rect, Math.Max(3, size / 4));
            using (var br = new LinearGradientBrush(rect, light, dark, 90f))
                g.FillPath(br, path);

            float fs = text.Length >= 3 ? size * 0.40f : size * 0.50f;
            using var font = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel);
            var sz = g.MeasureString(text, font);
            using var white = new SolidBrush(Color.White);
            g.DrawString(text, font, white, (size - sz.Width) / 2f, (size - sz.Height) / 2f - size * 0.03f);
        }

        // 深底 + 彩色描边
        private static void DrawRing(Graphics g, string text, Color accent, int size)
        {
            var rect = new Rectangle(1, 1, size - 3, size - 3);
            using var path = Rounded(rect, Math.Max(3, size / 3));
            using (var bg = new SolidBrush(Color.FromArgb(255, 22, 24, 30)))
                g.FillPath(bg, path);
            using (var pen = new Pen(accent, Math.Max(1f, size / 16f)))
                g.DrawPath(pen, path);

            float fs = text.Length >= 3 ? size * 0.40f : size * 0.50f;
            using var font = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel);
            var sz = g.MeasureString(text, font);
            using var white = new SolidBrush(Color.White);
            g.DrawString(text, font, white, (size - sz.Width) / 2f, (size - sz.Height) / 2f - size * 0.03f);
        }

        private static GraphicsPath Rounded(Rectangle b, int r)
        {
            var p = new GraphicsPath();
            if (r <= 0) { p.AddRectangle(b); return p; }
            int d = r * 2;
            p.AddArc(b.X, b.Y, d, d, 180, 90);
            p.AddArc(b.Right - d, b.Y, d, d, 270, 90);
            p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
            p.AddArc(b.X, b.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);
    }

    internal static class Settings
    {
        private static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "config.ini");

        public static IconStyle Style
        {
            get
            {
                try
                {
                    if (File.Exists(Path))
                    {
                        foreach (var line in File.ReadAllLines(Path))
                        {
                            var s = line.Trim();
                            if (s.StartsWith("style", StringComparison.OrdinalIgnoreCase))
                            {
                                var v = s.Split('=', 2);
                                if (v.Length == 2)
                                {
                                    var val = v[1].Trim().ToLowerInvariant();
                                    if (val == "pill") return IconStyle.Pill;
                                    if (val == "ring") return IconStyle.Ring;
                                    if (val == "mono") return IconStyle.Mono;
                                }
                            }
                        }
                    }
                }
                catch { }
                return IconStyle.Mono;
            }
        }

        public static void EnsureDefault()
        {
            try
            {
                if (!File.Exists(Path))
                {
                    File.WriteAllText(Path,
                        "# TaskbarTemp 配置\r\n" +
                        "# style = mono | number | pill | ring\r\n" +
                        "#   mono   : Win11 原生风格，单色数字无底板（默认，随系统深浅主题自动换色）\r\n" +
                        "#   number : 彩色数字+阴影\r\n" +
                        "#   pill   : 渐变圆角胶囊+白字\r\n" +
                        "#   ring   : 深底+彩色描边\r\n" +
                        "style = mono\r\n");
                }
            }
            catch { }
        }
    }
}
