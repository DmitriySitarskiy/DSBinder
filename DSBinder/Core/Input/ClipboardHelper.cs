using System;
using System.Runtime.InteropServices;
using System.Threading;
using DSBinder.Core.Native;

namespace DSBinder.Core.Input
{
    /// <summary>
    /// Робота з буфером обміну напряму через Win32 — з повторними спробами.
    /// Стара версія при помилці «буфер зайнятий» просто вставляла в чат попередній вміст буфера.
    /// </summary>
    public static class ClipboardHelper
    {
        public static bool TrySetText(string text, int attempts = 20)
        {
            for (int i = 0; i < attempts; i++)
            {
                if (NativeMethods.OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        if (!NativeMethods.EmptyClipboard()) return false;

                        IntPtr hMem = AllocUnicode(text);
                        if (hMem == IntPtr.Zero) return false;

                        if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, hMem) == IntPtr.Zero)
                        {
                            NativeMethods.GlobalFree(hMem);
                            return false;
                        }
                        return true; // після успіху пам'ять належить системі
                    }
                    finally
                    {
                        NativeMethods.CloseClipboard();
                    }
                }
                Thread.Sleep(15);
            }
            return false;
        }

        public static string? TryGetText(int attempts = 10)
        {
            if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT)) return null;

            for (int i = 0; i < attempts; i++)
            {
                if (NativeMethods.OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        IntPtr hData = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
                        if (hData == IntPtr.Zero) return null;

                        IntPtr ptr = NativeMethods.GlobalLock(hData);
                        if (ptr == IntPtr.Zero) return null;
                        try
                        {
                            return Marshal.PtrToStringUni(ptr);
                        }
                        finally
                        {
                            NativeMethods.GlobalUnlock(hData);
                        }
                    }
                    finally
                    {
                        NativeMethods.CloseClipboard();
                    }
                }
                Thread.Sleep(15);
            }
            return null;
        }

        private static IntPtr AllocUnicode(string text)
        {
            int bytes = (text.Length + 1) * 2;
            IntPtr hMem = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)bytes);
            if (hMem == IntPtr.Zero) return IntPtr.Zero;

            IntPtr ptr = NativeMethods.GlobalLock(hMem);
            if (ptr == IntPtr.Zero)
            {
                NativeMethods.GlobalFree(hMem);
                return IntPtr.Zero;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                Marshal.WriteInt16(ptr, text.Length * 2, 0);
            }
            finally
            {
                NativeMethods.GlobalUnlock(hMem);
            }
            return hMem;
        }
    }
}
