using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DSBinder.Core.Engine;
using DSBinder.Core.Models;
using DSBinder.Core.Services;

namespace DSBinder
{
    /// <summary>Редактор бінду (створення, редагування, дублювання).</summary>
    public partial class AddBindWindow : Window
    {
        private static readonly Regex DigitsOnly = new("^[0-9]+$", RegexOptions.Compiled);

        private readonly IReadOnlyList<KeyBind> _allBinds;
        private readonly AppConfig _config;
        private readonly string _editingId;
        private readonly DispatcherTimer _previewTimer;
        private string _previewSource = "";

        public KeyBind Result { get; private set; } = new();

        /// <param name="source">Бінд для редагування/копіювання або null для нового.</param>
        /// <param name="isNew">true — створюється новий бінд (у т.ч. копія).</param>
        public AddBindWindow(KeyBind? source, bool isNew, IReadOnlyList<KeyBind> allBinds, AppConfig config)
        {
            InitializeComponent();

            _allBinds = allBinds;
            _config = config;
            _editingId = (!isNew && source != null) ? source.Id : Guid.NewGuid().ToString();

            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _previewTimer.Tick += async (_, _) =>
            {
                _previewTimer.Stop();
                await LoadPreviewAsync();
            };

            if (!isNew)
            {
                TitleText.Text = "Редагування бінду";
                TitleIcon.Text = "";
            }
            else if (source != null)
            {
                TitleText.Text = "Копія бінду";
                TitleIcon.Text = "";
            }

            var b = source ?? new KeyBind();

            NameBox.Text = (isNew && source == null) ? "" : b.Name;
            CategoryBox.Text = b.Category == KeyBind.DefaultCategory ? "" : b.Category;
            KeyBox.SetHotkey(b.VkCode, b.IsControl, b.IsShift, b.IsAlt);
            KeyBox.HotkeyChanged += (_, _) => ValidateKey();

            CommandBox.Text = b.CommandText;
            OpenChatSwitch.IsChecked = b.OpenChatFirst;
            PressEnterSwitch.IsChecked = b.PressEnterAfter;
            DelayBox.Text = b.DelayMs.ToString();

            ImageBox.Text = b.ImageUrl;
            ImageOpacitySlider.Value = b.ImageOpacity;

            SuppressSwitch.IsChecked = b.SuppressKey;
            OverlaySwitch.IsChecked = b.ShowInOverlay;

            if (b.Type == BindType.ImageOverlay) TypeImage.IsChecked = true;
            else TypeText.IsChecked = true;

            OpenChatDesc.Text = $"Перед кожним рядком натискається клавіша чату ({KeyNames.KeyName(config.ChatKeyVk)})";

            BuildCategorySuggestions();
            UpdateTypePanels();
            UpdateLineCounter();
            UpdateDelayHint();
            ValidateKey();

            Loaded += (_, _) =>
            {
                if (string.IsNullOrEmpty(NameBox.Text)) NameBox.Focus();
            };
        }

        private void BuildCategorySuggestions()
        {
            var categories = _allBinds
                .Select(x => x.Category)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(c => c)
                .Take(8)
                .ToList();

            foreach (var cat in categories)
            {
                var chip = new Button
                {
                    Content = cat,
                    Style = (Style)FindResource("ChipButton"),
                    Focusable = false
                };
                chip.Click += (_, _) => CategoryBox.Text = cat == KeyBind.DefaultCategory ? "" : cat;
                CategorySuggestions.Children.Add(chip);
            }
        }

        private bool IsImage => TypeImage.IsChecked == true;

        private void Type_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded && TextPanel == null) return;
            UpdateTypePanels();
        }

        private void UpdateTypePanels()
        {
            if (TextPanel == null || ImagePanel == null) return;
            TextPanel.Visibility = IsImage ? Visibility.Collapsed : Visibility.Visible;
            ImagePanel.Visibility = IsImage ? Visibility.Visible : Visibility.Collapsed;
            if (IsImage) _previewTimer.Start();
        }

        private void CommandBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateLineCounter();

        private void UpdateLineCounter()
        {
            if (LineCounter == null) return;
            var steps = MacroParser.Parse(CommandBox.Text);
            int msgs = steps.Count(s => !s.IsWait);
            int waits = steps.Count(s => s.IsWait);

            string text = msgs switch
            {
                0 => "немає повідомлень",
                1 => "1 повідомлення",
                _ => $"{msgs} повідомлень"
            };
            if (waits > 0) text += $" · {waits} пауз(и)";
            LineCounter.Text = text;
        }

        private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !DigitsOnly.IsMatch(e.Text);
        }

        private void DelayBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateDelayHint();

        private void UpdateDelayHint()
        {
            if (DelayHint == null) return;
            if (int.TryParse(DelayBox.Text, out int ms) && ms < 700)
            {
                DelayHint.Text = "Увага: менше ~700 мс сервер може відкинути повідомлення через антифлуд";
                DelayHint.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
            }
            else
            {
                DelayHint.Text = "Більшість серверів мають антифлуд — рекомендовано 1000 мс і більше";
                DelayHint.Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush");
            }
        }

        private bool ValidateKey()
        {
            string? warning = null;
            bool ok = true;

            if (KeyBox.Vk == 0)
            {
                warning = "Призначте клавішу для бінду";
                ok = false;
            }
            else
            {
                var probe = new KeyBind
                {
                    VkCode = KeyBox.Vk,
                    IsControl = KeyBox.Ctrl,
                    IsShift = KeyBox.Shift,
                    IsAlt = KeyBox.Alt
                };

                var conflict = _allBinds.FirstOrDefault(x => x.Id != _editingId && x.HasSameHotkey(probe));
                if (conflict != null)
                {
                    warning = $"Ця комбінація вже зайнята біндом «{conflict.Name}»";
                }
                else if (_config.ToggleHotkey.Matches(KeyBox.Vk, KeyBox.Ctrl, KeyBox.Shift, KeyBox.Alt) ||
                         _config.OverlayEditHotkey.Matches(KeyBox.Vk, KeyBox.Ctrl, KeyBox.Shift, KeyBox.Alt) ||
                         _config.OverlayVisibleHotkey.Matches(KeyBox.Vk, KeyBox.Ctrl, KeyBox.Shift, KeyBox.Alt) ||
                         _config.LawSearchHotkey.Matches(KeyBox.Vk, KeyBox.Ctrl, KeyBox.Shift, KeyBox.Alt))
                {
                    warning = "Ця комбінація вже використовується як системна гаряча клавіша";
                }
                else if (KeyBox.Vk == _config.ChatKeyVk && !KeyBox.Ctrl && !KeyBox.Alt)
                {
                    warning = "Це клавіша відкриття чату — краще обрати іншу";
                }
                else if (KeyBox.Vk == 0x1B || KeyBox.Vk == 0x0D)
                {
                    warning = "Esc та Enter використовуються грою — бінд на них незручний";
                }
            }

            KeyWarning.Text = warning ?? "";
            KeyWarning.Visibility = warning == null ? Visibility.Collapsed : Visibility.Visible;
            return ok;
        }

        private void ImageBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private async System.Threading.Tasks.Task LoadPreviewAsync()
        {
            string src = ImageBox.Text.Trim();
            if (src == _previewSource && PreviewImage.Source != null) return;
            _previewSource = src;

            if (string.IsNullOrWhiteSpace(src))
            {
                PreviewImage.Source = null;
                PreviewStatus.Text = "Тут з'явиться попередній перегляд";
                PreviewStatus.Visibility = Visibility.Visible;
                return;
            }

            PreviewStatus.Text = "Завантаження…";
            PreviewStatus.Visibility = Visibility.Visible;
            PreviewImage.Source = null;

            var img = await ImageCacheService.GetImageAsync(src);
            if (src != _previewSource) return; // поки вантажили, адресу змінили

            if (img != null)
            {
                PreviewImage.Source = img;
                PreviewStatus.Visibility = Visibility.Collapsed;
            }
            else
            {
                PreviewStatus.Text = "Не вдалося завантажити картинку.\nПеревірте посилання (має вести прямо на .png/.jpg) або шлях до файлу.";
            }
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Оберіть картинку",
                Filter = "Зображення|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|Усі файли|*.*"
            };
            if (dlg.ShowDialog(this) == true) ImageBox.Text = dlg.FileName;
        }

        private void ImageOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ImageOpacityText != null) ImageOpacityText.Text = $"{Math.Round(e.NewValue * 100)}%";
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (KeyBox.IsCapturing) return;
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                e.Handled = true;
                Save();
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !KeyBox.IsCapturing)
            {
                e.Handled = true;
                DialogResult = false;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e) => Save();

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Save()
        {
            if (!ValidateKey())
            {
                KeyBox.Focus();
                return;
            }

            if (!int.TryParse(DelayBox.Text, out int delay)) delay = 1000;

            Result = new KeyBind
            {
                Id = _editingId,
                Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Бінд" : NameBox.Text.Trim(),
                Category = string.IsNullOrWhiteSpace(CategoryBox.Text) ? KeyBind.DefaultCategory : CategoryBox.Text.Trim(),
                VkCode = KeyBox.Vk,
                IsControl = KeyBox.Ctrl,
                IsShift = KeyBox.Shift,
                IsAlt = KeyBox.Alt,
                IsEnabled = true,
                SuppressKey = SuppressSwitch.IsChecked == true,
                ShowInOverlay = OverlaySwitch.IsChecked == true,
                Type = IsImage ? BindType.ImageOverlay : BindType.TextMacro,
                CommandText = CommandBox.Text.TrimEnd(),
                OpenChatFirst = OpenChatSwitch.IsChecked == true,
                PressEnterAfter = PressEnterSwitch.IsChecked == true,
                DelayMs = Math.Clamp(delay, 0, 60000),
                ImageUrl = ImageBox.Text.Trim().Trim('"'),
                ImageOpacity = ImageOpacitySlider.Value
            };

            // Зберігаємо позицію картинки старого бінду
            var old = _allBinds.FirstOrDefault(x => x.Id == _editingId);
            var geometrySource = old;
            if (geometrySource != null)
            {
                Result.ImageLeft = geometrySource.ImageLeft;
                Result.ImageTop = geometrySource.ImageTop;
                Result.ImageWidth = geometrySource.ImageWidth;
                Result.ImageHeight = geometrySource.ImageHeight;
            }

            DialogResult = true;
        }
    }
}
