using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using DSBinder.Core.Hooks;
using DSBinder.Core.Input;
using DSBinder.Core.Models;
using DSBinder.Core.Native;
using DSBinder.Core.Services;

namespace DSBinder.Core.Engine
{
    public enum SystemAction
    {
        ToggleBinder,
        ToggleOverlayEdit,
        ToggleOverlayVisible,
        OpenLawSearch
    }

    /// <summary>
    /// Серце біндера: приймає натискання з хука, вирішує, що з ними робити,
    /// і виконує макроси в окремому потоці строго по черзі (без «накладань»).
    /// </summary>
    public sealed class BindEngine : IDisposable
    {
        private const uint RepeatWindowMs = 1100;

        private sealed class Snapshot
        {
            public bool Enabled;
            public bool GameOnly;
            public bool SmartChat;
            public bool CancelOnEscape;
            public TextSendMode SendMode;
            public uint ChatKey;
            public int ChatOpenDelay;
            public int EnterDelay;
            public bool RestoreClipboard;
            public HotkeyData Toggle = new();
            public HotkeyData OverlayEdit = new();
            public HotkeyData OverlayVisible = new();
            public HotkeyData LawSearch = new();
            public Dictionary<long, KeyBind> Map = new();
        }

        private readonly GameWindowService _game;
        private readonly BlockingCollection<KeyBind> _queue = new(new ConcurrentQueue<KeyBind>(), 3);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Thread _worker;

        // Доступ лише з потоку хука
        private readonly Dictionary<uint, uint> _down = new();
        private readonly HashSet<uint> _suppressed = new();

        private GlobalKeyboardHook? _hook;
        private volatile Snapshot _snap = new();
        private volatile bool _chatOpen;
        private volatile KeyBind? _running;
        private CancellationTokenSource? _currentRun;
        private bool _disposed;

        /// <summary>Пауза всього вводу (наприклад, коли користувач призначає клавішу в інтерфейсі).</summary>
        public volatile bool InputPaused;

        public bool IsChatOpen => _chatOpen;
        public KeyBind? RunningBind => _running;

        public event Action<string>? ImageToggleRequested;
        public event Action<SystemAction>? SystemActionRequested;
        public event Action? StateChanged;

        public BindEngine(GameWindowService game)
        {
            _game = game;
            _game.GameFocusChanged += OnGameFocusChanged;

            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "DSBinder.MacroWorker",
                Priority = ThreadPriority.AboveNormal
            };
            _worker.Start();
        }

        public void Start()
        {
            if (_hook != null) return;
            _hook = new GlobalKeyboardHook(HandleKey);
            _hook.Start();
            Logger.Info("Хук клавіатури запущено");
        }

        public void ReinstallHook() => _hook?.Reinstall();

        /// <summary>Застосовує налаштування та список біндів (потокобезпечно, атомарною заміною знімка).</summary>
        public void Apply(AppConfig cfg)
        {
            var snap = new Snapshot
            {
                Enabled = cfg.IsBinderEnabled,
                GameOnly = cfg.IsGameOnlyFilter,
                SmartChat = cfg.SmartChatPause,
                CancelOnEscape = cfg.CancelOnEscape,
                SendMode = cfg.SendMode,
                ChatKey = cfg.ChatKeyVk == 0 ? 0x54u : cfg.ChatKeyVk,
                ChatOpenDelay = cfg.ChatOpenDelayMs,
                EnterDelay = cfg.EnterDelayMs,
                RestoreClipboard = cfg.RestoreClipboard,
                Toggle = cfg.ToggleHotkey.Clone(),
                OverlayEdit = cfg.OverlayEditHotkey.Clone(),
                OverlayVisible = cfg.OverlayVisibleHotkey.Clone(),
                LawSearch = cfg.LawSearchHotkey.Clone()
            };

            foreach (var bind in cfg.Binds.Where(b => b.IsEnabled && b.VkCode != 0))
            {
                long key = Combo(bind.VkCode, bind.IsControl, bind.IsShift, bind.IsAlt);
                snap.Map.TryAdd(key, bind.Clone()); // при конфлікті працює перший бінд
            }

            _snap = snap;

            if (!snap.Enabled) CancelCurrent();
        }

        public void CancelCurrent()
        {
            var cts = _currentRun;
            try { cts?.Cancel(); } catch (ObjectDisposedException) { }
            while (_queue.TryTake(out _)) { }
        }

        private static long Combo(uint vk, bool ctrl, bool shift, bool alt) =>
            ((long)vk << 3) | (ctrl ? 4L : 0) | (shift ? 2L : 0) | (alt ? 1L : 0);

        private static bool IsModifier(uint vk) =>
            vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x5B || vk == 0x5C || (vk >= 0xA0 && vk <= 0xA5);

        private static bool IsCharKey(uint vk) =>
            (NativeMethods.MapVirtualKey(vk, NativeMethods.MAPVK_VK_TO_CHAR) & 0x7FFFFFFF) != 0;

        #region Hook handler (потік хука — має бути дуже швидким)

        private bool HandleKey(KeyEventRecord e)
        {
            uint vk = e.VkCode;
            if (IsModifier(vk)) return false;

            if (!e.IsKeyDown)
            {
                _down.Remove(vk);
                return _suppressed.Remove(vk); // відпускання глушимо, якщо глушили натискання
            }

            // Автоповтор при утриманні: клавіша вже натиснута і подія свіжа.
            bool repeat = _down.TryGetValue(vk, out uint last) && unchecked(e.Time - last) < RepeatWindowMs;
            _down[vk] = e.Time;
            if (!repeat) _suppressed.Remove(vk);

            if (InputPaused) return false;

            var s = _snap;
            bool ctrl = InputSimulator.IsDown(NativeMethods.VK_CONTROL);
            bool shift = InputSimulator.IsDown(NativeMethods.VK_SHIFT);
            bool alt = InputSimulator.IsDown(NativeMethods.VK_MENU);
            bool game = _game.IsGameActive;
            bool own = _game.IsOwnWindowActive;

            // Esc: зупинка макросу + чат закрився
            if (vk == NativeMethods.VK_ESCAPE)
            {
                if (s.CancelOnEscape && _running != null) CancelCurrent();
                if (game) SetChat(false);
                return false;
            }

            // Системні гарячі клавіші працюють завжди
            SystemAction? action = null;
            if (s.Toggle.Matches(vk, ctrl, shift, alt)) action = SystemAction.ToggleBinder;
            else if (s.OverlayEdit.Matches(vk, ctrl, shift, alt)) action = SystemAction.ToggleOverlayEdit;
            else if (s.OverlayVisible.Matches(vk, ctrl, shift, alt)) action = SystemAction.ToggleOverlayVisible;
            else if (s.LawSearch.Matches(vk, ctrl, shift, alt)) action = SystemAction.OpenLawSearch;

            if (action != null)
            {
                if (!repeat) SystemActionRequested?.Invoke(action.Value);
                _suppressed.Add(vk);
                return true;
            }

            // Бінди
            if (s.Enabled && !own && (!s.GameOnly || game) &&
                s.Map.TryGetValue(Combo(vk, ctrl, shift, alt), out var bind))
            {
                // Якщо в грі відкрито чат і ви просто друкуєте — «буквені» бінди без Ctrl/Alt не спрацьовують.
                bool typingInChat = s.SmartChat && _chatOpen && !ctrl && !alt && IsCharKey(vk);
                if (!typingInChat)
                {
                    if (!repeat) Trigger(bind);
                    if (bind.SuppressKey)
                    {
                        _suppressed.Add(vk);
                        return true;
                    }
                }
            }

            // Відстеження стану чату в грі
            if (game && !repeat)
            {
                if (vk == s.ChatKey && !ctrl && !alt && !_chatOpen) SetChat(true);
                else if (vk == NativeMethods.VK_RETURN) SetChat(false);
            }

            return false;
        }

        private void Trigger(KeyBind bind)
        {
            if (bind.Type == BindType.ImageOverlay)
            {
                ImageToggleRequested?.Invoke(bind.Id);
                return;
            }

            _queue.TryAdd(bind); // черга до 3 макросів, надлишок відкидається
        }

        private void SetChat(bool open)
        {
            if (_chatOpen == open) return;
            _chatOpen = open;
            StateChanged?.Invoke();
        }

        private void OnGameFocusChanged(bool focused)
        {
            if (!focused)
            {
                SetChat(false);
            }
            else
            {
                ReinstallHook(); // страховка: якщо Windows зняла хук — повертаємо
            }
        }

        #endregion

        #region Macro worker

        private sealed class RunContext
        {
            public bool ClipboardTouched;
            public string? ClipboardBackup;
        }

        private void WorkerLoop()
        {
            try
            {
                foreach (var bind in _queue.GetConsumingEnumerable(_shutdown.Token))
                {
                    RunMacro(bind);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Error("Потік макросів", ex);
            }
        }

        private void RunMacro(KeyBind bind)
        {
            var snap = _snap;
            if (!snap.Enabled) return;

            var steps = MacroParser.Parse(bind.CommandText);
            if (steps.Count == 0) return;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            var ct = cts.Token;
            var ctx = new RunContext();

            _currentRun = cts;
            _running = bind;
            StateChanged?.Invoke();
            NativeMethods.timeBeginPeriod(1);

            try
            {
                WaitForModifiersRelease(ct);

                bool chat = _chatOpen;
                int totalTexts = steps.Count(st => !st.IsWait);
                int sent = 0;

                foreach (var step in steps)
                {
                    if (step.IsWait)
                    {
                        InputSimulator.Delay(step.WaitMs, ct);
                        continue;
                    }

                    if (bind.OpenChatFirst && !chat)
                    {
                        InputSimulator.Tap(snap.ChatKey, 30, ct);
                        InputSimulator.Delay(snap.ChatOpenDelay, ct);
                        chat = true;
                    }

                    SendText(step.Text!, snap, ctx, ct);

                    if (bind.PressEnterAfter)
                    {
                        InputSimulator.Delay(snap.EnterDelay, ct);
                        InputSimulator.Tap(NativeMethods.VK_RETURN, 30, ct);
                        chat = false;
                    }

                    sent++;
                    if (sent < totalTexts) InputSimulator.Delay(bind.DelayMs, ct);
                }

                _chatOpen = chat;
            }
            catch (OperationCanceledException)
            {
                Logger.Info($"Макрос «{bind.Name}» зупинено");
            }
            catch (Exception ex)
            {
                Logger.Error($"Макрос «{bind.Name}»", ex);
            }
            finally
            {
                NativeMethods.timeEndPeriod(1);

                if (ctx.ClipboardTouched && snap.RestoreClipboard && ctx.ClipboardBackup != null)
                {
                    Thread.Sleep(150); // даємо грі вставити текст, перш ніж повернути буфер
                    ClipboardHelper.TrySetText(ctx.ClipboardBackup);
                }

                _currentRun = null;
                _running = null;
                StateChanged?.Invoke();
            }
        }

        /// <summary>
        /// Чекаємо, поки користувач відпустить Ctrl/Shift/Alt після комбінації (до 1.5 с).
        /// Інакше «T» перетвориться на «Ctrl+T», а Ctrl+V — на щось інше.
        /// </summary>
        private static void WaitForModifiersRelease(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (InputSimulator.AnyModifierDown() && sw.ElapsedMilliseconds < 1500)
            {
                InputSimulator.Delay(10, ct);
            }

            if (InputSimulator.AnyModifierDown())
            {
                InputSimulator.ReleaseHeldModifiers();
            }
            InputSimulator.Delay(25, ct);
        }

        private static void SendText(string text, Snapshot snap, RunContext ctx, CancellationToken ct)
        {
            if (snap.SendMode == TextSendMode.Type)
            {
                InputSimulator.TypeUnicode(text, 4, ct);
                return;
            }

            if (!ctx.ClipboardTouched)
            {
                ctx.ClipboardBackup = snap.RestoreClipboard ? ClipboardHelper.TryGetText() : null;
                ctx.ClipboardTouched = true;
            }

            if (!ClipboardHelper.TrySetText(text))
            {
                Logger.Warn("Буфер обміну зайнятий — друкую текст посимвольно");
                InputSimulator.TypeUnicode(text, 4, ct);
                return;
            }

            InputSimulator.Delay(35, ct);
            InputSimulator.PasteShortcut(ct);
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _game.GameFocusChanged -= OnGameFocusChanged;
            _hook?.Dispose();
            _shutdown.Cancel();
            _queue.CompleteAdding();
            _worker.Join(1000);
        }
    }
}
