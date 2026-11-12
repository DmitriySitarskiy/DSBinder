using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using DSBinder.Core.Native;
using DSBinder.Core.Services;

namespace DSBinder.Core.Hooks
{
    public readonly record struct KeyEventRecord(uint VkCode, uint ScanCode, bool IsKeyDown, bool IsExtended, uint Time);

    /// <summary>
    /// Глобальний низькорівневий хук клавіатури, який працює у ВЛАСНОМУ потоці з власним циклом повідомлень.
    ///
    /// Чому так: Windows мовчки знімає LL-хук, якщо його обробник відповідає довше ~300–1000 мс.
    /// У старій версії хук жив у UI-потоці і всередині колбеку шукав процес гри (Process.GetProcessById),
    /// тому під навантаженням Windows «вимикала» біндер, і він переставав реагувати до перезапуску.
    /// Тепер обробник лише читає кешовані значення і миттєво повертається.
    /// </summary>
    public sealed class GlobalKeyboardHook : IDisposable
    {
        private const uint WM_REINSTALL = NativeMethods.WM_APP + 1;

        private readonly NativeMethods.LowLevelKeyboardProc _proc;
        private readonly Func<KeyEventRecord, bool> _handler;
        private readonly ManualResetEventSlim _ready = new(false);

        private Thread? _thread;
        private volatile uint _threadId;
        private IntPtr _hookId = IntPtr.Zero;
        private Exception? _startError;
        private bool _disposed;

        /// <param name="handler">Повертає true, якщо натискання треба заблокувати (не передавати у гру).</param>
        public GlobalKeyboardHook(Func<KeyEventRecord, bool> handler)
        {
            _handler = handler;
            _proc = HookCallback; // тримаємо посилання, щоб GC не зібрав делегат
        }

        public void Start()
        {
            if (_thread != null) return;

            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "DSBinder.KeyboardHook",
                Priority = ThreadPriority.Highest
            };
            _thread.Start();

            if (!_ready.Wait(5000))
                throw new InvalidOperationException("Хук клавіатури не запустився вчасно.");
            if (_startError != null)
                throw _startError;
        }

        /// <summary>Перевстановлює хук (страховка на випадок, якщо Windows його зняла).</summary>
        public void Reinstall()
        {
            uint id = _threadId;
            if (id != 0) NativeMethods.PostThreadMessage(id, WM_REINSTALL, IntPtr.Zero, IntPtr.Zero);
        }

        private void ThreadMain()
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            // Створюємо чергу повідомлень потоку до того, як хтось надішле PostThreadMessage.
            NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);

            if (!Install())
            {
                _startError = new Win32Exception(Marshal.GetLastWin32Error(), "Не вдалося встановити глобальний хук клавіатури.");
                _ready.Set();
                return;
            }

            _ready.Set();

            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.hwnd == IntPtr.Zero && msg.message == WM_REINSTALL)
                {
                    Uninstall();
                    if (!Install())
                        Logger.Warn("Перевстановлення хука не вдалося: " + Marshal.GetLastWin32Error());
                    continue;
                }

                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }

            Uninstall();
        }

        private bool Install()
        {
            IntPtr module = NativeMethods.GetModuleHandle(null);
            _hookId = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, module, 0);
            return _hookId != IntPtr.Zero;
        }

        private void Uninstall()
        {
            if (_hookId == IntPtr.Zero) return;
            NativeMethods.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

                // Натискання, згенеровані самим біндером, пропускаємо без обробки.
                if (data.dwExtraInfo != NativeMethods.SYNTHETIC_KEY_EXTRA_INFO)
                {
                    int msg = wParam.ToInt32();
                    bool down = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
                    bool up = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;

                    if (down || up)
                    {
                        bool suppress = false;
                        try
                        {
                            suppress = _handler(new KeyEventRecord(
                                data.vkCode,
                                data.scanCode,
                                down,
                                (data.flags & NativeMethods.LLKHF_EXTENDED) != 0,
                                data.time));
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("Обробник клавіатури", ex);
                        }

                        if (suppress) return new IntPtr(1);
                    }
                }
            }

            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            uint id = _threadId;
            if (id != 0) NativeMethods.PostThreadMessage(id, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread?.Join(1000);
            _ready.Dispose();
        }
    }
}
