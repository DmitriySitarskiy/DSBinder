using System;
using System.Runtime.InteropServices;
using System.Threading;
using DSBinder.Core.Native;

namespace DSBinder.Core.Input
{
    /// <summary>
    /// Імітація клавіатури через SendInput зі скан-кодами (так їх «бачить» GTA/RAGE).
    /// Усі методи синхронні й викликаються з окремого потоку виконання макросів.
    /// </summary>
    public static class InputSimulator
    {
        private static readonly int InputSize = Marshal.SizeOf<NativeMethods.INPUT>();

        private static readonly int[] ModifierKeys =
        {
            NativeMethods.VK_LSHIFT, NativeMethods.VK_RSHIFT,
            NativeMethods.VK_LCONTROL, NativeMethods.VK_RCONTROL,
            NativeMethods.VK_LMENU, NativeMethods.VK_RMENU
        };

        public static void Delay(int ms, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (ms <= 0) return;
            if (ct.WaitHandle.WaitOne(ms))
                throw new OperationCanceledException(ct);
        }

        public static bool IsDown(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

        public static bool AnyModifierDown() =>
            IsDown(NativeMethods.VK_SHIFT) || IsDown(NativeMethods.VK_CONTROL) || IsDown(NativeMethods.VK_MENU);

        /// <summary>Відпускає модифікатори, які зараз утримуються (щоб T не перетворилось на Ctrl+T).</summary>
        public static void ReleaseHeldModifiers()
        {
            foreach (int vk in ModifierKeys)
            {
                if (IsDown(vk)) KeyUp((uint)vk);
            }
        }

        public static void KeyDown(uint vk) => SendKey(vk, keyUp: false);

        public static void KeyUp(uint vk) => SendKey(vk, keyUp: true);

        public static void Tap(uint vk, int holdMs, CancellationToken ct)
        {
            KeyDown(vk);
            try
            {
                Delay(holdMs, ct);
            }
            finally
            {
                KeyUp(vk); // клавіша ніколи не «залипне», навіть при скасуванні
            }
        }

        /// <summary>Ctrl+V скан-кодами.</summary>
        public static void PasteShortcut(CancellationToken ct)
        {
            const uint ctrl = NativeMethods.VK_LCONTROL;
            const uint v = NativeMethods.VK_V;

            KeyDown(ctrl);
            try
            {
                Delay(20, ct);
                KeyDown(v);
                try
                {
                    Delay(30, ct);
                }
                finally
                {
                    KeyUp(v);
                }
                Delay(20, ct);
            }
            finally
            {
                KeyUp(ctrl);
            }
        }

        /// <summary>Друкує текст посимвольно (Unicode), не використовуючи буфер обміну.</summary>
        public static void TypeUnicode(string text, int perCharDelayMs, CancellationToken ct)
        {
            foreach (char c in text)
            {
                if (c == '\r' || c == '\n') continue;

                var inputs = new[]
                {
                    UnicodeInput(c, keyUp: false),
                    UnicodeInput(c, keyUp: true)
                };
                NativeMethods.SendInput((uint)inputs.Length, inputs, InputSize);
                Delay(perCharDelayMs, ct);
            }
        }

        private static void SendKey(uint vk, bool keyUp)
        {
            ushort scan = (ushort)NativeMethods.MapVirtualKey(vk, NativeMethods.MAPVK_VK_TO_VSC);
            uint flags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0;

            var ki = new NativeMethods.KEYBDINPUT
            {
                time = 0,
                dwExtraInfo = NativeMethods.SYNTHETIC_KEY_EXTRA_INFO
            };

            if (scan != 0)
            {
                ki.wVk = 0;
                ki.wScan = scan;
                flags |= NativeMethods.KEYEVENTF_SCANCODE;
                if (IsExtendedKey(vk)) flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
            }
            else
            {
                ki.wVk = (ushort)vk; // запасний варіант для клавіш без скан-коду
            }

            ki.dwFlags = flags;

            var input = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                U = new NativeMethods.InputUnion { ki = ki }
            };

            NativeMethods.SendInput(1, new[] { input }, InputSize);
        }

        private static NativeMethods.INPUT UnicodeInput(char c, bool keyUp)
        {
            return new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                U = new NativeMethods.InputUnion
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = NativeMethods.KEYEVENTF_UNICODE | (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0),
                        time = 0,
                        dwExtraInfo = NativeMethods.SYNTHETIC_KEY_EXTRA_INFO
                    }
                }
            };
        }

        private static bool IsExtendedKey(uint vk) =>
            (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || vk == 0x6F || vk == 0x90
                || vk == 0xA3 /* RCtrl */ || vk == 0xA5 /* RAlt */
                || vk == 0x5B || vk == 0x5C || vk == 0x5D;
    }
}
