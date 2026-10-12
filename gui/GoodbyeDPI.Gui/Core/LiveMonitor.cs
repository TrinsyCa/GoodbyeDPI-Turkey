using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace GoodbyeDPI.Gui.Core;

public sealed record TrafficSample(double DownPerSec, double UpPerSec, long TotalDown, long TotalUp);

public sealed record PublicIpInfo(string Ip, string Location, string Provider);

public sealed record ProcessStats(TimeSpan Uptime, double CpuPercent, long MemoryBytes);

/// <summary>
/// Kontrol Paneli'ndeki canlı analizlerin veri kaynağı. Saniyede bir trafik ve goodbyedpi işlem
/// istatistiği üretir; genel IP'yi ve engelli siteye erişimi daha seyrek kontrol eder.
/// Olaylar iş parçacığı havuzundan gelir.
/// </summary>
public sealed class LiveMonitor : IDisposable
{
    private static readonly TimeSpan IpInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReachInterval = TimeSpan.FromSeconds(45);

    // Aynı trafiği iki kez saymamak için: VPN tünelleri fiziksel kartın üstünden geçer,
    // Hyper-V/WSL sanal kartları da öyle.
    private static readonly string[] SkipAdapters =
        ["ProtonVPN", "WireGuard", "Wintun", "TAP-Windows", "OpenVPN", "Hyper-V", "Virtual", "Loopback", "Pseudo"];

    private readonly System.Threading.Timer _timer;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly Func<string?> _probeUrl;
    private readonly Func<bool> _pauseProbes;
    private long _lastDown, _lastUp, _startDown, _startUp;
    private DateTime _lastSampleAt;
    private DateTime _nextIpAt = DateTime.MinValue, _nextReachAt = DateTime.UtcNow.AddSeconds(3);
    private (int Pid, TimeSpan Cpu, DateTime At)? _lastCpu;
    private int _busy;

    public event Action<TrafficSample>? Traffic;
    public event Action<PublicIpInfo?>? PublicIp;
    public event Action<SiteResult>? Reachability;
    public event Action<ProcessStats?>? ServiceProcess;

    /// <param name="probeUrl">Erişimi izlenen engelli site (ilk test sitesi).</param>
    /// <param name="pauseProbes">Test sürerken site kontrolü yapılmasın (test trafiğine karışmasın).</param>
    public LiveMonitor(Func<string?> probeUrl, Func<bool> pauseProbes)
    {
        _probeUrl = probeUrl;
        _pauseProbes = pauseProbes;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GoodbyeDPI-Turkey-GUI");
        (_startDown, _startUp) = ReadCounters();
        (_lastDown, _lastUp) = (_startDown, _startUp);
        _lastSampleAt = DateTime.UtcNow;
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>VPN bağlanıp kopunca genel IP değişir; hemen yeniden sorgula.</summary>
    public void RefreshPublicIpSoon() => _nextIpAt = DateTime.UtcNow.AddSeconds(3);

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;
        try
        {
            var now = DateTime.UtcNow;
            var (down, up) = ReadCounters();
            var seconds = Math.Max((now - _lastSampleAt).TotalSeconds, 0.001);
            // Kart sayısı değişince (VPN açılıp kapanınca) sayaçlar geri gidebilir: negatif hız gösterme
            var downRate = Math.Max(0, (down - _lastDown) / seconds);
            var upRate = Math.Max(0, (up - _lastUp) / seconds);
            (_lastDown, _lastUp, _lastSampleAt) = (down, up, now);
            Traffic?.Invoke(new TrafficSample(downRate, upRate, Math.Max(0, down - _startDown), Math.Max(0, up - _startUp)));

            ServiceProcess?.Invoke(ReadProcessStats(now));

            if (now >= _nextIpAt)
            {
                _nextIpAt = now + IpInterval;
                _ = FetchPublicIpAsync();
            }
            if (now >= _nextReachAt && !_pauseProbes() && _probeUrl() is { } url)
            {
                _nextReachAt = now + ReachInterval;
                _ = ProbeAsync(url);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Canlı analiz güncellenemedi: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static (long Down, long Up) ReadCounters()
    {
        long down = 0, up = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            if (SkipAdapters.Any(k => nic.Description.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                                      nic.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                continue;
            var stats = nic.GetIPStatistics();
            down += stats.BytesReceived;
            up += stats.BytesSent;
        }
        return (down, up);
    }

    /// <summary>Servisin çalıştırdığı goodbyedpi.exe: çalışma süresi, işlemci ve bellek.</summary>
    private ProcessStats? ReadProcessStats(DateTime now)
    {
        if (DpiService.GetState() != DpiServiceState.Running)
        {
            _lastCpu = null;
            return null;
        }
        var processes = System.Diagnostics.Process.GetProcessesByName("goodbyedpi");
        try
        {
            var p = processes.FirstOrDefault(x => !TestRunner.IsOwnedTestProcess(x.Id));
            if (p is null)
                return null;

            var cpu = p.TotalProcessorTime;
            double percent = 0;
            if (_lastCpu is { } last && last.Pid == p.Id)
            {
                var wall = (now - last.At).TotalMilliseconds;
                percent = wall > 0 ? (cpu - last.Cpu).TotalMilliseconds / wall / Environment.ProcessorCount * 100 : 0;
            }
            _lastCpu = (p.Id, cpu, now);
            return new ProcessStats(DateTime.Now - p.StartTime, Math.Max(0, percent), p.WorkingSet64);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null; // İşlem tam o anda kapandı ya da erişim yok
        }
        finally
        {
            foreach (var p in processes)
                p.Dispose();
        }
    }

    private async Task FetchPublicIpAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync("https://ipinfo.io/json"));
            var root = doc.RootElement;
            string Get(string name) => root.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

            var location = string.Join(", ", new[] { Get("city"), Get("country") }.Where(s => s.Length > 0));
            // "AS34984 Superonline Iletisim Hizmetleri A.S." -> "Superonline Iletisim Hizmetleri A.S."
            var org = Get("org");
            var space = org.IndexOf(' ');
            if (org.StartsWith("AS", StringComparison.Ordinal) && space > 0)
                org = org[(space + 1)..];
            PublicIp?.Invoke(new PublicIpInfo(Get("ip"), location, org));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            PublicIp?.Invoke(null);
        }
    }

    private async Task ProbeAsync(string url)
    {
        try
        {
            Reachability?.Invoke(await TestRunner.ProbeAsync(url, CancellationToken.None));
        }
        catch (Exception ex)
        {
            Log.Warn($"Erişim kontrolü başarısız: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }
}
