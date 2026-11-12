using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace DSBinder.Core.Laws
{
    /// <summary>Одна стаття законодавства.</summary>
    public sealed class LawArticle
    {
        public string Number { get; set; } = "";
        public List<string> Tags { get; set; } = new();
        public string Title { get; set; } = "";
        public string Chapter { get; set; } = "";
        public int? Priority { get; set; }
        public string Punishment { get; set; } = "";
        public string Comment { get; set; } = "";
        /// <summary>Інші рядки статті, що не підпали під відомі поля.</summary>
        public string Extra { get; set; } = "";

        // --- Службові поля (будуються при завантаженні) ---
        [JsonIgnore] public string CodeShort { get; internal set; } = "";
        [JsonIgnore] internal string SearchText { get; set; } = "";
        [JsonIgnore] internal string TitleLower { get; set; } = "";
        [JsonIgnore] internal string NumberLower { get; set; } = "";
        [JsonIgnore] internal int[] SortKey { get; set; } = Array.Empty<int>();
        [JsonIgnore] internal int Order { get; set; }

        public string ShortReference => $"ст. {Number} {CodeShort}".Trim();

        public string ToFullText()
        {
            var sb = new StringBuilder();
            sb.Append(Number);
            foreach (var t in Tags) sb.Append(" [").Append(t).Append(']');
            sb.Append(' ').Append(Title);
            if (!string.IsNullOrEmpty(CodeShort)) sb.Append(" (").Append(CodeShort).Append(')');
            if (Priority.HasValue) sb.AppendLine().Append("Пріоритет розшуку: ").Append(Priority.Value).Append('.');
            if (!string.IsNullOrWhiteSpace(Punishment)) sb.AppendLine().Append(Punishment);
            if (!string.IsNullOrWhiteSpace(Comment)) sb.AppendLine().Append("Коментар: ").Append(Comment);
            if (!string.IsNullOrWhiteSpace(Extra)) sb.AppendLine().Append(Extra);
            return sb.ToString();
        }
    }

    /// <summary>Кодекс / закон: КК, АК, ТК тощо.</summary>
    public sealed class LawCode
    {
        public string Short { get; set; } = "";
        public string Name { get; set; } = "";
        public List<LawArticle> Articles { get; set; } = new();
    }

    public sealed class LawLibrary
    {
        public int Version { get; set; } = 1;
        public List<LawCode> Codes { get; set; } = new();
    }

    /// <summary>Результат пошуку для відображення в списку.</summary>
    public sealed class LawHit
    {
        public LawHit(LawArticle article, LawCode code, string query, int score)
        {
            Article = article;
            Code = code;
            Query = query;
            Score = score;
        }

        public LawArticle Article { get; }
        public LawCode Code { get; }
        public string Query { get; }
        public int Score { get; }

        public string CodeShort => Code.Short;
        public string CodeName => Code.Name;
        public string NumberText => "ст. " + Article.Number;
        public string Title => Article.Title;
        public string Punishment => Article.Punishment;
        public string Comment => Article.Comment;
        public string Extra => Article.Extra;
        public string Chapter => Article.Chapter;
        public string TagsText => string.Join(" ", Article.Tags.Select(t => $"[{t}]"));

        public bool HasTags => Article.Tags.Count > 0;
        public bool HasPriority => Article.Priority.HasValue;
        public bool HasPunishment => !string.IsNullOrWhiteSpace(Article.Punishment);
        public bool HasComment => !string.IsNullOrWhiteSpace(Article.Comment);
        public bool HasExtra => !string.IsNullOrWhiteSpace(Article.Extra);
        public bool HasChapter => !string.IsNullOrWhiteSpace(Article.Chapter);
        public string PriorityText => Article.Priority.HasValue ? $"Розшук {Article.Priority.Value}" : "";

        public System.Windows.Media.Brush CodeBrush => LawColors.ForCode(Code.Short);
        public System.Windows.Media.Brush CodeBackground => LawColors.ForCodeBackground(Code.Short);
        public System.Windows.Media.Brush PriorityBrush => LawColors.ForPriority(Article.Priority ?? 0);
        public System.Windows.Media.Brush PriorityBackground => LawColors.ForPriorityBackground(Article.Priority ?? 0);
    }

    /// <summary>Кольори бейджів кодексів і рівнів розшуку.</summary>
    public static class LawColors
    {
        private static readonly Dictionary<string, System.Windows.Media.Color> Known = new(StringComparer.OrdinalIgnoreCase)
        {
            ["КК"] = Rgb(0xF8, 0x71, 0x71),
            ["АК"] = Rgb(0xF5, 0xA5, 0x24),
            ["КУпАП"] = Rgb(0xF5, 0xA5, 0x24),
            ["ТК"] = Rgb(0x60, 0xA5, 0xFA),
            ["ДК"] = Rgb(0x34, 0xD3, 0x99),
            ["ПДР"] = Rgb(0x34, 0xD3, 0x99),
            ["ПК"] = Rgb(0xA7, 0x8B, 0xFA),
            ["КПК"] = Rgb(0xA7, 0x8B, 0xFA),
        };

        private static readonly System.Windows.Media.Color[] Palette =
        {
            Rgb(0xF4, 0x72, 0xB6), Rgb(0x22, 0xD3, 0xEE), Rgb(0xFB, 0x92, 0x3C),
            Rgb(0xA3, 0xE6, 0x35), Rgb(0x81, 0x8C, 0xF8), Rgb(0x2D, 0xD4, 0xBF)
        };

        private static readonly Dictionary<string, System.Windows.Media.SolidColorBrush> Cache = new();

        private static System.Windows.Media.Color Rgb(byte r, byte g, byte b) => System.Windows.Media.Color.FromRgb(r, g, b);

        public static System.Windows.Media.Color ColorForCode(string code)
        {
            if (Known.TryGetValue(code, out var c)) return c;
            int hash = 0;
            foreach (char ch in code.ToUpperInvariant()) hash = hash * 31 + ch;
            return Palette[Math.Abs(hash % Palette.Length)];
        }

        public static System.Windows.Media.Brush ForCode(string code) => Get("c:" + code, ColorForCode(code), 255);

        public static System.Windows.Media.Brush ForCodeBackground(string code) => Get("cb:" + code, ColorForCode(code), 0x2A);

        private static System.Windows.Media.Color PriorityColor(int p) => p switch
        {
            >= 4 => Rgb(0xF8, 0x71, 0x71),
            3 => Rgb(0xFB, 0x92, 0x3C),
            2 => Rgb(0xF5, 0xA5, 0x24),
            _ => Rgb(0x8B, 0x94, 0xA3)
        };

        public static System.Windows.Media.Brush ForPriority(int p) => Get("p:" + p, PriorityColor(p), 255);

        public static System.Windows.Media.Brush ForPriorityBackground(int p) => Get("pb:" + p, PriorityColor(p), 0x26);

        private static System.Windows.Media.Brush Get(string key, System.Windows.Media.Color color, byte alpha)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(key, out var b)) return b;
                b = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, color.R, color.G, color.B));
                b.Freeze();
                Cache[key] = b;
                return b;
            }
        }
    }
}
