using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using DSBinder.Core.Models;
using DSBinder.Core.Native;
using DSBinder.Core.Services;

namespace DSBinder
{
    /// <summary>Картинка поверх гри (шпаргалка, мапа, кодекс тощо). Не забирає фокус у гри.</summary>
    public partial class ImageOverlayWindow : Window
    {
        private bool _dragging;
        private Point _dragStart;

        public KeyBind Bind { get; }
        public bool IsEditMode { get; private set; }

        public ImageOverlayWindow(KeyBind bind)
        {
            InitializeComponent();
            Bind = bind;

            Left = bind.ImageLeft;
            Top = bind.ImageTop;
            Width = bind.ImageWidth;
            Height = bind.ImageHeight;
            Opacity = bind.ImageOpacity;

            SourceInitialized += (_, _) => ApplyWindowStyles();
            _ = LoadImageAsync(bind.ImageUrl);
        }

        private async Task LoadImageAsync(string url)
        {
            var bitmap = await ImageCacheService.GetImageAsync(url);
            if (bitmap != null)
            {
                OverlayImage.Source = bitmap;
                StatusText.Visibility = Visibility.Collapsed;
                ImageBorder.Background = System.Windows.Media.Brushes.Transparent;
            }
            else
            {
                StatusText.Text = "Не вдалося завантажити картинку.\nПеревірте посилання або шлях до файлу.";
            }
        }

        /// <summary>Зберігає поточне положення/розмір у бінд.</summary>
        public void PersistToBind()
        {
            Bind.ImageLeft = Left;
            Bind.ImageTop = Top;
            Bind.ImageWidth = Width;
            Bind.ImageHeight = Height;
        }

        public void SetEditMode(bool edit)
        {
            IsEditMode = edit;
            ResizeGrip.Visibility = edit ? Visibility.Visible : Visibility.Collapsed;
            ImageBorder.BorderBrush = edit
                ? (System.Windows.Media.Brush)FindResource("AccentBrush")
                : System.Windows.Media.Brushes.Transparent;
            ImageBorder.Cursor = edit ? Cursors.SizeAll : null;
            if (!edit) PersistToBind();
            ApplyWindowStyles();
        }

        public void ForceTopmost()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
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
            ImageBorder.CaptureMouse();
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
            ImageBorder.ReleaseMouseCapture();
        }

        private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
        {
            Width = Math.Clamp(Width + e.HorizontalChange, 80, 4000);
            Height = Math.Clamp(Height + e.VerticalChange, 60, 4000);
        }
    }
}
