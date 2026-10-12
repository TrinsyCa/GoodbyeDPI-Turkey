using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GoodbyeDPI.Gui.Core;
using GoodbyeDPI.Gui.ViewModels;
using Microsoft.Win32;

namespace GoodbyeDPI.Gui;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const string AppTitle = "GoodbyeDPI Turkey";

    private readonly AppSettings _settings;
    private readonly VpnWatcher _vpn;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _toastTimer;
    private ObservableCollection<PresetRow> _rows = [];
    private string? _folder;
    private DpiServiceState _state;
    private CancellationTokenSource? _testCts;
    private bool _busy;
    private int _tick;
    private bool _trayHintShown;
    private string? _visualKey;
    private bool? _vpnVisual;
    private DateTime? _stoppedByUsAt;

    /// <summary>Test ya da servis işlemi sürerken satır butonları kapalı.</summary>
    public bool CanAct => !_busy;

    public bool IsClosed { get; private set; }

    private static App CurrentApp => (App)Application.Current;

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        DataContext = this;
        _settings = settings;

        AutoPauseCheck.IsChecked = settings.AutoPauseOnVpn;
        AutostartCheck.IsChecked = settings.StartWithWindows;
        TrayCheck.IsChecked = settings.MinimizeToTray;
        UrlsBox.Text = string.Join(Environment.NewLine, settings.TestUrls);
        Log.LineAdded += line => Dispatcher.BeginInvoke(() => AppendLog(line));

        LoadFolderAndPresets();

        _vpn = new VpnWatcher();
        _vpn.VpnStateChanged += active => Dispatcher.BeginInvoke(() => OnVpnChangedAsync(active));

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, _) => HideToast();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        StateChanged += (_, _) => OnWindowStateChanged();
        Loaded += (_, _) =>
        {
            Refresh();
            MoveNavIndicator(NavDashboard, animate: false);
            ShowPage(PageDashboard);
        };
        _ = CheckBannersAsync(includeLegacyTask: true);

        // Uygulama kapalıyken VPN kapandıysa, VPN yüzünden durdurduğumuz servisi geri aç.
        // (VPN hâlâ açıksa izleyicinin ilk kontrolü zaten değişiklik olarak bildirir.)
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (_settings.PausedByVpn && !_vpn.IsVpnActive)
                await ResumeAfterVpnAsync();
        });
    }

    // ═════════════════════════ Pencere çerçevesi ═════════════════════════

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1, round = 2;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));  // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE = yuvarlak (Win11)
    }

    private void OnWindowStateChanged()
    {
        // WindowChrome ile büyütülen pencere ekran kenarlarından taşar
        RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxGlyph.Text = WindowState == WindowState.Maximized ? "" : "";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ═════════════════════════ Gezinme ═════════════════════════

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton nav)
            return;
        MoveNavIndicator(nav, animate: true);
        ShowPage(nav == NavMethods ? PageMethods : nav == NavSettings ? PageSettings : nav == NavLogs ? PageLogs : PageDashboard);
    }

    private void MoveNavIndicator(FrameworkElement nav, bool animate)
    {
        var y = nav.TranslatePoint(new Point(0, 0), NavPanel).Y;
        if (animate)
            Animate(NavIndicatorY, TranslateTransform.YProperty, y, 380, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });
        else
            NavIndicatorY.Y = y;
    }

    private void ShowPage(FrameworkElement page)
    {
        foreach (FrameworkElement p in Pages.Children)
            if (p != page)
                p.Visibility = Visibility.Collapsed;

        page.Visibility = Visibility.Visible;
        Animate(page, OpacityProperty, 1, 280, from: 0);
        Animate((TranslateTransform)page.RenderTransform, TranslateTransform.YProperty, 0, 420, Ease(), from: 22);

        if (page == PageMethods)
            AnimateCardsIn();
    }

    /// <summary>Yöntem kartları sırayla, aşağıdan kayarak gelir.</summary>
    private void AnimateCardsIn()
    {
        PresetList.UpdateLayout();
        for (var i = 0; i < _rows.Count; i++)
        {
            if (PresetList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;
            if (container.RenderTransform is not TranslateTransform t || t.IsFrozen)
                container.RenderTransform = t = new TranslateTransform();
            var delay = 60 + i * 55;
            Animate(container, OpacityProperty, 1, 350, from: 0, delayMs: delay);
            Animate(t, TranslateTransform.YProperty, 0, 520, Ease(), from: 26, delayMs: delay);
        }
    }

    // ═════════════════════════ Durum ═════════════════════════

    private void LoadFolderAndPresets()
    {
        _folder = DpiService.LocateFolder(_settings.GoodbyeDpiFolder);
        if (_folder is not null && !string.Equals(_folder, _settings.GoodbyeDpiFolder, StringComparison.OrdinalIgnoreCase))
        {
            _settings.GoodbyeDpiFolder = _folder;
            _settings.Save();
        }
        FolderBox.Text = _folder ?? "(bulunamadı)";
        FolderBanner.Visibility = _folder is null ? Visibility.Visible : Visibility.Collapsed;

        _rows = new ObservableCollection<PresetRow>(PresetLoader.Load(_folder).Select(p => new PresetRow(p)));
        PresetList.ItemsSource = _rows;

        // Aynı sitelerle yapılmış son testin sonuçlarını geri yükle
        if (_settings.LastResultUrls.SequenceEqual(_settings.TestUrls))
        {
            foreach (var row in _rows)
                if (_settings.LastResults.TryGetValue(row.Preset.Arguments, out var result))
                    row.Result = result;
            UpdateRecommendation(notify: false);
        }
    }

    private void Refresh()
    {
        _state = DpiService.GetState();
        DetectExternalRestart();
        var (_, args) = DpiService.ReadInstalledCommand();
        var active = _state == DpiServiceState.NotInstalled ? null : PresetLoader.Match(_rows.Select(r => r.Preset), args);
        foreach (var row in _rows)
            row.IsActive = row.Preset == active;

        var recommended = _rows.FirstOrDefault(r => r.IsRecommended);
        var methodName = active?.Name ?? (string.IsNullOrEmpty(args) ? "—" : "Özel ayar");
        string title, detail, hint, trayToggle;
        Color color;
        bool pulse = false, spin = _busy;
        var canToggle = !_busy;

        switch (_state)
        {
            case DpiServiceState.Running:
                (title, detail, hint, trayToggle, color, pulse) =
                    ("Koruma açık", $"{methodName} yöntemi arka planda çalışıyor.", "Kapatmak için dokunun", "GoodbyeDPI'ı kapat", C("GoodColor"), true);
                break;
            case DpiServiceState.Stopped when _settings.PausedByVpn && _vpn.IsVpnActive:
                (title, detail, hint, trayToggle, color) =
                    ("VPN nedeniyle duraklatıldı", "VPN kapanınca GoodbyeDPI kendiliğinden açılacak.", "Yine de açmak için dokunun", "GoodbyeDPI'ı aç", C("WarnColor"));
                break;
            case DpiServiceState.Stopped:
                (title, detail, hint, trayToggle, color) =
                    ("Koruma kapalı", $"Kurulu yöntem: {methodName}", "Açmak için dokunun", "GoodbyeDPI'ı aç", C("IdleColor"));
                break;
            case DpiServiceState.Starting:
            case DpiServiceState.Stopping:
                (title, detail, hint, trayToggle, color, spin) =
                    (_state == DpiServiceState.Starting ? "Başlatılıyor…" : "Durduruluyor…", methodName, "Lütfen bekleyin", "Bekleyin", C("Accent2Color"), true);
                canToggle = false;
                break;
            default:
                title = "Kurulu değil";
                detail = recommended is not null
                    ? $"Test sonucuna göre bu cihaz için en iyisi: {recommended.Name}"
                    : "Yöntemler sayfasından testi çalıştırın; cihazınızda çalışanı tek dokunuşla kurun.";
                hint = recommended is not null ? "Önerileni kurmak için dokunun" : "Önce yöntemleri test edin";
                trayToggle = recommended is not null ? "Önerileni kur" : "Kurulu değil";
                canToggle &= recommended is not null;
                color = C("IdleColor");
                break;
        }

        if (_testCts is not null)
        {
            hint = "Test sürüyor…";
            spin = true;
            canToggle = false;
        }

        ApplyStatusVisual(title, color, pulse, spin);
        StatusDetail.Text = detail;
        PowerHint.Text = hint;
        PowerButton.IsEnabled = canToggle;
        PowerButton.Cursor = canToggle ? Cursors.Hand : Cursors.Arrow;

        StatMethod.Text = methodName;
        StatVpn.Text = _vpn.IsVpnActive ? "Bağlı" : "Bağlı değil";
        VpnSideText.Text = _vpn.IsVpnActive ? _vpn.ActiveAdapter ?? "Bağlı" : "Bağlı değil";
        ApplyVpnVisual(_vpn.IsVpnActive);

        CurrentApp.UpdateTray(title, trayToggle, canToggle);

        if (++_tick % 5 == 0 && !_busy)
            _ = CheckBannersAsync(includeLegacyTask: false);
    }

    private void DetectExternalRestart()
    {
        if (_busy || _testCts is not null || _state is not (DpiServiceState.Running or DpiServiceState.Starting))
            return;

        var stoppedRecently = _stoppedByUsAt is { } t && DateTime.UtcNow - t < TimeSpan.FromSeconds(30);
        var pausedForVpn = _settings.PausedByVpn && _vpn.IsVpnActive;
        if (stoppedRecently || pausedForVpn)
        {
            _stoppedByUsAt = null;
            if (pausedForVpn)
                _settings.PausedByVpn = false; // Duraklatma bozuldu; VPN kapanınca "geri açma" yapılmasın
            OnExternalRestart();
        }
    }

    /// <summary>
    /// Durum değiştiyse güç düğmesinin renklerini yumuşakça geçirir ve halkaları açıp kapatır.
    /// Durum her 2 saniyede yenilendiği için aynı durumda tekrar animasyon başlatılmaz.
    /// </summary>
    private void ApplyStatusVisual(string title, Color color, bool pulse, bool spin)
    {
        var key = $"{title}|{color}|{pulse}|{spin}";
        if (key == _visualKey)
            return;
        var firstTime = _visualKey is null;
        _visualKey = key;

        if (StatusTitle.Text != title)
        {
            StatusTitle.Text = title;
            if (!firstTime)
            {
                Animate(StatusTitle, OpacityProperty, 1, 300, from: 0);
                Animate(StatusTitleY, TranslateTransform.YProperty, 0, 420, Ease(), from: 10);
            }
        }

        AnimateColor(CoreStrokeBrush, color);
        AnimateColor(PowerIconBrush, color);
        AnimateColor(PulseBrush1, color);
        AnimateColor(PulseBrush2, color);
        AnimateColor(HaloStop, Color.FromArgb(0x55, color.R, color.G, color.B));
        CoreGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.ColorProperty, new ColorAnimation(color, Ms(500)));
        Animate(CoreGlow, System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, pulse ? 0.85 : 0.45, 500);

        Animate(PulseGroup, OpacityProperty, pulse ? 1 : 0, 500);
        Animate(SpinGroup, OpacityProperty, spin ? 1 : 0, 300);

        // Duruma geçerken düğme kısa bir "nefes" alır
        if (!firstTime)
        {
            Animate(PowerScale, ScaleTransform.ScaleXProperty, 1, 600, new ElasticEase { Oscillations = 1, Springiness = 4 }, from: 0.9);
            Animate(PowerScale, ScaleTransform.ScaleYProperty, 1, 600, new ElasticEase { Oscillations = 1, Springiness = 4 }, from: 0.9);
        }
    }

    private void ApplyVpnVisual(bool active)
    {
        if (_vpnVisual == active)
            return;
        _vpnVisual = active;
        var color = active ? C("Accent2Color") : C("IdleColor");
        AnimateColor(VpnDotBrush, color);
        VpnDotPulse.Fill = new SolidColorBrush(color);
        if (active)
        {
            var grow = new DoubleAnimation(1, 2.6, Ms(1600)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Ease() };
            VpnPulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            VpnPulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            VpnDotPulse.BeginAnimation(OpacityProperty, new DoubleAnimation(0.7, 0, Ms(1600)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            VpnDotPulse.BeginAnimation(OpacityProperty, null);
            VpnDotPulse.Opacity = 0;
        }
    }

    /// <summary>Process/schtasks çağırdığı için arka planda ve seyrek çalışır.</summary>
    private async Task CheckBannersAsync(bool includeLegacyTask)
    {
        var (manual, legacy) = await Task.Run(() =>
            (DpiService.FindManualProcesses().Count > 0,
             includeLegacyTask ? ConflictDetector.FindLegacyVpnSwitch() : null));
        SetBanner(ManualBanner, manual);
        if (legacy is not null)
        {
            SetBanner(LegacyBanner, legacy.Found);
            if (legacy.Found)
            {
                SetBanner(ExternalBanner, false); // Sebep belli, genel uyarıya gerek yok
                Log.Warn($"Eski VPN script'i bulundu: {legacy.TaskPaths.Count} görev, {legacy.ProcessIds.Count} işlem.");
            }
        }
    }

    /// <summary>
    /// Kullanıcı kapattıktan (ya da VPN yüzünden duraklattıktan) sonra servis yeniden açıldıysa,
    /// bunu başka bir şey yapmıştır. Sessizce "Koruma açık" göstermek yerine kullanıcıyı uyar.
    /// </summary>
    private void OnExternalRestart()
    {
        Log.Warn("GoodbyeDPI servisi bu uygulamanın dışında yeniden başlatıldı.");
        Notify("Başka bir program GoodbyeDPI'ı geri açtı. Kontrol Paneli'ndeki uyarıya bakın.", ToastKind.Warn);
        SetBanner(ExternalBanner, true);
        _ = CheckBannersAsync(includeLegacyTask: true);
    }

    private void DismissExternal_Click(object sender, RoutedEventArgs e) => SetBanner(ExternalBanner, false);

    private void SetBanner(FrameworkElement banner, bool visible)
    {
        var isVisible = banner.Visibility == Visibility.Visible;
        if (visible == isVisible)
            return;
        if (visible)
        {
            banner.Visibility = Visibility.Visible;
            banner.RenderTransform = new TranslateTransform();
            Animate(banner, OpacityProperty, 1, 300, from: 0);
            Animate((TranslateTransform)banner.RenderTransform, TranslateTransform.XProperty, 0, 450, Ease(), from: -24);
        }
        else
        {
            banner.Visibility = Visibility.Collapsed;
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        OnChanged(nameof(CanAct));
        Refresh();
    }

    // ═════════════════════════ Güç düğmesi etkileşimi ═════════════════════════

    private void PowerButton_MouseEnter(object sender, MouseEventArgs e) => ScalePower(1.05, 260);
    private void PowerButton_MouseLeave(object sender, MouseEventArgs e) => ScalePower(1, 320);
    private void PowerButton_Down(object sender, MouseButtonEventArgs e) => ScalePower(0.94, 90);
    private void PowerButton_Up(object sender, MouseButtonEventArgs e) => ScalePower(PowerButton.IsMouseOver ? 1.05 : 1, 420, new ElasticEase { Oscillations = 1, Springiness = 5 });

    private void ScalePower(double to, int ms, IEasingFunction? ease = null)
    {
        if (!PowerButton.IsEnabled)
            to = 1;
        Animate(PowerScale, ScaleTransform.ScaleXProperty, to, ms, ease ?? Ease());
        Animate(PowerScale, ScaleTransform.ScaleYProperty, to, ms, ease ?? Ease());
    }

    // ═════════════════════════ VPN ═════════════════════════

    private async Task OnVpnChangedAsync(bool vpnActive)
    {
        Refresh();
        if (!_settings.AutoPauseOnVpn)
            return;

        if (vpnActive && _testCts is not null)
        {
            // Test sırasında trafik VPN'e geçerse sonuçlar bu ağı yansıtmaz
            Log.Warn("VPN bağlandı, test iptal edildi.");
            _testCts.Cancel();
            return; // Testin finally bloğu servisi duraklatılmış olarak işaretler
        }
        if (_busy)
            return;

        if (vpnActive && _state is DpiServiceState.Running or DpiServiceState.Starting)
        {
            if (await RunServiceOpAsync(DpiService.Stop))
            {
                SetPausedByVpn(true);
                Notify("VPN algılandı, GoodbyeDPI durduruldu. VPN kapanınca tekrar açılacak.", ToastKind.Warn);
            }
        }
        else if (!vpnActive && _settings.PausedByVpn)
        {
            await ResumeAfterVpnAsync();
        }
    }

    private async Task ResumeAfterVpnAsync()
    {
        if (_state == DpiServiceState.Stopped && await RunServiceOpAsync(DpiService.Start))
            Notify("VPN kapandı, GoodbyeDPI tekrar açıldı.", ToastKind.Good);
        SetPausedByVpn(false);
    }

    private void SetPausedByVpn(bool value)
    {
        _settings.PausedByVpn = value;
        _settings.Save();
        Refresh();
    }

    // ═════════════════════════ Servis işlemleri ═════════════════════════

    /// <summary>Servis işlemleri birkaç saniye sürebilir; arayüzü kilitlememek için arka planda.</summary>
    private async Task<bool> RunServiceOpAsync(Action operation)
    {
        _stoppedByUsAt = null; // Bizim yaptığımız açma/kurma dış müdahale sayılmasın; durdurma sonrası yeniden ayarlanır
        SetBusy(true);
        try
        {
            await Task.Run(operation);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            MessageBox.Show(this, ex.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e) => await ToggleAsync();

    public async void ToggleFromTray() => await ToggleAsync();

    private async Task ToggleAsync()
    {
        if (_busy || _testCts is not null)
            return;
        switch (_state)
        {
            case DpiServiceState.Running:
                if (await RunServiceOpAsync(DpiService.Stop))
                {
                    _stoppedByUsAt = DateTime.UtcNow;
                    SetPausedByVpn(false);
                    Notify("GoodbyeDPI kapatıldı.", ToastKind.Info);
                }
                break;

            case DpiServiceState.Stopped:
                if (_vpn.IsVpnActive && !Confirm(
                        "VPN bağlıyken GoodbyeDPI, VPN tünelinin içindeki trafiği de değiştirip bağlantıyı bozabilir.\n\nYine de açılsın mı?"))
                    return;
                if (await RunServiceOpAsync(DpiService.Start))
                {
                    SetPausedByVpn(false);
                    Notify("GoodbyeDPI açıldı.", ToastKind.Good);
                }
                break;

            case DpiServiceState.NotInstalled when _rows.FirstOrDefault(r => r.IsRecommended) is { } recommended:
                await InstallAsync(recommended);
                break;
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is PresetRow row)
            await InstallAsync(row);
    }

    private async Task InstallAsync(PresetRow row)
    {
        if (_folder is not { } folder)
        {
            MessageBox.Show(this, "Önce GoodbyeDPI klasörünü seçin.", AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (row.Kind is CompatKind.None && !Confirm($"{row.Name} bu cihazda yapılan testte hiçbir siteyi açamadı.\n\nYine de kurulsun mu?"))
            return;

        if (!await RunServiceOpAsync(() => DpiService.Install(folder, row.Preset)))
            return;

        if (_vpn.IsVpnActive && _settings.AutoPauseOnVpn)
        {
            await RunServiceOpAsync(DpiService.Stop);
            SetPausedByVpn(true);
            Notify($"{row.Name} kuruldu. VPN bağlı olduğu için şimdilik durduruldu; VPN kapanınca açılacak.", ToastKind.Warn);
        }
        else
        {
            SetPausedByVpn(false);
            Notify($"{row.Name} kuruldu ve etkinleştirildi.", ToastKind.Good);
        }
    }

    private async void RemoveService_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("GoodbyeDPI servisi ve WinDivert sürücüleri kaldırılacak (service_remove.cmd ile aynı). Devam edilsin mi?"))
            return;
        if (await RunServiceOpAsync(DpiService.Remove))
        {
            SetPausedByVpn(false);
            Notify("Servis ve sürücüler kaldırıldı.", ToastKind.Info);
        }
    }

    private async void StopManual_Click(object sender, RoutedEventArgs e)
    {
        await Task.Run(() =>
        {
            foreach (var p in DpiService.FindManualProcesses())
            {
                try { p.Kill(); Log.Info($"Elle başlatılmış goodbyedpi.exe durduruldu (PID {p.Id})."); }
                catch (Exception ex) { Log.Error($"PID {p.Id} durdurulamadı: {ex.Message}"); }
            }
        });
        await CheckBannersAsync(includeLegacyTask: false);
    }

    private async void RemoveLegacyTask_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Task.Run(() => ConflictDetector.RemoveLegacyVpnSwitch(ConflictDetector.FindLegacyVpnSwitch()));
            Notify("Eski VPN script'i kaldırıldı. Artık kapattığınızda kapalı kalır.", ToastKind.Good);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        await CheckBannersAsync(includeLegacyTask: true);
    }

    // ═════════════════════════ Test ═════════════════════════

    private async void TestAll_Click(object sender, RoutedEventArgs e) => await TestAsync(_rows.ToList());

    private async void TestOne_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is PresetRow row)
            await TestAsync([row]);
    }

    private void CancelTest_Click(object sender, RoutedEventArgs e) => _testCts?.Cancel();

    private async Task TestAsync(IReadOnlyList<PresetRow> rows)
    {
        if (_busy)
            return;
        if (_folder is not { } folder)
        {
            MessageBox.Show(this, "Önce GoodbyeDPI klasörünü seçin.", AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_vpn.IsVpnActive)
        {
            MessageBox.Show(this,
                "VPN bağlıyken test yapılamaz: trafik VPN'den geçtiği için sonuçlar bu ağı yansıtmaz.\n\nVPN'i kapatıp tekrar deneyin.",
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var urls = _settings.TestUrls;
        if (urls.Count == 0)
        {
            MessageBox.Show(this, "Ayarlardan en az bir test sitesi ekleyin.", AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var manual = await Task.Run(DpiService.FindManualProcesses);
        if (manual.Count > 0)
        {
            if (!Confirm("Elle başlatılmış bir goodbyedpi.exe çalışıyor; test için durdurulması gerekiyor. Durdurulsun mu?"))
                return;
            foreach (var p in manual)
                try { p.Kill(); } catch (Exception ex) { Log.Warn(ex.Message); }
        }

        _testCts = new CancellationTokenSource();
        var ct = _testCts.Token;
        var wasRunning = _state is DpiServiceState.Running or DpiServiceState.Starting;
        var steps = rows.Count + 1; // + engelsiz kontrol
        SetBusy(true);
        TestAllButton.IsEnabled = false;
        CancelTestButton.Visibility = Visibility.Visible;
        ShowTestProgress(true);
        SetProgress(0, steps, "GoodbyeDPI olmadan erişim kontrol ediliyor…");

        var externalRestarts = 0;
        // Servis test sırasında dışarıdan açılırsa (ör. eski VPN script'i) iki goodbyedpi aynı anda
        // çalışır ve sonuçlar o yöntemi değil servisi ölçer. Her adımda kontrol edip yeniden durdur.
        async Task EnsureServiceStoppedAsync()
        {
            if (await Task.Run(DpiService.GetState, ct) is DpiServiceState.Running or DpiServiceState.Starting)
            {
                externalRestarts++;
                Log.Warn("Test sırasında GoodbyeDPI servisi dışarıdan başlatıldı, tekrar durduruluyor.");
                await Task.Run(DpiService.Stop, ct);
            }
        }

        try
        {
            if (wasRunning)
                await Task.Run(DpiService.Stop, ct);

            var baseline = await TestRunner.RunBaselineAsync(urls, ct);
            await EnsureServiceStoppedAsync();
            var blocked = baseline.Sites.Count(s => !s.Ok);
            ShowBaseline(blocked, baseline.Sites.Count);
            Log.Info($"Engelsiz kontrol: {blocked}/{baseline.Sites.Count} site engelli.");

            for (var i = 0; i < rows.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = rows[i];
                SetProgress(i + 1, steps, $"{row.Name} deneniyor…");
                await EnsureServiceStoppedAsync();
                row.IsTesting = true;
                try
                {
                    row.Result = await TestRunner.RunPresetAsync(folder, row.Preset, urls, ct);
                }
                finally
                {
                    row.IsTesting = false;
                }
                Log.Info($"Test: {row.Name} -> {row.StatusText}");
                _settings.LastResults[row.Preset.Arguments] = row.Result;
            }
            await EnsureServiceStoppedAsync();
            _settings.LastResultUrls = [.. urls];
            SetProgress(steps, steps, "Test tamamlandı");
        }
        catch (OperationCanceledException)
        {
            Log.Info("Test iptal edildi.");
            SetProgress(0, steps, "Test iptal edildi");
        }
        catch (Exception ex)
        {
            Log.Error($"Test hatası: {ex.Message}");
            MessageBox.Show(this, ex.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (wasRunning)
            {
                if (_vpn.IsVpnActive && _settings.AutoPauseOnVpn)
                    _settings.PausedByVpn = true; // Test sırasında VPN bağlandı
                else
                    try { await Task.Run(DpiService.Start); }
                    catch (Exception ex) { Log.Error($"Servis tekrar başlatılamadı: {ex.Message}"); }
                _settings.Save();
            }

            _settings.Save();
            _testCts.Dispose();
            _testCts = null;
            TestAllButton.IsEnabled = true;
            CancelTestButton.Visibility = Visibility.Collapsed;
            UpdateRecommendation(notify: externalRestarts == 0);
            SetBusy(false);
            _ = HideTestProgressLaterAsync();

            if (externalRestarts > 0)
            {
                BaselineIcon.Text = "";
                BaselineIcon.Foreground = (Brush)FindResource("Warn");
                BaselineText.Text = $"Test sırasında GoodbyeDPI servisi {externalRestarts} kez dışarıdan yeniden başlatıldı; " +
                                    "sonuçlar güvenilir olmayabilir. Kontrol Paneli'ndeki uyarıyı giderip testi tekrarlayın.";
                OnExternalRestart();
            }
        }
    }

    private void ShowBaseline(int blocked, int total)
    {
        BaselineCard.Visibility = Visibility.Visible;
        Animate(BaselineCard, OpacityProperty, 1, 300, from: 0);
        if (blocked == 0)
        {
            BaselineIcon.Text = "";
            BaselineIcon.Foreground = (Brush)FindResource("Warn");
            BaselineText.Text = "Test sitelerinin hepsi GoodbyeDPI olmadan da açılıyor. Bu ağda engelli değiller, sonuçlar yanıltıcı olabilir; Ayarlar'dan engelli siteler ekleyin.";
        }
        else
        {
            BaselineIcon.Text = "";
            BaselineIcon.Foreground = (Brush)FindResource("Accent2");
            BaselineText.Text = $"GoodbyeDPI olmadan {blocked}/{total} test sitesi engelli, yani test anlamlı.";
        }
    }

    private void ShowTestProgress(bool visible)
    {
        if (visible)
        {
            TestProgressCard.Visibility = Visibility.Visible;
            Animate(TestProgressCard, OpacityProperty, 1, 250, from: 0);
        }
        else
        {
            var fade = new DoubleAnimation(0, Ms(400));
            fade.Completed += (_, _) => { if (_testCts is null) TestProgressCard.Visibility = Visibility.Collapsed; };
            TestProgressCard.BeginAnimation(OpacityProperty, fade);
        }
    }

    private async Task HideTestProgressLaterAsync()
    {
        await Task.Delay(2500);
        if (_testCts is null)
            ShowTestProgress(false);
    }

    private void SetProgress(int done, int total, string text)
    {
        TestProgressText.Text = text;
        TestProgressCount.Text = $"{done}/{total}";
        Animate(ProgressScale, ScaleTransform.ScaleXProperty, total == 0 ? 0 : (double)done / total, 500, Ease());
    }

    private void UpdateRecommendation(bool notify = true)
    {
        var recommended = PresetRanker.PickRecommended(_rows.Select(r => (r.Preset, r.Result)).ToList());
        foreach (var row in _rows)
            row.IsRecommended = row.Preset == recommended;

        var tested = _rows.Count(r => r.Result is not null);
        if (tested > 0)
            StatTest.Text = $"{_rows.Count(r => r.Kind == CompatKind.Full)}/{tested} tam uyumlu";
        if (recommended is not null && notify)
            Notify($"Test bitti. Bu cihaz için önerilen yöntem: {recommended.Name}", ToastKind.Good);
    }

    // ═════════════════════════ Ayarlar ═════════════════════════

    private async void SettingCheck_Click(object sender, RoutedEventArgs e)
    {
        var autostartChanged = _settings.StartWithWindows != (AutostartCheck.IsChecked == true);
        _settings.AutoPauseOnVpn = AutoPauseCheck.IsChecked == true;
        _settings.StartWithWindows = AutostartCheck.IsChecked == true;
        _settings.MinimizeToTray = TrayCheck.IsChecked == true;
        if (!_settings.AutoPauseOnVpn)
            _settings.PausedByVpn = false; // Otomatik açma kapatıldıysa "duraklatıldı" durumunda takılı kalmasın
        _settings.Save();

        if (autostartChanged)
        {
            var enabled = _settings.StartWithWindows;
            try { await Task.Run(() => ScheduledTasks.SetAutostart(enabled)); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        // Ayar şimdi açıldıysa ve VPN zaten bağlıysa hemen uygula
        if (sender == AutoPauseCheck && _settings.AutoPauseOnVpn && _vpn.IsVpnActive)
            await OnVpnChangedAsync(true);
        Refresh();
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "GoodbyeDPI release klasörünü seçin (içinde x86_64\\ olan)" };
        if (dialog.ShowDialog(this) != true)
            return;
        if (!DpiService.IsValidFolder(dialog.FolderName))
        {
            MessageBox.Show(this, $"Bu klasörde {DpiService.ArchFolder}\\goodbyedpi.exe yok.", AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.GoodbyeDpiFolder = dialog.FolderName;
        _settings.Save();
        LoadFolderAndPresets();
        Refresh();
        Notify("GoodbyeDPI klasörü güncellendi.", ToastKind.Good);
    }

    private void SaveUrls_Click(object sender, RoutedEventArgs e)
    {
        var lines = UrlsBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var invalid = lines.Where(l => !Uri.TryCreate(l, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http")).ToList();
        if (invalid.Count > 0)
        {
            MessageBox.Show(this, "Geçersiz adres:\n" + string.Join("\n", invalid), AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.TestUrls = lines.ToList();
        _settings.LastResults.Clear();
        _settings.LastResultUrls.Clear();
        _settings.Save();
        foreach (var row in _rows)
            row.Result = null; // Eski sonuçlar başka sitelerle alındı
        foreach (var row in _rows)
            row.IsRecommended = false;
        StatTest.Text = "Henüz test yok";
        Log.Info($"{lines.Length} test sitesi kaydedildi.");
        Notify($"{lines.Length} test sitesi kaydedildi.", ToastKind.Good);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (System.IO.File.Exists(Log.FilePath))
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
    }

    // ═════════════════════════ Bildirim ═════════════════════════

    private enum ToastKind { Info, Good, Warn }

    /// <summary>Pencere açıksa uygulama içi bildirim, tepsideyse Windows balonu.</summary>
    private void Notify(string message, ToastKind kind)
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            CurrentApp.ShowBalloon(message);
            return;
        }

        ToastText.Text = message;
        var (glyph, color) = kind switch
        {
            ToastKind.Good => ("", C("GoodColor")),
            ToastKind.Warn => ("", C("WarnColor")),
            _ => ("", C("AccentColor")),
        };
        ToastIcon.Text = glyph;
        ToastIcon.Foreground = new SolidColorBrush(color);
        ToastIconBg.Color = Color.FromArgb(0x33, color.R, color.G, color.B);

        Animate(Toast, OpacityProperty, 1, 250);
        Animate(ToastY, TranslateTransform.YProperty, 0, 500, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 });
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        Animate(Toast, OpacityProperty, 0, 350);
        Animate(ToastY, TranslateTransform.YProperty, 24, 350, Ease());
    }

    // ═════════════════════════ Yardımcılar ═════════════════════════

    private Color C(string key) => (Color)FindResource(key);

    private static Duration Ms(int ms) => new(TimeSpan.FromMilliseconds(ms));

    private static IEasingFunction Ease() => new CubicEase { EasingMode = EasingMode.EaseOut };

    private static void Animate(IAnimatable target, DependencyProperty property, double to, int ms,
        IEasingFunction? ease = null, double? from = null, int delayMs = 0)
    {
        var animation = new DoubleAnimation(to, Ms(ms))
        {
            EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseInOut },
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
        };
        if (from is { } f)
            animation.From = f;
        target.BeginAnimation(property, animation);
    }

    private static void AnimateColor(Animatable target, Color to)
    {
        var property = target is GradientStop ? GradientStop.ColorProperty : SolidColorBrush.ColorProperty;
        target.BeginAnimation(property, new ColorAnimation(to, Ms(500)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        if (LogBox.LineCount > 400)
            LogBox.Text = string.Join(Environment.NewLine, LogBox.Text.Split(Environment.NewLine).TakeLast(250));
        LogBox.ScrollToEnd();
    }

    private bool Confirm(string message) =>
        MessageBox.Show(this, message, AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!CurrentApp.IsExiting && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                CurrentApp.ShowBalloon("Uygulama tepside çalışmaya devam ediyor. Çıkmak için tepsi simgesine sağ tıklayın.");
                _trayHintShown = true;
            }
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        IsClosed = true;
        _timer.Stop();
        _toastTimer.Stop();
        _vpn.Dispose();
        _testCts?.Cancel();
        TestRunner.KillOwnedProcesses(); // Test sırasında kapatılırsa goodbyedpi alt işlemi geride kalmasın
        base.OnClosed(e);
        if (!CurrentApp.IsExiting)
            CurrentApp.ExitApplication();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
