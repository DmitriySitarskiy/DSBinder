using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using DSBinder.Core.Engine;

namespace DSBinder.Core.Models
{
    public enum BindType
    {
        TextMacro = 0,
        ImageOverlay = 1
    }

    public enum TextSendMode
    {
        /// <summary>Через буфер обміну + Ctrl+V (швидко, підтримує кирилицю).</summary>
        Paste = 0,
        /// <summary>Посимвольний друк Unicode (не чіпає буфер обміну).</summary>
        Type = 1
    }

    /// <summary>Комбінація клавіш (для системних гарячих клавіш).</summary>
    public sealed class HotkeyData
    {
        public uint Vk { get; set; }
        public bool Ctrl { get; set; }
        public bool Shift { get; set; }
        public bool Alt { get; set; }

        public HotkeyData() { }

        public HotkeyData(uint vk, bool ctrl = false, bool shift = false, bool alt = false)
        {
            Vk = vk;
            Ctrl = ctrl;
            Shift = shift;
            Alt = alt;
        }

        [JsonIgnore] public bool IsEmpty => Vk == 0;

        public bool Matches(uint vk, bool ctrl, bool shift, bool alt) =>
            Vk != 0 && Vk == vk && Ctrl == ctrl && Shift == shift && Alt == alt;

        public HotkeyData Clone() => new(Vk, Ctrl, Shift, Alt);

        public override string ToString() => KeyNames.Format(Vk, Ctrl, Shift, Alt);
    }

    public class KeyBind : INotifyPropertyChanged
    {
        public const string DefaultCategory = "Загальне";

        private bool _isEnabled = true;
        private bool _hasConflict;

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "Новий бінд";
        public string Category { get; set; } = DefaultCategory;

        public uint VkCode { get; set; }
        public bool IsControl { get; set; }
        public bool IsShift { get; set; }
        public bool IsAlt { get; set; }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                OnChanged(nameof(IsEnabled));
            }
        }

        /// <summary>Не передавати натискання в гру.</summary>
        public bool SuppressKey { get; set; } = true;
        public bool ShowInOverlay { get; set; } = true;

        public BindType Type { get; set; } = BindType.TextMacro;

        // --- Текстовий макрос ---
        public string CommandText { get; set; } = "";
        public bool OpenChatFirst { get; set; } = true;
        public bool PressEnterAfter { get; set; } = true;
        public int DelayMs { get; set; } = 1000;

        // --- Картинка ---
        public string ImageUrl { get; set; } = "";
        public double ImageOpacity { get; set; } = 0.95;
        public double ImageWidth { get; set; } = 420;
        public double ImageHeight { get; set; } = 300;
        public double ImageTop { get; set; } = 150;
        public double ImageLeft { get; set; } = 150;

        // --- Лише для інтерфейсу ---
        [JsonIgnore] public string KeyDisplay => KeyNames.Format(VkCode, IsControl, IsShift, IsAlt);
        [JsonIgnore] public bool IsImage => Type == BindType.ImageOverlay;

        [JsonIgnore]
        public bool HasConflict
        {
            get => _hasConflict;
            set
            {
                if (_hasConflict == value) return;
                _hasConflict = value;
                OnChanged(nameof(HasConflict));
            }
        }

        [JsonIgnore]
        public string Summary
        {
            get
            {
                if (Type == BindType.ImageOverlay)
                {
                    if (string.IsNullOrWhiteSpace(ImageUrl)) return "Картинка не вказана";
                    string name = ImageUrl;
                    try { name = Path.GetFileName(ImageUrl.Split('?')[0]); } catch { /* ignore */ }
                    return "Показати / сховати картинку · " + (string.IsNullOrWhiteSpace(name) ? ImageUrl : name);
                }

                var texts = MacroParser.Parse(CommandText).Where(s => !s.IsWait).Select(s => s.Text!).ToList();
                if (texts.Count == 0) return "Порожній бінд";
                return texts.Count > 1 ? $"{texts[0]}   (+ ще {texts.Count - 1})" : texts[0];
            }
        }

        [JsonIgnore]
        public int MessageCount => Type == BindType.TextMacro ? MacroParser.CountMessages(CommandText) : 0;

        public bool HasSameHotkey(KeyBind other) =>
            VkCode != 0 && VkCode == other.VkCode && IsControl == other.IsControl &&
            IsShift == other.IsShift && IsAlt == other.IsAlt;

        public KeyBind Clone()
        {
            var copy = (KeyBind)MemberwiseClone();
            copy.PropertyChanged = null;
            return copy;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class AppConfig
    {
        public int ConfigVersion { get; set; } = 2;

        // --- Робота біндера ---
        public bool IsBinderEnabled { get; set; } = true;
        public bool IsGameOnlyFilter { get; set; } = true;
        public bool SmartChatPause { get; set; } = true;
        public bool CancelOnEscape { get; set; } = true;
        public bool SoundFeedback { get; set; } = true;
        public List<string> CustomProcesses { get; set; } = new();

        // --- Відправка тексту ---
        public TextSendMode SendMode { get; set; } = TextSendMode.Paste;
        public uint ChatKeyVk { get; set; } = 0x54; // T
        public int ChatOpenDelayMs { get; set; } = 180;
        public int EnterDelayMs { get; set; } = 80;
        public bool RestoreClipboard { get; set; } = true;

        // --- Гарячі клавіші ---
        public HotkeyData ToggleHotkey { get; set; } = new(0x79, shift: true);          // Shift + F10
        public HotkeyData OverlayEditHotkey { get; set; } = new(0x78, shift: true);     // Shift + F9
        public HotkeyData OverlayVisibleHotkey { get; set; } = new(0x7A, shift: true);  // Shift + F11
        public HotkeyData LawSearchHotkey { get; set; } = new(0x77, shift: true);       // Shift + F8

        // --- Оверлей ---
        public bool OverlayVisible { get; set; } = true;
        public bool OverlayOnlyInGame { get; set; } = true;
        public double OverlayOpacity { get; set; } = 0.92;
        public double OverlayFontSize { get; set; } = 12.0;
        public double OverlayTop { get; set; } = 220;
        public double OverlayLeft { get; set; } = 20;
        public double OverlayWidth { get; set; } = 280;

        // --- Програма ---
        public bool StartMinimized { get; set; }
        public bool MinimizeToTray { get; set; } = true;
        public bool TrayHintShown { get; set; }
        public bool AutoCheckUpdates { get; set; } = true;

        public List<KeyBind> Binds { get; set; } = new();
    }
}
