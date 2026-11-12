using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DSBinder.Core.Laws
{
    /// <summary>
    /// Розбирає текст законодавства на статті.
    ///
    /// Початок статті:  «8.6 [Р] Назва…», «Стаття 12. Назва…», «Ст. 3.1 Назва…»
    /// Поля статті:      «Пріоритет розшуку: 3.», «Карається…» / «Покарання:…» / «Штраф:…»,
    ///                   «Коментар:…» / «Примітка:…» / «Виняток…»
    /// Заголовки:        «Кримінальний кодекс» (перемикає кодекс), «Глава 8. …» / «Розділ …» (розділ)
    /// </summary>
    public static class LawParser
    {
        private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        // «Стаття 12.» / «Ст. 3.1» / «Статья 5»
        private static readonly Regex ArticleWordRx = new(
            @"^(?:стаття|статья|ст\.?)\s*(\d{1,4}(?:[.\-]\d{1,4}){0,3})\s*[.):\-–—]?\s*(.*)$", Opts);

        // «8.6 [Р] Назва» — номер із крапкою
        private static readonly Regex ArticleDottedRx = new(
            @"^(\d{1,4}(?:\.\d{1,4}){1,3})\.?\s*(?=\[|[^\d\s.])(.*)$", Opts);

        // «12. Назва» — запасний варіант, якщо в тексті немає інших форматів
        private static readonly Regex ArticleIntRx = new(
            @"^(\d{1,4})[.)]\s+(\S.*)$", Opts);

        private static readonly Regex TagRx = new(@"^\[([^\]\r\n]{1,12})\]\s*", Opts);

        private static readonly Regex PriorityRx = new(
            @"^(?:пріоритет|приоритет)(?:\s+розшуку|\s+розыска|\s+розшук)?\s*[:\-–—]?\s*(\d{1,2})", Opts);

        private static readonly Regex PunishmentRx = new(
            @"^(?:покарання|наказание|санкція|санкция|штраф)\s*[:\-–—]\s*(.*)$", Opts);

        private static readonly Regex PunishmentInlineRx = new(
            @"^(?:карається|карается|тягне\s+за\s+собою|влече[тч])", Opts);

        private static readonly Regex CommentRx = new(
            @"^(?:коментар|комментарий|примітка|примечание|прим\.|пояснення|роз'яснення|роз’яснення)\s*[:\-–—]?\s*(.*)$", Opts);

        private static readonly Regex ExceptionRx = new(
            @"^\(?\s*(?:виняток|винятки|исключение|исключения)\b", Opts);

        private static readonly Regex ChapterRx = new(
            @"^(?:глава|розділ|раздел|частина)\s+[\dIVXLCivxlc]+", Opts);

        private static readonly Regex CodeHeaderRx = new(
            @"^((?:[А-ЯҐЄІЇа-яґєії'’\-]+\s+){1,4})(кодекс)(?![а-яґєії])(.*)$", Opts);

        public sealed class ParsedCode
        {
            public string Short { get; set; } = "";
            public string Name { get; set; } = "";
            public List<LawArticle> Articles { get; } = new();
        }

        private enum Field { Title, Punishment, Comment, Extra }

        /// <summary>Розбирає текст. Статті до першого заголовка кодексу потрапляють у defaultShort/defaultName.</summary>
        public static List<ParsedCode> Parse(string text, string defaultShort, string defaultName, bool detectCodeHeaders = true)
        {
            var lines = SplitLines(text);
            bool hasStrictArticles = lines.Any(l => ArticleWordRx.IsMatch(l) || ArticleDottedRx.IsMatch(l));

            var result = new List<ParsedCode>();
            var current = new ParsedCode { Short = defaultShort, Name = defaultName };
            result.Add(current);

            LawArticle? art = null;
            string chapter = "";
            var field = Field.Title;

            foreach (var raw in lines)
            {
                string line = raw;

                // Заголовок кодексу
                if (detectCodeHeaders && TryParseCodeHeader(line, out string shortName, out string fullName))
                {
                    current = result.FirstOrDefault(c => c.Short.Equals(shortName, StringComparison.OrdinalIgnoreCase)) ??
                              AddCode(result, shortName, fullName);
                    art = null;
                    chapter = "";
                    continue;
                }

                // Глава / розділ
                if (ChapterRx.IsMatch(line) && line.Length <= 120)
                {
                    chapter = line.TrimEnd('.', ':');
                    art = null;
                    continue;
                }

                // Нова стаття
                if (TryParseArticleStart(line, hasStrictArticles, out string number, out string rest))
                {
                    art = new LawArticle { Number = number, Chapter = chapter };
                    while (true)
                    {
                        var m = TagRx.Match(rest);
                        if (!m.Success) break;
                        art.Tags.Add(m.Groups[1].Value.Trim());
                        rest = rest.Substring(m.Length);
                    }
                    art.Title = rest.Trim();
                    field = Field.Title;
                    current.Articles.Add(art);
                    continue;
                }

                if (art == null) continue; // текст поза статтями (вступ, преамбула)

                // Поля статті
                var pm = PriorityRx.Match(line);
                if (pm.Success && int.TryParse(pm.Groups[1].Value, out int prio))
                {
                    art.Priority = prio;
                    field = Field.Extra;
                    continue;
                }

                var pun = PunishmentRx.Match(line);
                if (pun.Success)
                {
                    art.Punishment = Append(art.Punishment, pun.Groups[1].Value);
                    field = Field.Punishment;
                    continue;
                }

                if (PunishmentInlineRx.IsMatch(line))
                {
                    art.Punishment = Append(art.Punishment, line);
                    field = Field.Punishment;
                    continue;
                }

                var cm = CommentRx.Match(line);
                if (cm.Success)
                {
                    art.Comment = Append(art.Comment, cm.Groups[1].Value);
                    field = Field.Comment;
                    continue;
                }

                if (ExceptionRx.IsMatch(line))
                {
                    art.Comment = Append(art.Comment, line);
                    field = Field.Comment;
                    continue;
                }

                // Продовження попереднього поля
                switch (field)
                {
                    case Field.Title:
                        art.Title = Append(art.Title, line, " ");
                        break;
                    case Field.Punishment:
                        art.Punishment = Append(art.Punishment, line);
                        break;
                    case Field.Comment:
                        art.Comment = Append(art.Comment, line);
                        break;
                    default:
                        art.Extra = Append(art.Extra, line);
                        break;
                }
            }

            result.RemoveAll(c => c.Articles.Count == 0);
            return result;
        }

        private static ParsedCode AddCode(List<ParsedCode> list, string shortName, string name)
        {
            var c = new ParsedCode { Short = shortName, Name = name };
            list.Add(c);
            return c;
        }

        private static bool TryParseArticleStart(string line, bool strict, out string number, out string rest)
        {
            var m = ArticleWordRx.Match(line);
            if (!m.Success) m = ArticleDottedRx.Match(line);
            if (!m.Success && !strict) m = ArticleIntRx.Match(line);

            if (m.Success)
            {
                number = m.Groups[1].Value.Replace('-', '.').TrimEnd('.');
                rest = m.Groups[2].Value.Trim();
                return true;
            }

            number = rest = "";
            return false;
        }

        /// <summary>«Кримінальний кодекс» → КК, «Кримінально-процесуальний кодекс» → КПК.</summary>
        public static bool TryParseCodeHeader(string line, out string shortName, out string fullName)
        {
            shortName = fullName = "";
            string l = line.Trim().TrimEnd('.', ':');
            if (l.Length < 6 || l.Length > 70) return false;
            if (char.IsDigit(l[0]) || l.Contains(':')) return false;

            var m = CodeHeaderRx.Match(l);
            if (!m.Success) return false;

            // Після слова «кодекс» допускаємо лише коротке уточнення («штату San Andreas», «(КК)» тощо)
            string tail = m.Groups[3].Value.Trim();
            if (tail.Length > 30) return false;

            var words = m.Groups[1].Value
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(w => w.Split('-', StringSplitOptions.RemoveEmptyEntries))
                .ToList();

            if (words.Count == 0) return false;

            var sb = new StringBuilder();
            foreach (var w in words) sb.Append(char.ToUpper(w[0], CultureInfo.CurrentCulture));
            sb.Append('К');
            shortName = sb.ToString();

            // Якщо в дужках явно вказана абревіатура: «Кримінальний кодекс (КК)»
            var abbr = Regex.Match(tail, @"\(([А-ЯҐЄІЇA-Z]{2,6})\)");
            if (abbr.Success) shortName = abbr.Groups[1].Value;

            fullName = ToSentenceCase(l);
            return true;
        }

        private static string ToSentenceCase(string s)
        {
            bool allUpper = s.Where(char.IsLetter).All(char.IsUpper);
            if (!allUpper) return s;
            string lower = s.ToLower(CultureInfo.CurrentCulture);
            return char.ToUpper(lower[0], CultureInfo.CurrentCulture) + lower.Substring(1);
        }

        private static string Append(string existing, string addition, string separator = "\n")
        {
            addition = addition.Trim();
            if (addition.Length == 0) return existing;
            return string.IsNullOrEmpty(existing) ? addition : existing + separator + addition;
        }

        private static List<string> SplitLines(string text)
        {
            var list = new List<string>();
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string l = raw.Replace(' ', ' ').Replace('\t', ' ').Trim();
                if (l.Length == 0) continue;
                // прибираємо маркери списків: «• », «- », «— »
                if (l.Length > 2 && (l[0] == '•' || l[0] == '▪' || l[0] == '●') && l[1] == ' ') l = l.Substring(2).TrimStart();
                list.Add(l);
            }
            return list;
        }

        #region Читання файлів

        /// <summary>Читає .txt / .md (UTF-8 або Windows-1251) та .docx.</summary>
        public static string ReadFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".docx") return ReadDocx(path);

            byte[] bytes = File.ReadAllBytes(path);
            try
            {
                var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                return utf8.GetString(bytes).TrimStart('﻿');
            }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(1251).GetString(bytes);
            }
        }

        private static string ReadDocx(string path)
        {
            const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("Це не схоже на документ Word (.docx).");

            var doc = new XmlDocument();
            using (var s = entry.Open()) doc.Load(s);

            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("w", w);

            var sb = new StringBuilder();
            var paragraphs = doc.SelectNodes("//w:body//w:p", ns);
            if (paragraphs == null) return "";

            foreach (XmlNode p in paragraphs)
            {
                foreach (XmlNode node in p.SelectNodes(".//w:t | .//w:tab | .//w:br", ns)!)
                {
                    if (node.LocalName == "t") sb.Append(node.InnerText);
                    else if (node.LocalName == "tab") sb.Append(' ');
                    else sb.Append('\n');
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        #endregion
    }
}
