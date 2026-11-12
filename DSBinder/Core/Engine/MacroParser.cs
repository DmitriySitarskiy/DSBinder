using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DSBinder.Core.Engine
{
    public readonly record struct MacroStep(string? Text, int WaitMs)
    {
        public bool IsWait => Text == null;
    }

    /// <summary>
    /// Розбирає текст бінду: кожен непорожній рядок — окреме повідомлення.
    /// Рядок виду {wait:1500} / {пауза 1500} — додаткова пауза в мілісекундах.
    /// </summary>
    public static class MacroParser
    {
        private static readonly Regex WaitRegex = new(
            @"^\{\s*(?:wait|delay|pause|пауза|затримка)\s*[:=]?\s*(\d{1,6})\s*(?:ms|мс)?\s*\}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static List<MacroStep> Parse(string? text)
        {
            var steps = new List<MacroStep>();
            if (string.IsNullOrWhiteSpace(text)) return steps;

            foreach (string raw in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                var m = WaitRegex.Match(line);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int ms))
                {
                    steps.Add(new MacroStep(null, Math.Min(ms, 600_000)));
                }
                else
                {
                    steps.Add(new MacroStep(line, 0));
                }
            }
            return steps;
        }

        public static int CountMessages(string? text) => Parse(text).Count(s => !s.IsWait);
    }
}
