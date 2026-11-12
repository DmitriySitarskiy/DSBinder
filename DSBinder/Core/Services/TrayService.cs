using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace DSBinder.Core.Services
{
    /// <summary>Іконка в треї з меню та індикацією стану (жовта — увімкнено, сіра — вимкнено).</summary>
    public sealed class TrayService : IDisposable
    {
        private readonly WinForms.NotifyIcon _icon;
        private readonly Drawing.Icon _onIcon;
        private readonly Drawing.Icon _offIcon;
        private readonly WinForms.ToolStripMenuItem _toggleItem;
        private readonly WinForms.ToolStripMenuItem _overlayItem;

        public event Action? OpenRequested;
        public event Action? ToggleRequested;
        public event Action? OverlayRequested;
        public event Action? ExitRequested;

        public System.Windows.Media.ImageSource WindowIcon { get; }

        public TrayService()
        {
            _onIcon = CreateIcon(Drawing.Color.FromArgb(245, 165, 36));
            _offIcon = CreateIcon(Drawing.Color.FromArgb(92, 100, 114));

            WindowIcon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(_onIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            WindowIcon.Freeze();

            var menu = new WinForms.ContextMenuStrip();
            var openItem = new WinForms.ToolStripMenuItem("Відкрити DSBinder", null, (_, _) => OpenRequested?.Invoke());
            openItem.Font = new Drawing.Font(openItem.Font, Drawing.FontStyle.Bold);
            _toggleItem = new WinForms.ToolStripMenuItem("Біндер увімкнено", null, (_, _) => ToggleRequested?.Invoke());
            _overlayItem = new WinForms.ToolStripMenuItem("Показувати оверлей", null, (_, _) => OverlayRequested?.Invoke());
            var exitItem = new WinForms.ToolStripMenuItem("Вийти", null, (_, _) => ExitRequested?.Invoke());

            menu.Items.Add(openItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(_toggleItem);
            menu.Items.Add(_overlayItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(exitItem);

            _icon = new WinForms.NotifyIcon
            {
                Icon = _onIcon,
                Text = "DSBinder",
                Visible = true,
                ContextMenuStrip = menu
            };
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left) OpenRequested?.Invoke();
            };
        }

        public void SetState(bool enabled, bool overlayVisible)
        {
            _icon.Icon = enabled ? _onIcon : _offIcon;
            _icon.Text = enabled ? "DSBinder — увімкнено" : "DSBinder — вимкнено";
            _toggleItem.Checked = enabled;
            _overlayItem.Checked = overlayVisible;
        }

        public void ShowBalloon(string title, string text)
        {
            _icon.ShowBalloonTip(2500, title, text, WinForms.ToolTipIcon.Info);
        }

        private static Drawing.Icon CreateIcon(Drawing.Color color)
        {
            using var bmp = new Drawing.Bitmap(32, 32);
            using (var g = Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Drawing.Color.Transparent);

                using (var path = RoundedRect(new Drawing.RectangleF(1, 1, 30, 30), 8))
                using (var brush = new Drawing.SolidBrush(color))
                {
                    g.FillPath(brush, path);
                }

                using var font = new Drawing.Font("Segoe UI", 14f, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
                using var textBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(20, 16, 10));
                using var format = new Drawing.StringFormat
                {
                    Alignment = Drawing.StringAlignment.Center,
                    LineAlignment = Drawing.StringAlignment.Center
                };
                g.DrawString("DS", font, textBrush, new Drawing.RectangleF(0, 1, 32, 32), format);
            }

            IntPtr hIcon = bmp.GetHicon();
            try
            {
                using var temp = Drawing.Icon.FromHandle(hIcon);
                return (Drawing.Icon)temp.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }

        private static Drawing.Drawing2D.GraphicsPath RoundedRect(Drawing.RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new Drawing.Drawing2D.GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            _onIcon.Dispose();
            _offIcon.Dispose();
        }
    }
}
