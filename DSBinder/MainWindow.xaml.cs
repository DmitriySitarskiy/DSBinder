using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DSBinder.Controls;
using DSBinder.Core;
using DSBinder.Core.Engine;
using DSBinder.Core.Laws;
using DSBinder.Core.Models;
using DSBinder.Core.Native;
using DSBinder.Core.Services;

namespace DSBinder
{
    public partial class MainWindow : Window
    {
        private const string AllCategories = "Усі";

        private readonly ConfigService _configService = new();
        private readonly GameWindowService _game = new();
        private readonly Dictionary<string, ImageOverlayWindow> _imageWindows = new();
        private readonly DispatcherTimer _saveTimer;
        private readonly DispatcherTimer _snackTimer;
        private readonly DispatcherTimer _topmostTimer;
        private readonly DispatcherTimer _updateTimer;
        private readonly UpdateService _updates = new();
        private bool _updating;

        private BindEngine? _engine;
        private TrayService? _tray;
        private OverlayWindow? _overlay;
        private QuickLawWindow? _quickLaw;
        private ICollectionView? _view;

        private Action? _snackAction;
        private string _category = AllCategories;
        private bool _loadingUi;
        private bool _overlayEdit;
        private bool _exiting;
        private bool _initialized;

        public AppConfig Config { get; private set; }
        public ObservableCollection<KeyBind> Binds { get; } = new();

        public MainWindow()
        {
            InitializeComponent();

            Config = _configService.LoadConfig();

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveNow();
            };

            _snackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _snackTimer.Tick += (_, _) => HideSnack();

            _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _topmostTimer.Tick += (_, _) => ReassertTopmost();

            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _updateTimer.Tick += async (_, _) =>
            {
                _updateTimer.Interval = TimeSpan.FromHours(6); // перша перевірка через 8 с після запуску, далі раз на 6 год
                await CheckUpdatesAsync(silent: true);
            };

            StateChanged += (_, _) => UpdateMaximizeState();
        }

        #region Ініціалізація

        /// <summary>Запуск усіх сервісів. Викликається з App ще до показу вікна (вікно може стартувати згорнутим).</summary>
        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            foreach (var b in Config.Binds) Binds.Add(b);
            _view = CollectionViewSource.GetDefaultView(Binds);
            _view.Filter = FilterBind;
            BindsList.ItemsSource = _view;

            // Трей
            _tray = new TrayService();
            Icon = _tray.WindowIcon;
            _tray.OpenRequested += ShowFromTray;
            _tray.ToggleRequested += () => SetBinderEnabled(!Config.IsBinderEnabled, fromHotkey: false);
            _tray.OverlayRequested += () => SetOverlayVisible(!Config.OverlayVisible);
            _tray.ExitRequested += ExitApplication;

            // Оверлей
            _overlay = new OverlayWindow
            {
                Left = Config.OverlayLeft,
                Top = Config.OverlayTop,
                Width = Config.OverlayWidth,
                Opacity = Config.OverlayOpacity
            };
            _overlay.EnsureOnScreen();

            // Законодавство
            _quickLaw = new QuickLawWindow();
            LawService.Changed += () => Dispatcher.InvokeAsync(UpdateLawsUi);

            // Детектор гри
            _game.SetCustomProcesses(Config.CustomProcesses);
            _game.StatusChanged += () => Dispatcher.InvokeAsync(OnGameStatusChanged);
            _game.Start();

            // Рушій
            _engine = new BindEngine(_game);
            _engine.ImageToggleRequested += id => Dispatcher.InvokeAsync(() => ToggleImage(id));
            _engine.SystemActionRequested += a => Dispatcher.InvokeAsync(() => HandleSystemAction(a));
            _engine.StateChanged += () => Dispatcher.InvokeAsync(UpdateOverlayContent, DispatcherPriority.Background);
            _engine.Apply(Config);

            HotkeyBox.CapturingChanged += capturing =>
            {
                if (_engine != null) _engine.InputPaused = capturing;
            };

            try
            {
                _engine.Start();
            }
            catch (Exception ex)
            {
                Logger.Error("Запуск хука", ex);
                System.Windows.MessageBox.Show(
                    "Не вдалося підключитися до клавіатури. Спробуйте запустити DSBinder від імені адміністратора.\n\n" + ex.Message,
                    "DSBinder", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            LoadSettingsToUi();
            RefreshBindsUi(rebuildCategories: true);
            UpdateMasterUi();
            UpdateHotkeyHints();
            OnGameStatusChanged();
            UpdateOverlayContent();
            UpdateOverlayVisibility();
            UpdateLawsUi();
            _topmostTimer.Start();

            AdminWarning.Visibility = App.IsElevated ? Visibility.Collapsed : Visibility.Visible;
            if (!App.IsElevated) Logger.Warn("Програма працює без прав адміністратора");

            UpdateVersionUi();
            if (Config.AutoCheckUpdates && _updates.IsAvailable) _updateTimer.Start();

            VersionText.Text = $"DSBinder {_updates.CurrentVersion} · налаштування: {ConfigService.DataDirectory}";

            PrecacheImages();
        }

        private async void PrecacheImages()
        {
            foreach (var b in Binds.Where(x => x.IsImage && !string.IsNullOrWhiteSpace(x.ImageUrl)).ToList())
            {
                await ImageCacheService.GetImageAsync(b.ImageUrl);
            }
        }

        #endregion

        #region Збереження

        private void ScheduleSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void SaveNow()
        {
            if (_overlay != null && !_overlayEdit)
            {
                Config.OverlayLeft = _overlay.Left;
                Config.OverlayTop = _overlay.Top;
                Config.OverlayWidth = _overlay.Width;
            }
            Config.Binds = Binds.ToList();
            _configService.SaveConfig(Config);
        }

        /// <summary>Після будь-якої зміни біндів: передати в рушій, оновити UI, оверлей і зберегти.</summary>
        private void OnBindsChanged(bool rebuildCategories = true)
        {
            Config.Binds = Binds.ToList();
            _engine?.Apply(Config);
            RefreshBindsUi(rebuildCategories);
            UpdateOverlayContent();
            ScheduleSave();
        }

        #endregion

        #region Список біндів

        private bool FilterBind(object obj)
        {
            if (obj is not KeyBind b) return false;

            if (_category != AllCategories &&
                !string.Equals(b.Category, _category, StringComparison.CurrentCultureIgnoreCase))
                return false;

            string q = SearchBox.Text.Trim();
            if (q.Length == 0) return true;

            return Contains(b.Name, q) || Contains(b.CommandText, q) || Contains(b.Category, q) ||
                   Contains(b.KeyDisplay, q) || Contains(b.ImageUrl, q);
        }

        private static bool Contains(string? text, string q) =>
            text != null && text.Contains(q, StringComparison.CurrentCultureIgnoreCase);

        private void RefreshBindsUi(bool rebuildCategories)
        {
            // Конфлікти: кілька увімкнених біндів на одній комбінації
            foreach (var b in Binds)
            {
                b.HasConflict = b.IsEnabled && b.VkCode != 0 &&
                                Binds.Any(o => !ReferenceEquals(o, b) && o.IsEnabled && o.HasSameHotkey(b));
            }

            if (rebuildCategories) RebuildCategories();
            _view?.Refresh();

            int total = Binds.Count;
            int active = Binds.Count(b => b.IsEnabled);
            BindsSubtitle.Text = total == 0
                ? "Біндів поки немає"
                : $"{total} {Plural(total, "бінд", "бінди", "біндів")} · {active} {Plural(active, "активний", "активні", "активних")}";

            bool empty = _view == null || _view.IsEmpty;
            EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            if (total == 0)
            {
                EmptyTitle.Text = "Ще немає біндів";
                EmptyDesc.Text = "Створіть перший бінд — наприклад, відіграш показу посвідчення.";
                EmptyAddButton.Visibility = Visibility.Visible;
            }
            else
            {
                EmptyTitle.Text = "Нічого не знайдено";
                EmptyDesc.Text = "Спробуйте інший запит або оберіть категорію «Усі».";
                EmptyAddButton.Visibility = Visibility.Collapsed;
            }
        }

        private static string Plural(int n, string one, string few, string many)
        {
            int n10 = n % 10, n100 = n % 100;
            if (n10 == 1 && n100 != 11) return one;
            if (n10 >= 2 && n10 <= 4 && (n100 < 10 || n100 >= 20)) return few;
            return many;
        }

        private void RebuildCategories()
        {
            var cats = Binds.Select(b => b.Category)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(c => c)
                .ToList();

            if (_category != AllCategories && !cats.Contains(_category, StringComparer.CurrentCultureIgnoreCase))
                _category = AllCategories;

            CategoryPanel.Children.Clear();
            CategoryPanel.Visibility = cats.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (cats.Count <= 1)
            {
                _category = AllCategories;
                return;
            }

            foreach (var cat in new[] { AllCategories }.Concat(cats))
            {
                int count = cat == AllCategories
                    ? Binds.Count
                    : Binds.Count(b => string.Equals(b.Category, cat, StringComparison.CurrentCultureIgnoreCase));

                var chip = new RadioButton
                {
                    Content = $"{cat}  {count}",
                    Tag = cat,
                    GroupName = "Categories",
                    Style = (Style)FindResource("Chip"),
                    IsChecked = string.Equals(cat, _category, StringComparison.CurrentCultureIgnoreCase)
                };
                chip.Checked += Category_Checked;
                CategoryPanel.Children.Add(chip);
            }
        }

        private void Category_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string cat)
            {
                _category = cat;
                RefreshBindsUi(rebuildCategories: false);
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_initialized) return;
            RefreshBindsUi(rebuildCategories: false);
        }

        private static KeyBind? BindFrom(object sender) =>
            sender is FrameworkElement fe ? (fe.Tag as KeyBind ?? fe.DataContext as KeyBind) : null;

        private void AddBind_Click(object sender, RoutedEventArgs e)
        {
            OpenEditor(null, isNew: true, presetCategory: _category == AllCategories ? null : _category);
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (BindFrom(sender) is { } b) OpenEditor(b, isNew: false);
        }

        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && BindFrom(sender) is { } b)
            {
                e.Handled = true;
                OpenEditor(b, isNew: false);
            }
        }

        private void Duplicate_Click(object sender, RoutedEventArgs e)
        {
            if (BindFrom(sender) is not { } b) return;
            var copy = b.Clone();
            copy.Id = Guid.NewGuid().ToString();
            copy.Name = b.Name + " (копія)";
            copy.VkCode = 0; // клавішу треба призначити нову
            copy.IsControl = copy.IsShift = copy.IsAlt = false;
            OpenEditor(copy, isNew: true, insertAfter: b);
        }

        private void OpenEditor(KeyBind? source, bool isNew, string? presetCategory = null, KeyBind? insertAfter = null)
        {
            var editor = new AddBindWindow(source, isNew, Binds.ToList(), Config);
            if (IsVisible) editor.Owner = this;
            else editor.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            if (source == null && !string.IsNullOrEmpty(presetCategory) && presetCategory != KeyBind.DefaultCategory)
                editor.CategoryBox.Text = presetCategory;

            if (editor.ShowDialog() != true) return;

            var result = editor.Result;

            if (!isNew && source != null)
            {
                CloseImageWindow(source.Id);
                int index = Binds.IndexOf(source);
                if (index >= 0) Binds[index] = result;
                else Binds.Add(result);
                ShowSnack($"Бінд «{result.Name}» збережено");
            }
            else
            {
                int index = insertAfter != null ? Binds.IndexOf(insertAfter) : -1;
                if (index >= 0) Binds.Insert(index + 1, result);
                else Binds.Add(result);
                ShowSnack($"Бінд «{result.Name}» створено");
            }

            OnBindsChanged();

            if (result.IsImage) _ = ImageCacheService.GetImageAsync(result.ImageUrl);
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (BindFrom(sender) is not { } b) return;

            int index = Binds.IndexOf(b);
            if (index < 0) return;

            CloseImageWindow(b.Id);
            Binds.RemoveAt(index);
            OnBindsChanged();

            ShowSnack($"Бінд «{b.Name}» видалено", "Відновити", () =>
            {
                Binds.Insert(Math.Min(index, Binds.Count), b);
                OnBindsChanged();
            });
        }

        private void BindEnabled_Click(object sender, RoutedEventArgs e)
        {
            if (BindFrom(sender) is { } b && !b.IsEnabled) CloseImageWindow(b.Id);
            OnBindsChanged(rebuildCategories: false);
        }

        #endregion

        #region Біндер ON/OFF, статус гри

        private void MasterToggle_Click(object sender, RoutedEventArgs e)
        {
            SetBinderEnabled(MasterToggle.IsChecked == true, fromHotkey: false);
        }

        private void SetBinderEnabled(bool enabled, bool fromHotkey)
        {
            Config.IsBinderEnabled = enabled;
            _engine?.Apply(Config);
            UpdateMasterUi();
            UpdateOverlayContent();
            ScheduleSave();

            if (fromHotkey && Config.SoundFeedback)
            {
                NativeMethods.MessageBeep(enabled ? NativeMethods.MB_ICONASTERISK : NativeMethods.MB_ICONEXCLAMATION);
            }
        }

        private void UpdateMasterUi()
        {
            MasterToggle.IsChecked = Config.IsBinderEnabled;
            MasterStateText.Text = Config.IsBinderEnabled ? "Увімкнено" : "Вимкнено";
            MasterStateText.Foreground = (Brush)FindResource(Config.IsBinderEnabled ? "SuccessBrush" : "DangerBrush");
            _tray?.SetState(Config.IsBinderEnabled, Config.OverlayVisible);
        }

        private void OnGameStatusChanged()
        {
            if (_game.IsGameActive)
            {
                GameDot.Fill = (Brush)FindResource("SuccessBrush");
                GameStateText.Text = "У грі — бінди активні";
            }
            else if (_game.IsGameRunning)
            {
                GameDot.Fill = (Brush)FindResource("AccentBrush");
                GameStateText.Text = "GTA 5 запущена (не у фокусі)";
            }
            else
            {
                GameDot.Fill = (Brush)FindResource("TextDimBrush");
                GameStateText.Text = "GTA 5 не запущена";
            }

            UpdateOverlayVisibility();
            if (_game.IsGameActive) ReassertTopmost();
        }

        private void UpdateHotkeyHints()
        {
            ToggleHotkeyHint.Text = Config.ToggleHotkey.IsEmpty
                ? "Гаряча клавіша вимкнення не призначена"
                : $"{Config.ToggleHotkey} — увімк./вимк.";

            HelpHotkeys.Text =
                $"{Fmt(Config.ToggleHotkey)} — увімкнути / вимкнути біндер\n" +
                $"{Fmt(Config.OverlayEditHotkey)} — режим переміщення оверлею й картинок\n" +
                $"{Fmt(Config.OverlayVisibleHotkey)} — показати / сховати оверлей\n" +
                $"{Fmt(Config.LawSearchHotkey)} — швидкий пошук по законодавству поверх гри\n" +
                "Esc — зупинити макрос, що виконується\n" +
                "Подвійний клік по бінду — редагувати";
        }

        private static string Fmt(HotkeyData h) => h.IsEmpty ? "(не призначено)" : h.ToString();

        private void HandleSystemAction(SystemAction action)
        {
            switch (action)
            {
                case SystemAction.ToggleBinder:
                    SetBinderEnabled(!Config.IsBinderEnabled, fromHotkey: true);
                    break;
                case SystemAction.ToggleOverlayEdit:
                    SetOverlayEdit(!_overlayEdit);
                    break;
                case SystemAction.ToggleOverlayVisible:
                    SetOverlayVisible(!Config.OverlayVisible);
                    break;
                case SystemAction.OpenLawSearch:
                    _quickLaw?.Toggle();
                    break;
            }
        }

        #endregion

        #region Оверлей і картинки

        private void UpdateOverlayContent()
        {
            if (_overlay == null) return;
            var binds = Binds.Where(b => b.IsEnabled && b.ShowInOverlay && b.VkCode != 0);
            _overlay.UpdateContent(binds, Config.OverlayFontSize, Config.IsBinderEnabled,
                _engine?.IsChatOpen ?? false, _engine?.RunningBind?.Name);
        }

        private void UpdateOverlayVisibility()
        {
            if (_overlay == null) return;

            bool visible = _overlayEdit ||
                           (Config.OverlayVisible && (!Config.OverlayOnlyInGame || _game.IsGameActive));

            if (visible && !_overlay.IsVisible)
            {
                _overlay.Show();
                _overlay.ForceTopmost();
            }
            else if (!visible && _overlay.IsVisible)
            {
                _overlay.Hide();
            }
        }

        private void ReassertTopmost()
        {
            if (_overlay?.IsVisible == true) _overlay.ForceTopmost();
            foreach (var w in _imageWindows.Values) w.ForceTopmost();
        }

        private void SetOverlayVisible(bool visible)
        {
            Config.OverlayVisible = visible;
            _loadingUi = true;
            SetOverlayShow.IsChecked = visible;
            _loadingUi = false;
            UpdateOverlayVisibility();
            _tray?.SetState(Config.IsBinderEnabled, Config.OverlayVisible);
            ScheduleSave();
        }

        private void SetOverlayEdit(bool edit)
        {
            _overlayEdit = edit;
            _overlay?.SetEditMode(edit);
            foreach (var w in _imageWindows.Values) w.SetEditMode(edit);

            OverlayEditButtonText.Text = edit ? "Готово" : "Перемістити оверлей";

            if (!edit)
            {
                if (_overlay != null)
                {
                    Config.OverlayLeft = _overlay.Left;
                    Config.OverlayTop = _overlay.Top;
                    Config.OverlayWidth = _overlay.Width;
                }
                ScheduleSave(); // картинки вже записали позицію у свої бінди
            }
            else
            {
                ShowSnack($"Режим переміщення: тягніть оверлей і картинки мишею. {Fmt(Config.OverlayEditHotkey)} або «Готово» — завершити.");
            }

            UpdateOverlayVisibility();
        }

        private void ToggleImage(string bindId)
        {
            if (_imageWindows.ContainsKey(bindId))
            {
                CloseImageWindow(bindId);
                return;
            }

            var bind = Binds.FirstOrDefault(b => b.Id == bindId);
            if (bind == null) return;

            var win = new ImageOverlayWindow(bind);
            win.Closed += (_, _) => _imageWindows.Remove(bindId);
            _imageWindows[bindId] = win;
            win.Show();
            win.SetEditMode(_overlayEdit);
            win.ForceTopmost();
        }

        private void CloseImageWindow(string bindId)
        {
            if (_imageWindows.TryGetValue(bindId, out var win))
            {
                win.PersistToBind();
                _imageWindows.Remove(bindId);
                win.Close();
                ScheduleSave();
            }
        }

        private void OverlayEdit_Click(object sender, RoutedEventArgs e) => SetOverlayEdit(!_overlayEdit);

        private void OverlayReset_Click(object sender, RoutedEventArgs e)
        {
            if (_overlay == null) return;
            _overlay.Left = 20;
            _overlay.Top = 220;
            _overlay.Width = 280;
            Config.OverlayLeft = 20;
            Config.OverlayTop = 220;
            Config.OverlayWidth = 280;
            ScheduleSave();
            ShowSnack("Положення оверлею скинуто");
        }

        #endregion

        #region Налаштування

        private void LoadSettingsToUi()
        {
            _loadingUi = true;
            try
            {
                SetGameOnly.IsChecked = Config.IsGameOnlyFilter;
                SetSmartChat.IsChecked = Config.SmartChatPause;
                SetCancelEsc.IsChecked = Config.CancelOnEscape;
                SetSound.IsChecked = Config.SoundFeedback;

                SendPaste.IsChecked = Config.SendMode == TextSendMode.Paste;
                SendType.IsChecked = Config.SendMode == TextSendMode.Type;
                ChatKeyBox.SetHotkey(Config.ChatKeyVk, false, false, false);
                ChatDelaySlider.Value = Config.ChatOpenDelayMs;
                EnterDelaySlider.Value = Config.EnterDelayMs;
                SetRestoreClipboard.IsChecked = Config.RestoreClipboard;

                HkToggle.SetHotkey(Config.ToggleHotkey);
                HkOverlayEdit.SetHotkey(Config.OverlayEditHotkey);
                HkOverlayVisible.SetHotkey(Config.OverlayVisibleHotkey);
                HkLawSearch.SetHotkey(Config.LawSearchHotkey);

                SetOverlayShow.IsChecked = Config.OverlayVisible;
                SetOverlayOnlyGame.IsChecked = Config.OverlayOnlyInGame;
                OverlayOpacitySlider.Value = Config.OverlayOpacity;
                OverlayFontSlider.Value = Config.OverlayFontSize;

                SetCloseToTray.IsChecked = Config.MinimizeToTray;
                SetStartMinimized.IsChecked = Config.StartMinimized;
                SetAutoUpdate.IsChecked = Config.AutoCheckUpdates;
                ProcessesBox.Text = string.Join(", ", Config.CustomProcesses);

                UpdateSliderLabels();
            }
            finally
            {
                _loadingUi = false;
            }

            ChatKeyBox.HotkeyChanged += (_, _) =>
            {
                Config.ChatKeyVk = ChatKeyBox.Vk == 0 ? 0x54u : ChatKeyBox.Vk;
                if (ChatKeyBox.Vk == 0) ChatKeyBox.SetHotkey(0x54, false, false, false);
                ApplySettings();
            };
            HkToggle.HotkeyChanged += (_, _) => { Config.ToggleHotkey = HkToggle.ToData(); ApplySettings(); };
            HkOverlayEdit.HotkeyChanged += (_, _) => { Config.OverlayEditHotkey = HkOverlayEdit.ToData(); ApplySettings(); };
            HkOverlayVisible.HotkeyChanged += (_, _) => { Config.OverlayVisibleHotkey = HkOverlayVisible.ToData(); ApplySettings(); };
            HkLawSearch.HotkeyChanged += (_, _) => { Config.LawSearchHotkey = HkLawSearch.ToData(); ApplySettings(); };
        }

        private void ApplySettings()
        {
            _engine?.Apply(Config);
            UpdateHotkeyHints();
            UpdateMasterUi();
            UpdateOverlayContent();
            UpdateOverlayVisibility();
            ScheduleSave();
        }

        private void Setting_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingUi) return;

            Config.IsGameOnlyFilter = SetGameOnly.IsChecked == true;
            Config.SmartChatPause = SetSmartChat.IsChecked == true;
            Config.CancelOnEscape = SetCancelEsc.IsChecked == true;
            Config.SoundFeedback = SetSound.IsChecked == true;
            Config.RestoreClipboard = SetRestoreClipboard.IsChecked == true;
            Config.OverlayVisible = SetOverlayShow.IsChecked == true;
            Config.OverlayOnlyInGame = SetOverlayOnlyGame.IsChecked == true;
            Config.MinimizeToTray = SetCloseToTray.IsChecked == true;
            Config.StartMinimized = SetStartMinimized.IsChecked == true;

            bool autoUpdate = SetAutoUpdate.IsChecked == true;
            if (autoUpdate != Config.AutoCheckUpdates)
            {
                Config.AutoCheckUpdates = autoUpdate;
                if (autoUpdate && _updates.IsAvailable) _updateTimer.Start();
                else _updateTimer.Stop();
            }

            ApplySettings();
        }

        private void SendMode_Checked(object sender, RoutedEventArgs e)
        {
            if (_loadingUi || !_initialized) return;
            Config.SendMode = SendType.IsChecked == true ? TextSendMode.Type : TextSendMode.Paste;
            ApplySettings();
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_initialized) return;
            UpdateSliderLabels();
            if (_loadingUi) return;

            Config.ChatOpenDelayMs = (int)ChatDelaySlider.Value;
            Config.EnterDelayMs = (int)EnterDelaySlider.Value;
            Config.OverlayOpacity = Math.Round(OverlayOpacitySlider.Value, 2);
            Config.OverlayFontSize = Math.Round(OverlayFontSlider.Value);

            if (_overlay != null) _overlay.Opacity = Config.OverlayOpacity;
            _engine?.Apply(Config);
            UpdateOverlayContent();
            ScheduleSave();
        }

        private void UpdateSliderLabels()
        {
            if (ChatDelayText == null || EnterDelayText == null || OverlayOpacityText == null || OverlayFontText == null) return;
            ChatDelayText.Text = $"{(int)ChatDelaySlider.Value} мс";
            EnterDelayText.Text = $"{(int)EnterDelaySlider.Value} мс";
            OverlayOpacityText.Text = $"{Math.Round(OverlayOpacitySlider.Value * 100)}%";
            OverlayFontText.Text = $"{Math.Round(OverlayFontSlider.Value)} px";
        }

        private void ProcessesBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var list = ProcessesBox.Text
                .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Config.CustomProcesses = list;
            _game.SetCustomProcesses(list);
            ScheduleSave();
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Експорт біндів",
                FileName = $"DSBinder-бінди-{DateTime.Now:yyyy-MM-dd}.json",
                Filter = "Бінди DSBinder (*.json)|*.json"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                _configService.ExportBinds(dlg.FileName, Binds);
                ShowSnack($"Експортовано {Binds.Count} {Plural(Binds.Count, "бінд", "бінди", "біндів")}");
            }
            catch (Exception ex)
            {
                Logger.Error("Експорт", ex);
                ShowSnack("Не вдалося зберегти файл: " + ex.Message);
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Імпорт біндів",
                Filter = "Бінди DSBinder (*.json)|*.json|Усі файли|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var imported = _configService.ImportBinds(dlg.FileName);
                if (imported.Count == 0)
                {
                    ShowSnack("У файлі не знайдено біндів");
                    return;
                }

                foreach (var b in imported) Binds.Add(b);
                OnBindsChanged();
                NavBinds.IsChecked = true;
                ShowSnack($"Імпортовано {imported.Count} {Plural(imported.Count, "бінд", "бінди", "біндів")}");
            }
            catch (Exception ex)
            {
                Logger.Error("Імпорт", ex);
                ShowSnack("Файл пошкоджений або має невідомий формат");
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.IO.Directory.CreateDirectory(ConfigService.DataDirectory);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ConfigService.DataDirectory}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Error("Відкриття папки", ex);
            }
        }

        #endregion

        #region Оновлення

        private void UpdateVersionUi()
        {
            SettingsVersionText.Text = $"DSBinder {_updates.CurrentVersion}";
            if (!_updates.IsAvailable)
            {
                UpdateStatusText.Text = _updates.UnavailableReason;
                CheckUpdatesButton.IsEnabled = false;
            }
            else if (_updates.PendingVersion == null)
            {
                UpdateStatusText.Text = "Натисніть «Перевірити оновлення», щоб дізнатися про нову версію.";
            }
        }

        private async System.Threading.Tasks.Task CheckUpdatesAsync(bool silent)
        {
            if (_updating || !_updates.IsAvailable) return;

            CheckUpdatesButton.IsEnabled = false;
            UpdateStatusText.Text = "Перевірка оновлень…";
            try
            {
                string? version = await _updates.CheckAsync();
                if (version == null)
                {
                    UpdateStatusText.Text = $"У вас остання версія ({_updates.CurrentVersion}). Перевірено о {DateTime.Now:HH:mm}.";
                    UpdateBanner.Visibility = Visibility.Collapsed;
                    if (!silent) ShowSnack("У вас остання версія DSBinder");
                }
                else
                {
                    UpdateStatusText.Text = $"Доступна нова версія {version} (у вас {_updates.CurrentVersion}).";
                    UpdateBannerText.Text = $"Версія {version} · у вас {_updates.CurrentVersion}";
                    UpdateBanner.Visibility = Visibility.Visible;
                    if (silent && !IsVisible)
                        _tray?.ShowBalloon("Доступне оновлення DSBinder", $"Версія {version}. Відкрийте DSBinder, щоб оновитися.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Перевірка оновлень", ex);
                UpdateStatusText.Text = "Не вдалося перевірити оновлення (немає інтернету або GitHub недоступний).";
                if (!silent) ShowSnack("Не вдалося перевірити оновлення");
            }
            finally
            {
                CheckUpdatesButton.IsEnabled = true;
            }
        }

        private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(silent: false);

        private async void UpdateNow_Click(object sender, RoutedEventArgs e)
        {
            if (_updating || _updates.PendingVersion == null) return;
            _updating = true;
            UpdateNowButton.IsEnabled = false;
            CheckUpdatesButton.IsEnabled = false;
            UpdateBannerText.Text = "Завантаження оновлення…";

            try
            {
                await _updates.DownloadAsync();
                UpdateBannerText.Text = "Встановлення і перезапуск…";

                // Зберігаємо все і відпускаємо клавіатуру перед перезапуском
                if (_overlayEdit) SetOverlayEdit(false);
                foreach (var id in _imageWindows.Keys.ToList()) CloseImageWindow(id);
                _saveTimer.Stop();
                SaveNow();
                _exiting = true;
                _engine?.Dispose();
                _tray?.Dispose();
                Logger.Info($"Оновлення до {_updates.PendingVersion}");

                _updates.ApplyAndRestart();
            }
            catch (Exception ex)
            {
                Logger.Error("Оновлення", ex);
                UpdateBannerText.Text = "Не вдалося оновитися. Спробуйте пізніше.";
                UpdateNowButton.IsEnabled = true;
                CheckUpdatesButton.IsEnabled = true;
                _updating = false;
            }
        }

        #endregion

        #region Законодавство

        private sealed class LawCodeRow
        {
            public string Short { get; init; } = "";
            public string Name { get; init; } = "";
            public string CountText { get; init; } = "";
            public Brush Foreground { get; init; } = Brushes.White;
            public Brush Background { get; init; } = Brushes.Transparent;
        }

        private void UpdateLawsUi()
        {
            var codes = LawService.Library.Codes;
            int total = LawService.TotalArticles;

            LawsSubtitle.Text = total == 0
                ? "Імпортуйте законку сервера, щоб швидко шукати статті"
                : $"{codes.Count} {Plural(codes.Count, "кодекс", "кодекси", "кодексів")} · {total} {Plural(total, "стаття", "статті", "статей")} · " +
                  $"у грі: {Fmt(Config.LawSearchHotkey)}";

            LawCodesList.ItemsSource = codes.Select(c => new LawCodeRow
            {
                Short = c.Short,
                Name = c.Name,
                CountText = $"{c.Articles.Count} {Plural(c.Articles.Count, "стаття", "статті", "статей")}",
                Foreground = LawColors.ForCode(c.Short),
                Background = LawColors.ForCodeBackground(c.Short)
            }).ToList();
            LawCodesEmpty.Visibility = codes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void LawsImport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new LawImportWindow();
            if (IsVisible) dlg.Owner = this;
            if (dlg.ShowDialog() == true)
            {
                ShowSnack($"Імпортовано {dlg.ImportedCount} {Plural(dlg.ImportedCount, "статтю", "статті", "статей")}");
                LawSearch.FocusSearch();
            }
        }

        private void LawsManage_Click(object sender, RoutedEventArgs e)
        {
            LawsManagePanel.Visibility = LawsManagePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        private void LawCodeDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string shortName) return;

            var code = LawService.Library.Codes.FirstOrDefault(c => c.Short == shortName);
            if (code == null) return;

            LawService.RemoveCode(shortName);
            ShowSnack($"Кодекс «{code.Name}» видалено", "Відновити", () =>
            {
                var p = new LawParser.ParsedCode { Short = code.Short, Name = code.Name };
                p.Articles.AddRange(code.Articles);
                LawService.Import(new[] { p }, replace: true);
            });
        }

        #endregion

        #region Навігація, snackbar

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PageBinds == null || PageSettings == null || PageHelp == null || PageLaws == null) return;
            PageBinds.Visibility = NavBinds.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageLaws.Visibility = NavLaws.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (NavLaws.IsChecked == true) Dispatcher.InvokeAsync(() => LawSearch.FocusSearch(), DispatcherPriority.Input);
            PageSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageHelp.Visibility = NavHelp.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowSnack(string text, string? actionText = null, Action? action = null)
        {
            SnackText.Text = text;
            _snackAction = action;
            SnackAction.Content = actionText;
            SnackAction.Visibility = actionText != null ? Visibility.Visible : Visibility.Collapsed;
            Snackbar.Visibility = Visibility.Visible;
            _snackTimer.Stop();
            _snackTimer.Start();
        }

        private void HideSnack()
        {
            _snackTimer.Stop();
            Snackbar.Visibility = Visibility.Collapsed;
            _snackAction = null;
        }

        private void SnackAction_Click(object sender, RoutedEventArgs e)
        {
            var action = _snackAction;
            HideSnack();
            action?.Invoke();
        }

        private void SnackClose_Click(object sender, RoutedEventArgs e) => HideSnack();

        #endregion

        #region Вікно, трей, вихід

        public void ShowFromTray()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;  // гарантовано виводимо на передній план
            Topmost = false;
            Focus();
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                if (PageLaws.Visibility == Visibility.Visible) LawSearch.FocusSearch();
                else if (PageBinds.Visibility == Visibility.Visible) { SearchBox.Focus(); SearchBox.SelectAll(); }
                e.Handled = true;
                return;
            }
            base.OnPreviewKeyDown(e);
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void UpdateMaximizeState()
        {
            bool max = WindowState == WindowState.Maximized;
            RootBorder.BorderThickness = max ? new Thickness(7) : new Thickness(1);
            MaxButton.Content = max ? "" : "";
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_exiting && Config.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();

                if (!Config.TrayHintShown)
                {
                    Config.TrayHintShown = true;
                    ScheduleSave();
                    _tray?.ShowBalloon("DSBinder працює у фоні",
                        "Бінди активні. Відкрити біндер або вийти — через іконку біля годинника.");
                }
                return;
            }

            if (!_exiting)
            {
                e.Cancel = true;
                ExitApplication();
                return;
            }

            base.OnClosing(e);
        }

        private void ExitApplication()
        {
            if (_exiting) return;
            _exiting = true;

            try
            {
                if (_overlayEdit) SetOverlayEdit(false);
                foreach (var id in _imageWindows.Keys.ToList()) CloseImageWindow(id);

                _saveTimer.Stop();
                _topmostTimer.Stop();
                _updateTimer.Stop();
                SaveNow();

                _engine?.Dispose();
                _game.Dispose();
                _tray?.Dispose();
                _overlay?.Close();
                _quickLaw?.Close();
            }
            catch (Exception ex)
            {
                Logger.Error("Вихід", ex);
            }

            Logger.Info("=== DSBinder закрито ===");
            System.Windows.Application.Current.Shutdown();
        }

        #endregion
    }
}
