using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using DSBinder.Core.Models;

namespace DSBinder.Core.Services
{
    /// <summary>
    /// Зберігає налаштування у %AppData%\DSBinder\config.json.
    /// Раніше конфіг лежав «поруч із програмою» відносно робочої папки, через що
    /// при запуску з ярлика/автозапуску бінди могли «зникати». Старий файл імпортується автоматично.
    /// </summary>
    public class ConfigService
    {
        public static string DataDirectory { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DSBinder");

        public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private readonly object _saveLock = new();
        private bool _backupDone;

        public AppConfig LoadConfig()
        {
            foreach (string path in CandidatePaths())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    string json = File.ReadAllText(path);
                    var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                    if (config == null) continue;

                    Normalize(config);
                    if (!string.Equals(path, ConfigPath, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Info($"Імпортовано старий конфіг: {path}");
                        SaveConfig(config);
                    }
                    return config;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Не вдалося прочитати конфіг {path}", ex);
                }
            }

            var def = CreateDefaultConfig();
            Normalize(def);
            return def;
        }

        private static IEnumerable<string> CandidatePaths()
        {
            yield return ConfigPath;
            yield return ConfigPath + ".bak";
            yield return Path.Combine(AppContext.BaseDirectory, "config.json");
            yield return Path.Combine(Environment.CurrentDirectory, "config.json");
        }

        public void SaveConfig(AppConfig config)
        {
            lock (_saveLock)
            {
                try
                {
                    Directory.CreateDirectory(DataDirectory);

                    if (!_backupDone && File.Exists(ConfigPath))
                    {
                        File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true);
                        _backupDone = true;
                    }

                    string json = JsonSerializer.Serialize(config, JsonOpts);
                    string tmp = ConfigPath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, ConfigPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    Logger.Error("Не вдалося зберегти конфіг", ex);
                }
            }
        }

        public void ExportBinds(string path, IEnumerable<KeyBind> binds)
        {
            string json = JsonSerializer.Serialize(binds.ToList(), JsonOpts);
            File.WriteAllText(path, json);
        }

        /// <summary>Читає бінди з файлу: підтримує як список біндів, так і повний конфіг.</summary>
        public List<KeyBind> ImportBinds(string path)
        {
            string json = File.ReadAllText(path).TrimStart();
            List<KeyBind>? binds;

            if (json.StartsWith("["))
            {
                binds = JsonSerializer.Deserialize<List<KeyBind>>(json, JsonOpts);
            }
            else
            {
                binds = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts)?.Binds;
            }

            binds ??= new List<KeyBind>();
            foreach (var b in binds)
            {
                b.Id = Guid.NewGuid().ToString();
                NormalizeBind(b);
            }
            return binds;
        }

        public static void Normalize(AppConfig c)
        {
            c.Binds ??= new List<KeyBind>();
            c.CustomProcesses ??= new List<string>();
            c.ToggleHotkey ??= new HotkeyData(0x79, shift: true);
            c.OverlayEditHotkey ??= new HotkeyData(0x78, shift: true);
            c.OverlayVisibleHotkey ??= new HotkeyData(0x7A, shift: true);
            c.LawSearchHotkey ??= new HotkeyData(0x77, shift: true);

            if (c.ChatKeyVk == 0) c.ChatKeyVk = 0x54;
            c.ChatOpenDelayMs = Math.Clamp(c.ChatOpenDelayMs, 0, 2000);
            c.EnterDelayMs = Math.Clamp(c.EnterDelayMs, 0, 2000);
            c.OverlayOpacity = Math.Clamp(c.OverlayOpacity, 0.2, 1.0);
            c.OverlayFontSize = Math.Clamp(c.OverlayFontSize, 9, 24);
            c.OverlayWidth = Math.Clamp(c.OverlayWidth, 180, 900);
            if (!Enum.IsDefined(typeof(TextSendMode), c.SendMode)) c.SendMode = TextSendMode.Paste;

            c.CustomProcesses = c.CustomProcesses
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var ids = new HashSet<string>();
            c.Binds.RemoveAll(b => b == null);
            foreach (var b in c.Binds)
            {
                NormalizeBind(b);
                if (!ids.Add(b.Id))
                {
                    b.Id = Guid.NewGuid().ToString();
                    ids.Add(b.Id);
                }
            }

            c.ConfigVersion = 2;
        }

        private static void NormalizeBind(KeyBind b)
        {
            if (string.IsNullOrWhiteSpace(b.Id)) b.Id = Guid.NewGuid().ToString();
            b.Name = string.IsNullOrWhiteSpace(b.Name) ? "Бінд" : b.Name.Trim();
            b.Category = string.IsNullOrWhiteSpace(b.Category) ? KeyBind.DefaultCategory : b.Category.Trim();
            b.CommandText ??= "";
            b.ImageUrl ??= "";
            if (!Enum.IsDefined(typeof(BindType), b.Type)) b.Type = BindType.TextMacro;
            b.DelayMs = Math.Clamp(b.DelayMs, 0, 60000);
            b.ImageOpacity = Math.Clamp(b.ImageOpacity, 0.1, 1.0);
            b.ImageWidth = Math.Clamp(b.ImageWidth, 80, 4000);
            b.ImageHeight = Math.Clamp(b.ImageHeight, 60, 4000);
        }

        private static AppConfig CreateDefaultConfig()
        {
            return new AppConfig
            {
                Binds = new List<KeyBind>
                {
                    new KeyBind
                    {
                        Name = "Привітання",
                        Category = "Загальне",
                        VkCode = 0x61, // Num 1
                        CommandText = "/me злегка кивнув у знак привітання\nДоброго дня! Чим можу допомогти?"
                    },
                    new KeyBind
                    {
                        Name = "Показати посвідчення",
                        Category = "Робота",
                        VkCode = 0x62, // Num 2
                        CommandText = "/me дістав посвідчення з внутрішньої кишені та розкрив його\n/do Посвідчення в розкритому вигляді.\n/me показав посвідчення людині навпроти"
                    },
                    new KeyBind
                    {
                        Name = "Аптечка",
                        Category = "Загальне",
                        VkCode = 0x63, // Num 3
                        CommandText = "/me дістав аптечку та обробив рану"
                    }
                }
            };
        }
    }
}
