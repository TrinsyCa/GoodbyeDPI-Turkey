using System.Windows;
using GoodbyeDPI.Gui.Core;
using Forms = System.Windows.Forms;

namespace GoodbyeDPI.Gui;

public partial class App : Application
{
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _trayToggle;

    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var startInTray = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);

        if (!Elevation.IsElevated)
        {
            HandleUnelevatedStart(e.Args);
            Shutdown();
            return;
        }

        _showEvent = Elevation.CreateShowEvent();
        if (_showEvent is null)
        {
            // Başka bir yönetici kopya zaten çalışıyor; ona pencereyi göstermesi söylendi
            Shutdown();
            return;
        }
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, Timeout.Infinite, executeOnlyOnce: false);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error($"Beklenmeyen hata: {args.Exception}");
            MessageBox.Show(args.Exception.Message, "GoodbyeDPI Turkey", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var settings = AppSettings.Load();
        Log.Info($"Uygulama başladı (yönetici, {(startInTray ? "tepside" : "pencereli")}).");

        // Sonraki açılışların UAC sormaması için başlatıcı görevi bu exe yoluyla güncel tut
        Task.Run(() =>
        {
            try
            {
                ScheduledTasks.EnsureLauncher();
                ScheduledTasks.SetAutostart(settings.StartWithWindows);
            }
            catch (Exception ex)
            {
                Log.Error($"Zamanlanmış görev kaydı başarısız: {ex.Message}");
            }
        });

        var window = new MainWindow(settings);
        MainWindow = window;
        CreateTray(window);
        if (!startInTray)
            window.Show();
    }

    private static void HandleUnelevatedStart(string[] args)
    {
        if (Elevation.SignalExistingInstance())
            return;

        // İlk açılıştan sonra: görev UAC sormadan yönetici kopyayı başlatır
        if (ScheduledTasks.PointsToCurrentExe(ScheduledTasks.LauncherTask) && ScheduledTasks.Run(ScheduledTasks.LauncherTask))
            return;

        // İlk açılış: bir kez UAC
        if (!Elevation.RelaunchElevated(string.Join(' ', args)))
        {
            MessageBox.Show(
                "GoodbyeDPI servisini yönetebilmek için yönetici izni gerekiyor.\n\n" +
                "İzin yalnızca ilk açılışta istenir; sonraki açılışlarda tekrar sorulmaz.",
                "GoodbyeDPI Turkey", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CreateTray(MainWindow window)
    {
        _trayToggle = new Forms.ToolStripMenuItem("GoodbyeDPI'ı aç/kapat", null, (_, _) => window.ToggleFromTray());
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Göster", null, (_, _) => ShowMainWindow());
        menu.Items.Add(_trayToggle);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => ExitApplication());

        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = "GoodbyeDPI Turkey",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowMainWindow();
    }

    /// <summary>Tepsi ipucunu ve menüyü güncel durumla eşitler.</summary>
    public void UpdateTray(string status, string toggleText, bool toggleEnabled)
    {
        if (_tray is null)
            return;
        var text = $"GoodbyeDPI Turkey – {status}";
        _tray.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon sınırı
        _trayToggle!.Text = toggleText;
        _trayToggle.Enabled = toggleEnabled;
    }

    public void ShowBalloon(string message)
    {
        _tray?.ShowBalloonTip(4000, "GoodbyeDPI Turkey", message, Forms.ToolTipIcon.Info);
    }

    public void ShowMainWindow()
    {
        if (MainWindow is not { } w)
            return;
        w.Show();
        if (w.WindowState == WindowState.Minimized)
            w.WindowState = WindowState.Normal;
        w.Activate();
        w.Topmost = true;  // Arka plandaki pencereyi öne almanın güvenilir yolu
        w.Topmost = false;
    }

    public void ExitApplication()
    {
        IsExiting = true;
        if (MainWindow is MainWindow { IsClosed: false } w)
            w.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.OnExit(e);
    }
}
