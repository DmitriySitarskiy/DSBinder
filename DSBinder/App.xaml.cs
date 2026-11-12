using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DSBinder.Core.Services;
using Velopack;

namespace DSBinder
{
    public partial class App : System.Windows.Application
    {
        private const string MutexName = @"Local\DSBinder.SingleInstance.v2";
        private const string ShowEventName = @"Local\DSBinder.ShowWindow.v2";

        private Mutex? _mutex;
        private EventWaitHandle? _showEvent;

        /// <summary>Програма працює з правами адміністратора.</summary>
        public static bool IsElevated { get; private set; }

        /// <summary>
        /// Власна точка входу (вимога Velopack): спочатку обробка подій інсталятора/оновлювача,
        /// потім запит прав адміністратора, потім звичайний запуск WPF.
        /// </summary>
        [STAThread]
        public static void Main(string[] args)
        {
            // Має бути найпершим рядком: обробляє встановлення, оновлення і видалення.
            VelopackApp.Build().Run();

            IsElevated = Elevation.IsElevated();

            bool alreadyTried = args.Any(a => a.Equals("--elevated", StringComparison.OrdinalIgnoreCase));
            bool noElevate = args.Any(a => a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase));

            // Під відладчиком (Visual Studio) не перезапускаємось — інакше відлагодження обірветься.
            if (!IsElevated && !alreadyTried && !noElevate && !Debugger.IsAttached)
            {
                if (Elevation.TryRelaunchElevated(args)) return; // запущено копію з правами адміністратора
                // користувач відмовився в UAC — працюємо без прав (з попередженням в інтерфейсі)
            }

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Дві копії біндера = кожен бінд спрацьовує двічі. Не допускаємо цього.
            bool createdNew;
            try
            {
                _mutex = new Mutex(true, MutexName, out createdNew);
            }
            catch (UnauthorizedAccessException)
            {
                createdNew = false; // копія вже працює з іншими правами
            }

            if (!createdNew)
            {
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* ignore */ }
                _mutex?.Dispose();
                _mutex = null;
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (_, args) =>
            {
                Logger.Error("Необроблена помилка UI", args.Exception);
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Logger.Error("Необроблена помилка", args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Logger.Error("Помилка задачі", args.Exception);
                args.SetObserved();
            };

            Logger.Info("=== DSBinder запущено ===");

            Core.Laws.LawService.Load();

            var window = new MainWindow();
            MainWindow = window;
            window.Initialize();

            bool startHidden = window.Config.StartMinimized ||
                               e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
            if (!startHidden) window.Show();

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var listener = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        _showEvent.WaitOne();
                        Dispatcher.InvokeAsync(window.ShowFromTray);
                    }
                    catch
                    {
                        return;
                    }
                }
            })
            {
                IsBackground = true,
                Name = "DSBinder.SingleInstance"
            };
            listener.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
            }
            catch { /* ignore */ }

            base.OnExit(e);
        }
    }
}
