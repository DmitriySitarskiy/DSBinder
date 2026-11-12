using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DSBinder.Core.Laws;
using DSBinder.Core.Services;

namespace DSBinder
{
    /// <summary>Імпорт законодавства: вставка тексту або файл, автоматичне розпізнавання статей і попередній перегляд.</summary>
    public partial class LawImportWindow : Window
    {
        private sealed class GroupRow
        {
            public string Short { get; init; } = "";
            public string Label { get; init; } = "";
            public Brush Foreground { get; init; } = Brushes.White;
            public Brush Background { get; init; } = Brushes.Transparent;
        }

        private sealed class PreviewRow
        {
            public string Code { get; init; } = "";
            public string Number { get; init; } = "";
            public string Title { get; init; } = "";
            public string Meta { get; init; } = "";
            public Brush CodeBrush { get; init; } = Brushes.White;
        }

        private static readonly (string Short, string Name)[] Presets =
        {
            ("КК", "Кримінальний кодекс"),
            ("АК", "Адміністративний кодекс"),
            ("ТК", "Трудовий кодекс"),
            ("ДК", "Дорожній кодекс"),
            ("ПК", "Процесуальний кодекс")
        };

        private readonly DispatcherTimer _parseTimer;
        private List<LawParser.ParsedCode> _parsed = new();

        public int ImportedCount { get; private set; }

        public LawImportWindow()
        {
            InitializeComponent();

            _parseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _parseTimer.Tick += (_, _) =>
            {
                _parseTimer.Stop();
                BuildPreview();
            };

            ShortBox.Text = "КК";
            NameBox.Text = "Кримінальний кодекс";

            var presets = Presets.ToList();
            foreach (var c in LawService.Library.Codes)
            {
                if (!presets.Any(p => p.Short.Equals(c.Short, StringComparison.OrdinalIgnoreCase)))
                    presets.Add((c.Short, c.Name));
            }

            foreach (var (s, n) in presets)
            {
                var b = new Button
                {
                    Content = s,
                    ToolTip = n,
                    Style = (Style)FindResource("ChipButton"),
                    Focusable = false
                };
                b.Click += (_, _) =>
                {
                    ShortBox.Text = s;
                    NameBox.Text = n;
                };
                PresetPanel.Children.Add(b);
            }

            Loaded += (_, _) => SourceBox.Focus();
        }

        private void SourceBox_TextChanged(object sender, TextChangedEventArgs e) => SchedulePreview();

        private void CodeBox_TextChanged(object sender, TextChangedEventArgs e) => SchedulePreview();

        private void SchedulePreview()
        {
            if (_parseTimer == null) return;
            _parseTimer.Stop();
            _parseTimer.Start();
        }

        private void BuildPreview()
        {
            string text = SourceBox.Text;
            string shortName = string.IsNullOrWhiteSpace(ShortBox.Text) ? "КК" : ShortBox.Text.Trim();
            string name = string.IsNullOrWhiteSpace(NameBox.Text) ? shortName : NameBox.Text.Trim();

            try
            {
                _parsed = LooksLikeJson(text) ? ParseJson(text) : LawParser.Parse(text, shortName, name);
            }
            catch (Exception ex)
            {
                Logger.Warn("Розбір законки: " + ex.Message);
                _parsed = new List<LawParser.ParsedCode>();
            }

            int total = _parsed.Sum(p => p.Articles.Count);
            SummaryText.Text = $"Розпізнано: {total} {Plural(total)}";

            GroupsList.ItemsSource = _parsed.Select(p => new GroupRow
            {
                Short = p.Short,
                Label = $"{p.Name} — {p.Articles.Count} {Plural(p.Articles.Count)}" +
                        (LawService.Library.Codes.Any(c => c.Short.Equals(p.Short, StringComparison.OrdinalIgnoreCase)) ? " (вже є)" : ""),
                Foreground = LawColors.ForCode(p.Short),
                Background = LawColors.ForCodeBackground(p.Short)
            }).ToList();

            PreviewList.ItemsSource = _parsed.SelectMany(p => p.Articles.Select(a => new PreviewRow
            {
                Code = p.Short,
                CodeBrush = LawColors.ForCode(p.Short),
                Number = a.Number,
                Title = a.Title.Length > 0 ? a.Title : "(без назви)",
                Meta = BuildMeta(a)
            })).ToList();

            ImportButton.IsEnabled = total > 0;
            ImportButtonText.Text = total > 0 ? $"Імпортувати {total} {Plural(total)}" : "Імпортувати";

            // Підказки щодо якості розпізнавання
            if (text.Trim().Length > 0 && total == 0)
            {
                WarningText.Text = "Статті не розпізнано. Перевірте, що кожна стаття починається з номера на новому рядку: «8.6 Назва…» або «Стаття 12. Назва…».";
            }
            else if (total > 0)
            {
                int noTitle = _parsed.Sum(p => p.Articles.Count(a => a.Title.Length == 0));
                int noPunishment = _parsed.Sum(p => p.Articles.Count(a => a.Punishment.Length == 0));
                WarningText.Text = noTitle > 0
                    ? $"У {noTitle} статей немає назви — можливо, номер і назва на різних рядках."
                    : noPunishment > total / 2
                        ? $"У {noPunishment} статей не знайдено покарання (рядок «Карається…» / «Покарання:»). Це нормально, якщо їх там немає."
                        : "";
            }
            else
            {
                WarningText.Text = "";
            }
        }

        private static string BuildMeta(LawArticle a)
        {
            var parts = new List<string>();
            if (a.Tags.Count > 0) parts.Add(string.Join(" ", a.Tags.Select(t => $"[{t}]")));
            if (a.Priority.HasValue) parts.Add($"розшук {a.Priority}");
            if (a.Punishment.Length > 0) parts.Add("покарання ✓");
            if (a.Comment.Length > 0) parts.Add("коментар ✓");
            return string.Join(" · ", parts);
        }

        private static bool LooksLikeJson(string text)
        {
            string t = text.TrimStart();
            return t.StartsWith("{") && t.Contains("\"Codes\"", StringComparison.OrdinalIgnoreCase);
        }

        private static List<LawParser.ParsedCode> ParseJson(string json)
        {
            var lib = JsonSerializer.Deserialize<LawLibrary>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var list = new List<LawParser.ParsedCode>();
            if (lib?.Codes == null) return list;

            foreach (var c in lib.Codes)
            {
                var p = new LawParser.ParsedCode { Short = c.Short, Name = c.Name };
                foreach (var a in c.Articles ?? new List<LawArticle>())
                {
                    a.Tags ??= new List<string>();
                    a.Title ??= "";
                    a.Punishment ??= "";
                    a.Comment ??= "";
                    a.Extra ??= "";
                    a.Chapter ??= "";
                    p.Articles.Add(a);
                }
                list.Add(p);
            }
            return list;
        }

        private static string Plural(int n)
        {
            int n10 = n % 10, n100 = n % 100;
            if (n10 == 1 && n100 != 11) return "стаття";
            if (n10 >= 2 && n10 <= 4 && (n100 < 10 || n100 >= 20)) return "статті";
            return "статей";
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Оберіть файл законодавства",
                Filter = "Текст і документи|*.txt;*.md;*.docx;*.json|Усі файли|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                string text = LawParser.ReadFile(dlg.FileName);
                SourceBox.Text = text;
                FileInfoText.Text = $"Файл: {Path.GetFileName(dlg.FileName)} · {text.Length:N0} символів";

                // Підставляємо назву кодексу з назви файлу, якщо вона схожа на «Кримінальний кодекс.docx»
                string fileName = Path.GetFileNameWithoutExtension(dlg.FileName);
                if (LawParser.TryParseCodeHeader(fileName, out var s, out var n))
                {
                    ShortBox.Text = s;
                    NameBox.Text = n;
                }

                _parseTimer.Stop();
                BuildPreview();
            }
            catch (Exception ex)
            {
                Logger.Error("Відкриття файлу законки", ex);
                WarningText.Text = "Не вдалося прочитати файл: " + ex.Message;
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            _parseTimer.Stop();
            BuildPreview();
            if (_parsed.Sum(p => p.Articles.Count) == 0) return;

            ImportedCount = LawService.Import(_parsed, ReplaceSwitch.IsChecked == true);
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DialogResult = false;
            }
        }
    }
}
