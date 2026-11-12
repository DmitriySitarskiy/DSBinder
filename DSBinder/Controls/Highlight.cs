using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DSBinder.Core.Laws;

namespace DSBinder.Controls
{
    /// <summary>
    /// Підсвічування знайдених фрагментів у TextBlock:
    /// ctl:Highlight.Text="{Binding Title}" ctl:Highlight.Query="{Binding Query}"
    /// </summary>
    public static class Highlight
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(Highlight), new PropertyMetadata(null, OnChanged));

        public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
            "Query", typeof(string), typeof(Highlight), new PropertyMetadata(null, OnChanged));

        public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
        public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);
        public static string? GetQuery(DependencyObject d) => (string?)d.GetValue(QueryProperty);
        public static void SetQuery(DependencyObject d, string? value) => d.SetValue(QueryProperty, value);

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBlock tb) Rebuild(tb);
        }

        private static void Rebuild(TextBlock tb)
        {
            string text = GetText(tb) ?? "";
            string query = GetQuery(tb) ?? "";

            tb.Inlines.Clear();
            if (text.Length == 0) return;

            var tokens = LawService.Tokenize(query).Where(t => t.Length >= 1).ToArray();
            if (tokens.Length == 0)
            {
                tb.Inlines.Add(new Run(text));
                return;
            }

            // Пошук збігів у нормалізованому тексті (довжина та сама, що в оригіналі)
            string norm = LawService.Normalize(text);
            var ranges = new List<(int Start, int End)>();
            foreach (var t in tokens)
            {
                int idx = 0;
                while ((idx = norm.IndexOf(t, idx, StringComparison.Ordinal)) >= 0)
                {
                    ranges.Add((idx, idx + t.Length));
                    idx += t.Length;
                }
            }

            if (ranges.Count == 0)
            {
                tb.Inlines.Add(new Run(text));
                return;
            }

            ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
            var merged = new List<(int Start, int End)>();
            foreach (var r in ranges)
            {
                if (merged.Count > 0 && r.Start <= merged[^1].End)
                    merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, r.End));
                else
                    merged.Add(r);
            }

            var accent = Application.Current?.TryFindResource("AccentBrush") as Brush ?? Brushes.Orange;
            var accentBg = Application.Current?.TryFindResource("AccentSoftBrush") as Brush ?? Brushes.Transparent;

            int pos = 0;
            foreach (var (start, end) in merged)
            {
                if (start > pos) tb.Inlines.Add(new Run(text.Substring(pos, start - pos)));
                tb.Inlines.Add(new Run(text.Substring(start, end - start))
                {
                    Foreground = accent,
                    Background = accentBg,
                    FontWeight = FontWeights.Bold
                });
                pos = end;
            }
            if (pos < text.Length) tb.Inlines.Add(new Run(text.Substring(pos)));
        }
    }
}
