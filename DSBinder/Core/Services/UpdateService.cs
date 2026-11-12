using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace DSBinder.Core.Services
{
    /// <summary>Автооновлення через Velopack + GitHub Releases.</summary>
    public sealed class UpdateService
    {
        private readonly UpdateManager? _manager;
        private UpdateInfo? _pending;

        public UpdateService()
        {
            if (!AppInfo.IsUpdateRepoConfigured) return;

            try
            {
                _manager = new UpdateManager(new GithubSource(AppInfo.UpdateRepoUrl, null, false));
            }
            catch (Exception ex)
            {
                Logger.Error("Ініціалізація оновлень", ex);
            }
        }

        /// <summary>Оновлення можливі лише у версії, встановленій через Setup.exe.</summary>
        public bool IsAvailable
        {
            get
            {
                try { return _manager != null && _manager.IsInstalled; }
                catch { return false; }
            }
        }

        /// <summary>Чому оновлення недоступні (для підказки в налаштуваннях).</summary>
        public string UnavailableReason =>
            !AppInfo.IsUpdateRepoConfigured
                ? "Оновлення не налаштовані: вкажіть репозиторій GitHub у Core/AppInfo.cs."
                : "Оновлення працюють лише у версії, встановленій через Setup.exe (не з Visual Studio).";

        public string CurrentVersion
        {
            get
            {
                try { return _manager?.CurrentVersion?.ToString() ?? AppInfo.Version; }
                catch { return AppInfo.Version; }
            }
        }

        public string? PendingVersion => _pending?.TargetFullRelease?.Version?.ToString();

        public async Task<string?> CheckAsync()
        {
            if (!IsAvailable) return null;
            _pending = await _manager!.CheckForUpdatesAsync();
            return PendingVersion;
        }

        public async Task DownloadAsync()
        {
            if (_manager == null || _pending == null) throw new InvalidOperationException("Немає оновлення для завантаження.");
            await _manager.DownloadUpdatesAsync(_pending);
        }

        /// <summary>Встановлює завантажене оновлення і перезапускає програму (поточний процес завершиться).</summary>
        public void ApplyAndRestart()
        {
            if (_manager == null || _pending == null) return;
            _manager.ApplyUpdatesAndRestart(_pending);
        }
    }
}
