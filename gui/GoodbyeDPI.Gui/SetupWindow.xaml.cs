using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GoodbyeDPI.Gui.Controls;
using GoodbyeDPI.Gui.Core;
using GoodbyeDPI.Gui.ViewModels;

namespace GoodbyeDPI.Gui;

/// <summary>
/// İlk açılışta ana pencereden önce gösterilir: tüm yöntemleri bu ağda canlı test eder ve
/// sonunda bu cihaz için en iyi yöntemi önerir (isterse tek tıkla kurar).
/// </summary>
public partial class SetupWindow : Window
{
    private const string AppTitle = "GoodbyeDPI Turkey";

    private readonly AppSettings _settings;
    private readonly bool _preview;
    private readonly ObservableCollection<PresetRow> _rows = [];
    private readonly Stopwatch _elapsed = new();
    private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? _cts;
    private string? _folder;
    private Preset? _recommended;
    private bool _installRecommended;
    private bool _finished;

    public SetupWindow(AppSettings settings, bool preview = false)
    {
        InitializeComponent();
        _settings = settings;
        _preview = preview;
        SetupList.ItemsSource = _rows;
        _elapsedTimer.Tick += (_, _) => ElapsedText.Text = UiHelpers.FormatDuration(_elapsed.Elapsed);

        if (!preview)
            Log.LineAdded += OnLogLine;

        Loaded += async (_, _) =>
        {
            if (_preview)
                ShowPreview();
            else
                await RunAsync();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UiHelpers.ApplyDarkRoundedFrame(this);
    }

    // Log satırları canlı olarak alt bilgide akar (test sırasında neler olduğunu görmek için)
    private void OnLogLine(string line) => Dispatcher.BeginInvoke(() =>
    {
        if (!_finished)
            LogLine.Text = line.Length > 21 ? line[21..] : line; // Tarih/saat kısmını at
    });

    // ═════════════════════════ Akış ═════════════════════════

    private async Task RunAsync()
    {
        ResetUi();
        _folder = DpiService.LocateFolder(_settings.GoodbyeDpiFolder);
        if (_folder is null)
        {
            Finish("GoodbyeDPI dosyaları bulunamadı",
                "Kontrol Paneli'nden klasörü seçtikten sonra testi Yöntemler sayfasından çalıştırabilirsiniz.",
                success: false, primary: "Devam");
            return;
        }
        _settings.GoodbyeDpiFolder = _folder;

        _rows.Clear();
        foreach (var preset in PresetLoader.Load(_folder))
            _rows.Add(new PresetRow(preset));

        if (VpnWatcher.DetectNow())
        {
            // Trafik VPN'den geçerken yapılan test bu ağı değil VPN'i ölçer
            SetStep("VPN bağlı. Testin bu ağı ölçebilmesi için VPN'i kapatıp tekrar deneyin.");
            HeaderTitle.Text = "Önce VPN'i kapatın";
            LogoRing.Opacity = 0;
            RetryButton.Visibility = Visibility.Visible;
            return;
        }

        var manual = await Task.Run(DpiService.FindManualProcesses);
        if (manual.Count > 0)
        {
            if (MessageBox.Show(this, "Elle başlatılmış bir goodbyedpi.exe çalışıyor; test için durdurulması gerekiyor. Durdurulsun mu?",
                    AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                Finish("Test yapılmadı", "Testi daha sonra Yöntemler sayfasından çalıştırabilirsiniz.", success: false, primary: "Devam");
                return;
            }
            foreach (var p in manual)
                try { p.Kill(); } catch (Exception ex) { Log.Warn(ex.Message); }
        }

        var urls = _settings.TestUrls;
        var total = _rows.Count + 1;
        var byPreset = _rows.ToDictionary(r => r.Preset);
        var restarts = 0;
        var wasRunning = DpiService.GetState() is DpiServiceState.Running or DpiServiceState.Starting;
        var cancelled = false;
        string? error = null;

        _cts = new CancellationTokenSource();
        _elapsed.Restart();
        _elapsedTimer.Start();
        Log.Info("İlk kurulum testi başladı.");

        void OnEvent(TestEvent e)
        {
            switch (e)
            {
                case BaselineStarted:
                    SetStep("GoodbyeDPI olmadan test siteleri deneniyor…");
                    BaselineDot.Visibility = Visibility.Collapsed;
                    BaselineSpinner.Visibility = Visibility.Visible;
                    BaselineStatus.Text = "Kontrol ediliyor…";
                    BaselineStatus.Foreground = (Brush)FindResource("Accent2");
                    break;
                case BaselineFinished b:
                    ShowBaseline(b.Blocked, b.Result.Sites.Count);
                    SetProgress(1, total);
                    break;
                case PresetStarted p:
                    SetStep($"{p.Preset.Name} deneniyor ({p.Index + 1}/{p.Total})…");
                    var row = byPreset[p.Preset];
                    row.BeginTest();
                    BringRowIntoView(p.Index);
                    break;
                case SiteFinished s:
                    byPreset[s.Preset].AddLiveSite(s.Site);
                    break;
                case PresetFinished f:
                    var done = byPreset[f.Preset];
                    done.IsTesting = false;
                    done.Result = f.Result;
                    _settings.LastResults[f.Preset.Arguments] = f.Result;
                    Log.Info($"Test: {done.Name} -> {done.StatusText}");
                    SetProgress(f.Index + 2, total);
                    break;
                case ExternalRestartDetected:
                    restarts++;
                    break;
            }
        }

        try
        {
            if (wasRunning)
                await Task.Run(DpiService.Stop, _cts.Token);
            await TestOrchestrator.RunAsync(_folder, _rows.Select(r => r.Preset).ToList(), urls, OnEvent, _cts.Token);
            _settings.LastResultUrls = [.. urls];
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            Log.Info("İlk kurulum testi atlandı.");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Error($"İlk kurulum testi başarısız: {ex.Message}");
        }
        finally
        {
            foreach (var row in _rows.Where(r => r.IsTesting))
                row.IsTesting = false;
            _elapsed.Stop();
            _elapsedTimer.Stop();
            _cts.Dispose();
            _cts = null;

            if (wasRunning)
            {
                if (VpnWatcher.DetectNow() && _settings.AutoPauseOnVpn)
                    _settings.PausedByVpn = true;
                else
                    try { await Task.Run(DpiService.Start); }
                    catch (Exception ex) { Log.Error($"Servis tekrar başlatılamadı: {ex.Message}"); }
            }
            _settings.Save();
        }

        if (cancelled)
        {
            Close();
            return;
        }
        if (error is not null)
        {
            Finish("Test tamamlanamadı", error, success: false, primary: "Devam");
            return;
        }
        ShowSummary(restarts);
    }

    private void ShowSummary(int externalRestarts)
    {
        _recommended = PresetRanker.PickRecommended(_rows.Select(r => (r.Preset, r.Result)).ToList());
        foreach (var row in _rows)
            row.IsRecommended = row.Preset == _recommended;

        var warning = externalRestarts > 0
            ? $" Test sırasında servis {externalRestarts} kez dışarıdan açıldı; sonuçlar güvenilir olmayabilir."
            : "";

        if (_recommended is null || _rows.First(r => r.Preset == _recommended).Result is not { } result)
        {
            Finish("Bu ağda tam uyumlu bir yöntem bulunamadı",
                "Ayarlar'dan farklı test siteleri deneyebilir ya da Windows DNS ayarını değiştirip testi Yöntemler sayfasından tekrarlayabilirsiniz." + warning,
                success: false, primary: "Devam");
            return;
        }

        var (_, installedArgs) = DpiService.ReadInstalledCommand();
        var alreadyActive = DpiService.GetState() != DpiServiceState.NotInstalled &&
                            PresetLoader.Match([_recommended], installedArgs) is not null;
        var full = _rows.Count(r => r.Kind == CompatKind.Full);

        Finish($"Hazır! Bu cihaz için en iyi yöntem: {_recommended.Name}",
            $"{result.Passed}/{result.Sites.Count} site · ortalama {result.AverageMs:0} ms · {full}/{_rows.Count} yöntem tam uyumlu." +
            (alreadyActive ? " Bu yöntem zaten kurulu." : "") + warning,
            success: true,
            primary: alreadyActive ? "Başla" : "Önerileni kur ve başla",
            secondary: alreadyActive ? null : "Kurmadan devam et");
        _installRecommended = !alreadyActive;
    }

    /// <summary>Sonuç ekranı: logo durur, başlık değişir, özet kartı esneyerek belirir.</summary>
    private void Finish(string title, string detail, bool success, string primary, string? secondary = null)
    {
        _finished = true;
        Log.LineAdded -= OnLogLine;
        SetProgress(1, 1);

        HeaderTitle.Text = success ? "Kurulum tamamlandı" : "Kurulum tamamlandı, ancak…";
        UiHelpers.Animate(HeaderTitle, OpacityProperty, 1, 300, from: 0);
        UiHelpers.Animate(HeaderTitleY, TranslateTransform.YProperty, 0, 420, from: 10);
        StepText.Text = success ? "Ayarlarınız kaydedildi. Bu ekran bir daha gösterilmeyecek." : detail;
        UiHelpers.Animate(LogoRing, OpacityProperty, 0, 500);
        LogoImage.Visibility = Visibility.Collapsed;
        LogoGlyph.Text = success ? "" : "";
        LogoGlyph.Foreground = (Brush)FindResource(success ? "Good" : "Warn");
        LogoGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.ColorProperty,
            new ColorAnimation((Color)FindResource(success ? "GoodColor" : "WarnColor"), new Duration(TimeSpan.FromMilliseconds(600))));
        LogLine.Text = success ? "Ayarlar ve test sonuçları kaydedildi." : "";

        SummaryTitle.Text = title;
        SummaryDetail.Text = detail;
        SummaryGlyph.Text = success ? "" : "";
        SummaryCard.Visibility = Visibility.Visible;
        UiHelpers.Animate(SummaryCard, OpacityProperty, 1, 300, from: 0);
        var pop = new ElasticEase { Oscillations = 1, Springiness = 5 };
        UiHelpers.Animate(SummaryScale, ScaleTransform.ScaleXProperty, 1, 600, pop, from: 0.85);
        UiHelpers.Animate(SummaryScale, ScaleTransform.ScaleYProperty, 1, 600, pop, from: 0.85);

        SkipButton.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        PrimaryButton.Content = primary;
        PrimaryButton.Visibility = Visibility.Visible;
        SecondaryButton.Content = secondary;
        SecondaryButton.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ═════════════════════════ Düğmeler ═════════════════════════

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null)
            _cts.Cancel(); // Test bitince pencere kapanır
        else
            Close();
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await RunAsync();

    private void Secondary_Click(object sender, RoutedEventArgs e) => Close();

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_installRecommended && _recommended is not null && _folder is not null)
        {
            PrimaryButton.IsEnabled = false;
            SecondaryButton.IsEnabled = false;
            PrimaryButton.Content = "Kuruluyor…";
            var preset = _recommended;
            var folder = _folder;
            try
            {
                await Task.Run(() =>
                {
                    DpiService.Install(folder, preset);
                    if (VpnWatcher.DetectNow() && _settings.AutoPauseOnVpn)
                    {
                        DpiService.Stop();
                        _settings.PausedByVpn = true;
                    }
                });
                _settings.Save();
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
                MessageBox.Show(this, ex.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _cts?.Cancel();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Log.LineAdded -= OnLogLine;
        base.OnClosed(e);
    }

    // ═════════════════════════ Görsel yardımcılar ═════════════════════════

    private void ResetUi()
    {
        _finished = false;
        HeaderTitle.Text = "Cihazınız hazırlanıyor";
        LogoRing.BeginAnimation(OpacityProperty, null);
        LogoRing.Opacity = 1;
        LogoImage.Visibility = Visibility.Visible;
        LogoGlyph.Text = "";
        RetryButton.Visibility = Visibility.Collapsed;
        SkipButton.Visibility = Visibility.Visible;
        SetStep("Bu ağda hangi yöntemin çalıştığı bulunuyor…");
        SetProgress(0, 1);
    }

    private void SetStep(string text)
    {
        if (StepText.Text == text)
            return;
        StepText.Text = text;
        UiHelpers.Animate(StepText, OpacityProperty, 1, 250, from: 0.2);
    }

    private void SetProgress(int done, int total)
    {
        var fraction = total == 0 ? 0 : (double)done / total;
        PercentText.Text = $"%{fraction * 100:0}";
        ProgressTrack.UpdateLayout();
        UiHelpers.Animate(ProgressFill, WidthProperty, ProgressTrack.ActualWidth * fraction, 600);
        ProgressShineRect.Visibility = fraction >= 1 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowBaseline(int blocked, int total)
    {
        BaselineSpinner.Visibility = Visibility.Collapsed;
        BaselineDot.Visibility = Visibility.Collapsed;
        var ok = blocked > 0;
        BaselineGlyph.Text = ok ? "" : "";
        BaselineGlyph.Foreground = (Brush)FindResource(ok ? "Good" : "Warn");
        BaselineStatus.Text = ok ? $"{blocked}/{total} site engelli" : "Hiçbiri engelli değil";
        BaselineStatus.Foreground = (Brush)FindResource(ok ? "Good" : "Warn");
    }

    private void BringRowIntoView(int index)
    {
        SetupList.UpdateLayout();
        if (SetupList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
            container.BringIntoView();
    }

    // ═════════════════════════ README görseli ═════════════════════════

    /// <summary>
    /// Ekran görüntüsü için test sürüyormuş gibi bir an: son kaydedilen sonuçlardan ilk yarısı
    /// tamamlanmış, bir yöntem test ediliyor, kalanlar bekliyor. Hiçbir şey çalıştırılmaz.
    /// </summary>
    private void ShowPreview()
    {
        var folder = DpiService.LocateFolder(_settings.GoodbyeDpiFolder);
        foreach (var preset in PresetLoader.Load(folder))
            _rows.Add(new PresetRow(preset));

        var current = Math.Min(4, _rows.Count - 1);
        for (var i = 0; i < current; i++)
            if (_settings.LastResults.TryGetValue(_rows[i].Preset.Arguments, out var r))
                _rows[i].Result = r;
        _rows[current].BeginTest();
        if (_settings.LastResults.TryGetValue(_rows[current].Preset.Arguments, out var partial) && partial.Sites.Count > 0)
            _rows[current].AddLiveSite(partial.Sites[0]);

        ShowBaseline(_settings.TestUrls.Count, _settings.TestUrls.Count);
        SetStep($"{_rows[current].Name} deneniyor ({current + 1}/{_rows.Count})…");
        SetProgress(current + 1, _rows.Count + 1);
        ElapsedText.Text = "38 sn";
        LogLine.Text = "INFO   Test: " + _rows[current - 1].Name + " -> " + _rows[current - 1].StatusText;
    }

    public async Task CaptureAsync(string path)
    {
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        UiHelpers.SaveSnapshot(RootGrid, Background, path);
    }
}
