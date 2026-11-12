using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DSBinder.Core.Models;

namespace DSBinder.Controls
{
    /// <summary>
    /// Поле для призначення клавіші: клацніть і натисніть комбінацію.
    /// Esc — скасувати, права кнопка миші — очистити.
    /// </summary>
    public class HotkeyBox : Border
    {
        private readonly TextBlock _text;
        private bool _capturing;

        public uint Vk { get; private set; }
        public bool Ctrl { get; private set; }
        public bool Shift { get; private set; }
        public bool Alt { get; private set; }

        public bool AllowModifiers { get; set; } = true;
        public string EmptyText { get; set; } = "Не призначено";
        public bool IsCapturing => _capturing;

        public event EventHandler? HotkeyChanged;

        /// <summary>Глобальна подія: будь-яке поле почало/закінчило «слухати» клавіші.</summary>
        public static event Action<bool>? CapturingChanged;

        public HotkeyBox()
        {
            Focusable = true;
            FocusVisualStyle = null;
            Cursor = Cursors.Hand;
            MinHeight = 38;
            CornerRadius = new CornerRadius(8);
            BorderThickness = new Thickness(1);
            Padding = new Thickness(12, 0, 12, 0);
            SetResourceReference(BackgroundProperty, "Surface2Brush");
            SetResourceReference(BorderBrushProperty, "LineBrush");

            _text = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _text.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            Child = _text;

            ToolTip = "Клацніть і натисніть потрібну комбінацію.\nEsc — скасувати, права кнопка миші — очистити.";
            UpdateText();
        }

        public void SetHotkey(uint vk, bool ctrl, bool shift, bool alt)
        {
            Vk = vk;
            Ctrl = AllowModifiers && ctrl;
            Shift = AllowModifiers && shift;
            Alt = AllowModifiers && alt;
            UpdateText();
        }

        public void SetHotkey(HotkeyData? data)
        {
            if (data == null) SetHotkey(0, false, false, false);
            else SetHotkey(data.Vk, data.Ctrl, data.Shift, data.Alt);
        }

        public HotkeyData ToData() => new(Vk, Ctrl, Shift, Alt);

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            if (!_capturing) SetResourceReference(BorderBrushProperty, "LineStrongBrush");
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (!_capturing) SetResourceReference(BorderBrushProperty, "LineBrush");
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            Keyboard.Focus(this);
            StartCapture();
            e.Handled = true;
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);
            StopCapture();
            SetHotkey(0, false, false, false);
            HotkeyChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            StopCapture();
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (!_capturing)
            {
                base.OnPreviewKeyDown(e);
                return;
            }

            e.Handled = true;

            Key key = e.Key switch
            {
                Key.System => e.SystemKey,
                Key.ImeProcessed => e.ImeProcessedKey,
                Key.DeadCharProcessed => e.DeadCharProcessedKey,
                _ => e.Key
            };

            ModifierKeys mods = Keyboard.Modifiers;

            if (key == Key.Escape && mods == ModifierKeys.None)
            {
                StopCapture();
                return;
            }

            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            {
                if (AllowModifiers)
                {
                    string prefix = "";
                    if ((mods & ModifierKeys.Control) != 0) prefix += "Ctrl + ";
                    if ((mods & ModifierKeys.Alt) != 0) prefix += "Alt + ";
                    if ((mods & ModifierKeys.Shift) != 0) prefix += "Shift + ";
                    _text.Text = prefix + "…";
                }
                return;
            }

            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) return;

            SetHotkey(vk,
                (mods & ModifierKeys.Control) != 0,
                (mods & ModifierKeys.Shift) != 0,
                (mods & ModifierKeys.Alt) != 0);

            StopCapture();
            HotkeyChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            if (_capturing) e.Handled = true;
            else base.OnPreviewKeyUp(e);
        }

        private void StartCapture()
        {
            if (_capturing) return;
            _capturing = true;
            _text.Text = "Натисніть клавішу…";
            SetResourceReference(BorderBrushProperty, "AccentBrush");
            _text.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            CapturingChanged?.Invoke(true);
        }

        private void StopCapture()
        {
            if (!_capturing) return;
            _capturing = false;
            SetResourceReference(BorderBrushProperty, IsMouseOver ? "LineStrongBrush" : "LineBrush");
            UpdateText();
            CapturingChanged?.Invoke(false);
        }

        private void UpdateText()
        {
            if (_capturing) return;

            if (Vk == 0)
            {
                _text.Text = EmptyText;
                _text.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
            }
            else
            {
                _text.Text = KeyNames.Format(Vk, Ctrl, Shift, Alt);
                _text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            }
        }
    }
}
