using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using DSBinder.Core.Native;

namespace DSBinder
{
    /// <summary>
    /// Швидкий пошук по законодавству поверх гри (гаряча клавіша).
    /// Після Esc фокус повертається у гру.
    /// </summary>
    public partial class QuickLawWindow : Window
    {
        private IntPtr _previousWindow = IntPtr.Zero;
        private bool _hiding;

        public QuickLawWindow()
        {
            InitializeComponent();
            Search.EscapePressed += () => HideAndReturn(restoreFocus: true);
            Deactivated += (_, _) => HideAndReturn(restoreFocus: false);
        }

        public void Toggle()
        {
            if (IsVisible) HideAndReturn(restoreFocus: true);
            else ShowOverGame();
        }

        public void ShowOverGame()
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            IntPtr own = new WindowInteropHelper(this).Handle;
            if (fg != own) _previousWindow = fg;

            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + Math.Max(20, (area.Height - Height) / 2 - 40);

            Show();
            Activate();
            ForceForeground(new WindowInteropHelper(this).Handle);
            Search.FocusSearch();
        }

        public void HideAndReturn(bool restoreFocus)
        {
            if (_hiding || !IsVisible) return;
            _hiding = true;
            try
            {
                Hide();
                if (restoreFocus && _previousWindow != IntPtr.Zero && NativeMethods.IsWindow(_previousWindow))
                {
                    NativeMethods.SetForegroundWindow(_previousWindow);
                }
            }
            finally
            {
                _hiding = false;
            }
        }

        /// <summary>
        /// Windows не дозволяє фоновій програмі просто так забрати фокус у гри.
        /// Тимчасово під'єднуємось до потоку вводу активного вікна — так фокус передається надійно.
        /// </summary>
        private static void ForceForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            IntPtr fg = NativeMethods.GetForegroundWindow();
            uint fgThread = NativeMethods.GetWindowThreadProcessId(fg, out _);
            uint ourThread = NativeMethods.GetCurrentThreadId();
            bool attached = false;

            try
            {
                if (fgThread != 0 && fgThread != ourThread)
                    attached = NativeMethods.AttachThreadInput(ourThread, fgThread, true);

                NativeMethods.BringWindowToTop(hwnd);
                NativeMethods.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) NativeMethods.AttachThreadInput(ourThread, fgThread, false);
            }
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => HideAndReturn(restoreFocus: true);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                HideAndReturn(restoreFocus: true);
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }
    }
}
