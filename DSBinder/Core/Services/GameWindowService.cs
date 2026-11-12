using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DSBinder.Core.Native;

namespace DSBinder.Core.Services
{
    /// <summary>
    /// Фоново (кожні 150 мс) визначає, чи у фокусі вікно GTA 5, і кешує результат.
    /// Хук клавіатури лише читає готове значення — без жодних повільних викликів.
    /// </summary>
    public sealed class GameWindowService : IDisposable
    {
        private static readonly string[] DefaultProcessNames =
        {
            "GTA5", "GTA5_Enhanced", "GTA5_BE", "PlayGTAV",
            "ragemp_v", "ragemp_game", "FiveM", "altv", "altv-client"
        };

        private static readonly string[] TitleHints =
        {
            "Grand Theft Auto V", "RAGE Multiplayer", "alt:V Multiplayer"
        };

        private readonly Timer _timer;
        private readonly uint _ownPid = (uint)Environment.ProcessId;
        private readonly Dictionary<uint, string> _pidNames = new();

        private volatile string[] _customNames = Array.Empty<string>();
        private volatile bool _isGameActive;
        private volatile bool _isOwnWindowActive;
        private volatile bool _isGameRunning;

        private IntPtr _lastHwnd = new(-1);
        private int _tick;
        private int _busy;

        public bool IsGameActive => _isGameActive;
        public bool IsOwnWindowActive => _isOwnWindowActive;
        public bool IsGameRunning => _isGameRunning;

        /// <summary>Змінився фокус гри або стан «гра запущена». Викликається з фонового потоку.</summary>
        public event Action? StatusChanged;

        /// <summary>Гра отримала фокус (true) або втратила його (false). Викликається з фонового потоку.</summary>
        public event Action<bool>? GameFocusChanged;

        public GameWindowService()
        {
            _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start() => _timer.Change(0, 150);

        public void SetCustomProcesses(IEnumerable<string> names)
        {
            _customNames = names
                .Select(n => Path.GetFileNameWithoutExtension(n.Trim()))
                .Where(n => n.Length > 0)
                .ToArray();
            _lastHwnd = new IntPtr(-1); // перерахувати при наступному тіку
        }

        private void Tick()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                _tick++;
                bool forceRefresh = _tick % 14 == 0; // ~раз на 2 с перевіряємо навіть те саме вікно

                IntPtr hwnd = NativeMethods.GetForegroundWindow();
                bool game = _isGameActive;
                bool own = _isOwnWindowActive;

                if (hwnd != _lastHwnd || forceRefresh)
                {
                    _lastHwnd = hwnd;
                    game = false;
                    own = false;

                    if (hwnd != IntPtr.Zero)
                    {
                        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                        if (pid == _ownPid)
                        {
                            own = true; 
                        }
                        else if (pid != 0)
                        {
                            string name = GetProcessName(pid);
                            game = IsGameProcess(name) || TitleLooksLikeGame(hwnd);
                        }
                    }
                }

                bool running = _isGameRunning;
                if (_tick % 20 == 1) running = game || DetectRunningGame(); // ~раз на 3 с
                if (game) running = true;

                bool focusChanged = game != _isGameActive;
                bool statusChanged = focusChanged || running != _isGameRunning;

                _isGameActive = game;
                _isOwnWindowActive = own;
                _isGameRunning = running;

                if (focusChanged) GameFocusChanged?.Invoke(game);
                if (statusChanged) StatusChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Error("GameWindowService", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        private bool IsGameProcess(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            foreach (var n in DefaultProcessNames)
                if (name.Equals(n, StringComparison.OrdinalIgnoreCase)) return true;

            foreach (var n in _customNames)
                if (name.Equals(n, StringComparison.OrdinalIgnoreCase)) return true;

            // FiveM_b3095_GTAProcess тощо
            return name.Contains("GTAProcess", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TitleLooksLikeGame(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (title.Length == 0) return false;

            foreach (var hint in TitleHints)
                if (title.Contains(hint, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private bool DetectRunningGame()
        {
            Process[] all = Array.Empty<Process>();
            try
            {
                all = Process.GetProcesses();
                foreach (var p in all)
                {
                    if (IsGameProcess(p.ProcessName)) return true;
                }
                return false;
            }
            catch
            {
                return _isGameRunning;
            }
            finally
            {
                foreach (var p in all) p.Dispose();
            }
        }

        private string GetProcessName(uint pid)
        {
            if (_pidNames.TryGetValue(pid, out var cached)) return cached;
            if (_pidNames.Count > 256) _pidNames.Clear();

            string name = string.Empty;
            IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    uint size = (uint)sb.Capacity;
                    if (NativeMethods.QueryFullProcessImageName(h, 0, sb, ref size))
                        name = Path.GetFileNameWithoutExtension(sb.ToString());
                }
                finally
                {
                    NativeMethods.CloseHandle(h);
                }
            }

            _pidNames[pid] = name;
            return name;
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
