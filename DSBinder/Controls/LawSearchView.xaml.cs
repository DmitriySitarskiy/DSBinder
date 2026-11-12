using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DSBinder.Core.Input;
using DSBinder.Core.Laws;

namespace DSBinder.Controls
{
    /// <summary>Пошук по законодавству. Використовується і на сторінці «Законодавство», і у швидкому вікні в грі.</summary>
    public partial class LawSearchView : UserControl
    {
        private readonly DispatcherTimer _searchTimer;
        private readonly DispatcherTimer _copiedTimer;
        private string? _code;
        private List<LawHit> _hits = new();

        /// <summary>Esc при порожньому полі пошуку (швидке вікно закривається).</summary>
        public event Action? EscapePressed;

        /// <summary>Компактний режим для вікна поверх гри.</summary>
        public bool IsCompact { get; set; }

        public LawSearchView()
        {
            InitializeComponent();

            // Невелика затримка, щоб не перебудовувати список на кожну літеру при швидкому наборі
            _searchTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(60) };
            _searchTimer.Tick += (_, _) =>
            {
                _searchTimer.Stop();
                RunSearch();
            };

            _copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _copiedTimer.Tick += (_, _) =>
            {
                _copiedTimer.Stop();
                CopiedInfo.Visibility = Visibility.Collapsed;
            };

            LawService.Changed += () => Dispatcher.InvokeAsync(Refresh);
            Refresh();
        }

        public void Refresh()
        {
            RebuildCodeChips();
            RunSearch();
        }

        public void FocusSearch()
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
        }

        private void RebuildCodeChips()
        {
            var codes = LawService.Library.Codes.Where(c => c.Articles.Count > 0).ToList();
            if (_code != null && !codes.Any(c => c.Short.Equals(_code, StringComparison.OrdinalIgnoreCase))) _code = null;

            CodePanel.Children.Clear();
            CodePanel.Visibility = codes.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (codes.Count <= 1) return;

            AddChip("Усі", null, LawService.TotalArticles, null);
            foreach (var c in codes) AddChip(c.Short, c.Short, c.Articles.Count, c.Name);
        }

        private void AddChip(string label, string? code, int count, string? tooltip)
        {
            var chip = new RadioButton
            {
                Content = $"{label}  {count}",
                Tag = code,
                GroupName = "LawCodes_" + GetHashCode(),
                Style = (Style)FindResource("Chip"),
                IsChecked = string.Equals(code, _code, StringComparison.OrdinalIgnoreCase),
                ToolTip = string.IsNullOrWhiteSpace(tooltip) ? null : tooltip
            };
            chip.Checked += (_, _) =>
            {
                _code = code;
                RunSearch();
            };
            CodePanel.Children.Add(chip);
        }

        private void RunSearch()
        {
            string query = SearchBox.Text;
            _hits = LawService.Search(query, _code);
            ResultsList.ItemsSource = _hits;
            if (_hits.Count > 0) ResultsList.ScrollIntoView(_hits[0]);

            ClearButton.Visibility = query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            int total = LawService.TotalArticles;
            bool hasQuery = LawService.Tokenize(query).Length > 0;

            if (total == 0)
            {
                ResultInfo.Text = "";
                ShowEmpty("", "Законодавство ще не завантажене",
                    IsCompact
                        ? "Відкрийте DSBinder → «Законодавство» → «Імпорт законки» і вставте текст або файл."
                        : "Натисніть «Імпорт законки» і вставте текст кодексу або відкрийте файл (.txt, .docx).");
            }
            else if (_hits.Count == 0)
            {
                ResultInfo.Text = "";
                ShowEmpty("", "Нічого не знайдено",
                    "Спробуйте коротшу частину слова або інший кодекс. Можна шукати за номером: «8.6».");
            }
            else
            {
                EmptyState.Visibility = Visibility.Collapsed;
                ResultsList.Visibility = Visibility.Visible;
                ResultInfo.Text = hasQuery
                    ? $"Знайдено: {_hits.Count}  ·  Enter — скопіювати «{_hits[0].Article.ShortReference}»"
                    : $"{_hits.Count} {Plural(_hits.Count)}  ·  ↓ — перейти до списку, Enter — скопіювати";
            }
        }

        private void ShowEmpty(string icon, string title, string desc)
        {
            EmptyIcon.Text = icon;
            EmptyTitle.Text = title;
            EmptyDesc.Text = desc;
            EmptyState.Visibility = Visibility.Visible;
            ResultsList.Visibility = Visibility.Collapsed;
        }

        private static string Plural(int n)
        {
            int n10 = n % 10, n100 = n % 100;
            if (n10 == 1 && n100 != 11) return "стаття";
            if (n10 >= 2 && n10 <= 4 && (n100 < 10 || n100 >= 20)) return "статті";
            return "статей";
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Clear();
            FocusSearch();
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down:
                    if (_hits.Count > 0)
                    {
                        FocusItem(0);
                        e.Handled = true;
                    }
                    break;

                case Key.Enter:
                    if (_searchTimer.IsEnabled)
                    {
                        _searchTimer.Stop();
                        RunSearch();
                    }
                    if (_hits.Count > 0) Copy(_hits[0].Article.ShortReference);
                    e.Handled = true;
                    break;

                case Key.Escape:
                    if (SearchBox.Text.Length > 0) SearchBox.Clear();
                    else EscapePressed?.Invoke();
                    e.Handled = true;
                    break;
            }
        }

        private void ResultsList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var hit = ResultsList.SelectedItem as LawHit;

            switch (e.Key)
            {
                case Key.Enter:
                    if (hit != null)
                    {
                        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) Copy(hit.Article.ToFullText(), "Скопійовано повний текст");
                        else Copy(hit.Article.ShortReference);
                    }
                    e.Handled = true;
                    break;

                case Key.C when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                    if (hit != null) Copy(hit.Article.ToFullText(), "Скопійовано повний текст");
                    e.Handled = true;
                    break;

                case Key.Up when ResultsList.SelectedIndex <= 0:
                case Key.Escape:
                    ResultsList.SelectedIndex = -1;
                    FocusSearch();
                    e.Handled = true;
                    break;

                default:
                    // Почали друкувати у списку — повертаємо фокус у пошук
                    if ((e.Key >= Key.A && e.Key <= Key.Z) || (e.Key >= Key.D0 && e.Key <= Key.D9) ||
                        e.Key == Key.Back || e.Key == Key.OemPeriod)
                    {
                        SearchBox.Focus();
                        SearchBox.CaretIndex = SearchBox.Text.Length;
                    }
                    break;
            }
        }

        private void FocusItem(int index)
        {
            ResultsList.SelectedIndex = index;
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            ResultsList.UpdateLayout();
            if (ResultsList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item) item.Focus();
        }

        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && sender is FrameworkElement fe && fe.DataContext is LawHit hit)
            {
                Copy(hit.Article.ShortReference);
                e.Handled = true;
            }
        }

        private void CopyShort_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is LawHit hit) Copy(hit.Article.ShortReference);
        }

        private void CopyFull_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is LawHit hit) Copy(hit.Article.ToFullText(), "Скопійовано повний текст");
        }

        private void Copy(string text, string? message = null)
        {
            bool ok = ClipboardHelper.TrySetText(text);
            CopiedInfo.Text = ok ? "✓ " + (message ?? $"Скопійовано: {text}") : "Не вдалося скопіювати — буфер зайнятий";
            CopiedInfo.Visibility = Visibility.Visible;
            _copiedTimer.Stop();
            _copiedTimer.Start();
        }
    }
}
