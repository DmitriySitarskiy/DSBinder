using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using DSBinder.Core.Models;
using DSBinder.Core.Native;

namespace DSBinder
{
    /// <summary>
    /// Ігровий оверлей зі списком біндів. Ніколи не забирає фокус у гри (WS_EX_NOACTIVATE)
    /// і пропускає кліки крізь себе, поки не ввімкнено режим редагування.
    /// </summary>
    public partial class OverlayWindow : Window
    {
        private bool _dragging;
        private Point _dragStart;

        public bool IsEditMode { get; private set; }

        public OverlayWindow()
        {
            InitializeComponent();
            SourceInitialized += (_, _) => ApplyWindowStyles();
        }

        public void UpdateContent(IEnumerable<KeyBind> binds, double fontSize, bool binderEnabled,
                                  bool chatOpen, string? runningName)
        {
            var list = binds.ToList();
            BindsItems.ItemsSource = list;
            BindsItems.FontSize = fontSize;
            EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (binderEnabled)
            {
                StatusPill.Background = (System.Windows.Media.Brush)FindResource("SuccessSoftBrush");
                StatusDot.Fill = (System.Windows.Media.Brush)FindResource("SuccessBrush");
                StatusText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
                StatusText.Text = "УВІМК";
            }
            else
            {
                StatusPill.Background = (System.Windows.Media.Brush)FindResource("DangerSoftBrush");
                StatusDot.Fill = (System.Windows.Media.Brush)FindResource("DangerBrush");
                StatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
                StatusText.Text = "ВИМК";
            }

            if (!string.IsNullOrEmpty(runningName))
            {
                ActivityText.Text = "▶ " + runningName;
                ActivityText.Visibility = Visibility.Visible;
            }
            else if (binderEnabled && chatOpen)
            {
                ActivityText.Text = "Чат відкрито — буквені бінди на паузі";
                ActivityText.Visibility = Visibility.Visible;
            }
            else
            {
                ActivityText.Visibility = Visibility.Collapsed;
            }
        }

        public void SetEditMode(bool edit)
        {
            IsEditMode = edit;
            EditHint.Visibility = edit ? Visibility.Visible : Visibility.Collapsed;
            ResizeGrip.Visibility = edit ? Visibility.Visible : Visibility.Collapsed;
            Root.BorderBrush = edit
                ? (System.Windows.Media.Brush)FindResource("AccentBrush")
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            Root.Cursor = edit ? Cursors.SizeAll : null;
            ApplyWindowStyles();
        }

        public void ForceTopmost()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        /// <summary>Повертає вікно на екран, якщо збережена позиція виявилась за межами моніторів.</summary>
        public void EnsureOnScreen()
        {
            double l = SystemParameters.VirtualScreenLeft, t = SystemParameters.VirtualScreenTop;
            double r = l + SystemParameters.VirtualScreenWidth, b = t + SystemParameters.VirtualScreenHeight;
            if (Left < l - Width + 40 || Left > r - 40 || Top < t || Top > b - 40)
            {
                Left = 20;
                Top = 220;
            }
        }

        private void ApplyWindowStyles()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            style |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_LAYERED;
            if (IsEditMode) style &= ~NativeMethods.WS_EX_TRANSPARENT;
            else style |= NativeMethods.WS_EX_TRANSPARENT;
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, style);
        }

        private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsEditMode) return;
            _dragging = true;
            _dragStart = e.GetPosition(this);
            Root.CaptureMouse();
            e.Handled = true;
        }

        private void Root_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            Point p = e.GetPosition(this);
            Left += p.X - _dragStart.X;
            Top += p.Y - _dragStart.Y;
        }

        private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            Root.ReleaseMouseCapture();
        }

        private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
        {
            Width = Math.Clamp(Width + e.HorizontalChange, 180, 900);
        }
    }
}
