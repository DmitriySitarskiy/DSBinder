using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DSBinder.Core.Services;

namespace DSBinder.Core.Laws
{
    /// <summary>
    /// Сховище законодавства (%AppData%\DSBinder\laws.json) і швидкий пошук.
    /// Пошук: усі слова запиту мають зустрічатися в статті (частина слова теж підходить).
    /// На кількох тисячах статей пошук займає лічені мілісекунди.
    /// </summary>
    public static class LawService
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        public static string LawsPath => Path.Combine(ConfigService.DataDirectory, "laws.json");

        public static LawLibrary Library { get; private set; } = new();

        public static int TotalArticles => Library.Codes.Sum(c => c.Articles.Count);

        /// <summary>Законодавство змінилося (імпорт, видалення).</summary>
        public static event Action? Changed;

        public static void Load()
        {
            try
            {
                if (File.Exists(LawsPath))
                {
                    var lib = JsonSerializer.Deserialize<LawLibrary>(File.ReadAllText(LawsPath), JsonOpts);
                    if (lib != null) Library = lib;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Не вдалося прочитати законодавство", ex);
            }

            Library.Codes ??= new List<LawCode>();
            Reindex();
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigService.DataDirectory);
                string tmp = LawsPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Library, JsonOpts));
                File.Move(tmp, LawsPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.Error("Не вдалося зберегти законодавство", ex);
            }
        }

        /// <summary>Додає розпізнані кодекси. replace = true — повністю замінює статті кодексу з тією ж абревіатурою.</summary>
        public static int Import(IEnumerable<LawParser.ParsedCode> parsed, bool replace)
        {
            int count = 0;
            foreach (var p in parsed)
            {
                if (p.Articles.Count == 0) continue;
                string shortName = string.IsNullOrWhiteSpace(p.Short) ? "ЗАК" : p.Short.Trim();

                var code = Library.Codes.FirstOrDefault(c => c.Short.Equals(shortName, StringComparison.OrdinalIgnoreCase));
                if (code == null)
                {
                    code = new LawCode { Short = shortName, Name = p.Name.Trim() };
                    Library.Codes.Add(code);
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(p.Name)) code.Name = p.Name.Trim();
                    if (replace) code.Articles.Clear();
                }

                if (replace)
                {
                    code.Articles.AddRange(p.Articles);
                }
                else
                {
                    // Без заміни: статті з тим самим номером оновлюються, нові — додаються
                    foreach (var a in p.Articles)
                    {
                        int idx = code.Articles.FindIndex(x => x.Number == a.Number);
                        if (idx >= 0) code.Articles[idx] = a;
                        else code.Articles.Add(a);
                    }
                }
                count += p.Articles.Count;
            }

            Reindex();
            Save();
            Changed?.Invoke();
            return count;
        }

        /// <summary>Імпорт готового laws.json (наприклад, підготовленого заздалегідь).</summary>
        public static int ImportJson(string json, bool replace)
        {
            var lib = JsonSerializer.Deserialize<LawLibrary>(json, JsonOpts) ?? new LawLibrary();
            var parsed = lib.Codes.Select(c =>
            {
                var p = new LawParser.ParsedCode { Short = c.Short, Name = c.Name };
                p.Articles.AddRange(c.Articles);
                return p;
            }).ToList();
            return Import(parsed, replace);
        }

        public static void RemoveCode(string shortName)
        {
            Library.Codes.RemoveAll(c => c.Short.Equals(shortName, StringComparison.OrdinalIgnoreCase));
            Reindex();
            Save();
            Changed?.Invoke();
        }

        public static void RenameCode(string oldShort, string newShort, string newName)
        {
            var code = Library.Codes.FirstOrDefault(c => c.Short.Equals(oldShort, StringComparison.OrdinalIgnoreCase));
            if (code == null) return;
            code.Short = newShort.Trim();
            code.Name = newName.Trim();
            Reindex();
            Save();
            Changed?.Invoke();
        }

        /// <summary>Перебудовує пошукові індекси (нижній регістр, ключі сортування).</summary>
        private static void Reindex()
        {
            int order = 0;
            foreach (var code in Library.Codes)
            {
                code.Articles ??= new List<LawArticle>();
                foreach (var a in code.Articles)
                {
                    a.Tags ??= new List<string>();
                    a.Number ??= "";
                    a.Title ??= "";
                    a.Punishment ??= "";
                    a.Comment ??= "";
                    a.Extra ??= "";
                    a.Chapter ??= "";

                    a.CodeShort = code.Short;
                    a.Order = order++;
                    a.NumberLower = a.Number.ToLowerInvariant();
                    a.TitleLower = Normalize(a.Title);
                    a.SortKey = a.Number.Split('.', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => int.TryParse(s, out int n) ? n : 0).ToArray();

                    var sb = new StringBuilder(256);
                    sb.Append(code.Short).Append(' ').Append(code.Name).Append(' ')
                      .Append("ст ").Append(a.Number).Append(' ')
                      .Append(string.Join(" ", a.Tags)).Append(' ')
                      .Append(a.Title).Append(' ')
                      .Append(a.Punishment).Append(' ')
                      .Append(a.Comment).Append(' ')
                      .Append(a.Extra).Append(' ')
                      .Append(a.Chapter);
                    if (a.Priority.HasValue) sb.Append(" розшук ").Append(a.Priority.Value);
                    a.SearchText = Normalize(sb.ToString());
                }
            }
        }

        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                char c = char.ToLowerInvariant(ch);
                switch (c)
                {
                    case '’':
                    case 'ʼ':
                    case '`':
                    case '´':
                        c = '\'';
                        break;
                    case 'ё':
                        c = 'е';
                        break;
                    case '\n':
                    case '\r':
                    case '\t':
                        c = ' ';
                        break;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        public static string[] Tokenize(string query) =>
            Normalize(query).Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim('"', '«', '»'))
                .Where(t => t.Length > 0)
                .Distinct()
                .ToArray();

        /// <summary>Пошук. codeFilter = null — усі кодекси.</summary>
        public static List<LawHit> Search(string query, string? codeFilter)
        {
            var tokens = Tokenize(query);
            var hits = new List<LawHit>();

            foreach (var code in Library.Codes)
            {
                if (codeFilter != null && !code.Short.Equals(codeFilter, StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var a in code.Articles)
                {
                    if (tokens.Length == 0)
                    {
                        hits.Add(new LawHit(a, code, query, 0));
                        continue;
                    }

                    int score = 0;
                    bool all = true;
                    foreach (var t in tokens)
                    {
                        // «ст.8.6», «8.6», «ст.» — номер статті
                        string tn = (t.StartsWith("ст.") && t.Length > 3 ? t.Substring(3) : t).TrimEnd('.');

                        if (tn.Length > 0 && a.NumberLower == tn) { score += 1000; continue; }
                        if (tn.Length > 0 && char.IsDigit(tn[0]) &&
                            a.NumberLower.StartsWith(tn + ".", StringComparison.Ordinal)) { score += 400; continue; }
                        if (!a.SearchText.Contains(t, StringComparison.Ordinal)) { all = false; break; }

                        if (code.Short.Equals(t, StringComparison.OrdinalIgnoreCase)) score += 20;
                        int ti = a.TitleLower.IndexOf(t, StringComparison.Ordinal);
                        if (ti >= 0)
                        {
                            score += 60;
                            if (ti == 0 || !char.IsLetterOrDigit(a.TitleLower[ti - 1])) score += 40; // початок слова
                        }
                        else
                        {
                            score += 10;
                        }
                    }

                    if (all) hits.Add(new LawHit(a, code, query, score));
                }
            }

            if (tokens.Length == 0) return hits; // порядок як у кодексі

            hits.Sort((x, y) =>
            {
                int c = y.Score.CompareTo(x.Score);
                return c != 0 ? c : x.Article.Order.CompareTo(y.Article.Order);
            });
            return hits;
        }
    }
}
