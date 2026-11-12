using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace DSBinder.Core.Services
{
    /// <summary>Завантажує картинки (URL або локальний файл) і кешує їх у пам'яті.</summary>
    public static class ImageCacheService
    {
        private static readonly ConcurrentDictionary<string, BitmapImage> Cache = new();
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) DSBinder/2.0");
            return client;
        }

        public static async Task<BitmapImage?> GetImageAsync(string? source)
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            source = source.Trim().Trim('"');

            if (Cache.TryGetValue(source, out var cached)) return cached;

            try
            {
                byte[] data;
                if (File.Exists(source))
                {
                    data = await File.ReadAllBytesAsync(source);
                }
                else if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
                         (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    data = await Http.GetByteArrayAsync(uri);
                }
                else
                {
                    return null;
                }

                var image = new BitmapImage();
                using (var stream = new MemoryStream(data))
                {
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                }
                image.Freeze();

                Cache[source] = image;
                return image;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Не вдалося завантажити картинку «{source}»: {ex.Message}");
                return null;
            }
        }
    }
}
