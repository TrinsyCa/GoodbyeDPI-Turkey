using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;

namespace GoodbyeDPI.Gui.Core;

public sealed record SiteResult(string Url, bool Ok, int? Milliseconds, string? Error);

public sealed record PresetTestResult(IReadOnlyList<SiteResult> Sites, string? StartError = null)
{
    public int Passed => Sites.Count(s => s.Ok);
    public bool AllPassed => StartError is null && Sites.Count > 0 && Passed == Sites.Count;
    public bool NonePassed => Passed == 0;
    public double? AverageMs => Sites.Any(s => s.Ok) ? Sites.Where(s => s.Ok).Average(s => s.Milliseconds!.Value) : null;
}

/// <summary>
/// Bir yöntemi kurmadan dener: goodbyedpi.exe'yi gizli alt işlem olarak o yöntemin argümanlarıyla
/// başlatır, DNS önbelleğini temizler ve engelli sitelere gerçek HTTPS isteği atar.
/// WinDivert tüm sistem trafiğini yakaladığı için bu işlemin kendi istekleri de goodbyedpi'dan geçer.
/// </summary>
public static class TestRunner
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(2);
    private static readonly ConcurrentDictionary<int, byte> OwnedPids = new();

    public static bool IsOwnedTestProcess(int pid) => OwnedPids.ContainsKey(pid);

    /// <summary>Uygulama test sürerken kapanırsa çağrılır; async finally blokları çalışmayabilir.</summary>
    public static void KillOwnedProcesses()
    {
        foreach (var pid in OwnedPids.Keys)
        {
            try { using var p = Process.GetProcessById(pid); p.Kill(); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
        }
        OwnedPids.Clear();
    }

    /// <summary>goodbyedpi olmadan: siteler gerçekten engelli mi? (Engelsizse test anlamsızdır.)</summary>
    public static async Task<PresetTestResult> RunBaselineAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        FlushDnsCache();
        return new PresetTestResult(await ProbeAllAsync(urls, ct));
    }

    public static async Task<PresetTestResult> RunPresetAsync(string folder, Preset preset, IReadOnlyList<string> urls, CancellationToken ct)
    {
        var exe = DpiService.ExePath(folder);
        var output = new StringBuilder();
        var psi = new ProcessStartInfo(exe, preset.Arguments)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!, // WinDivert.dll / .sys burada
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        try
        {
            process.Start();
            OwnedPids[process.Id] = 0;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // WinDivert sürücüsünün yüklenip filtrenin açılması için bekle
            await Task.Delay(StartupDelay, ct);
            if (process.HasExited)
            {
                string tail;
                lock (output) tail = LastLines(output.ToString(), 3);
                return new PresetTestResult([], $"goodbyedpi başlatılamadı (çıkış kodu {process.ExitCode}). {tail}");
            }

            FlushDnsCache();
            return new PresetTestResult(await ProbeAllAsync(urls, ct));
        }
        finally
        {
            await KillAsync(process);
        }
    }

    private static async Task KillAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            // Hiç başlamamış ya da zaten kapanmış
        }
        finally
        {
            try { OwnedPids.TryRemove(process.Id, out _); } catch (InvalidOperationException) { }
        }
    }

    private static async Task<IReadOnlyList<SiteResult>> ProbeAllAsync(IReadOnlyList<string> urls, CancellationToken ct) =>
        await Task.WhenAll(urls.Select(u => ProbeAsync(u, ct)));

    private static async Task<SiteResult> ProbeAsync(string url, CancellationToken ct)
    {
        // Her denemede yeni bağlantı: havuzdaki eski bağlantı bir önceki yöntemin sonucunu taşımasın
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.Zero,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) GoodbyeDPI-Turkey-GUI");

        var sw = Stopwatch.StartNew();
        try
        {
            // Herhangi bir HTTP yanıtı (3xx, 4xx dahil) TLS el sıkışmasının geçtiğini gösterir; engel aşılmış demektir.
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            return new SiteResult(url, true, (int)sw.ElapsedMilliseconds, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SiteResult(url, false, null, "Zaman aşımı");
        }
        catch (HttpRequestException ex)
        {
            return new SiteResult(url, false, null, Describe(ex));
        }
    }

    private static string Describe(HttpRequestException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case AuthenticationException:
                    return "TLS hatası (DNS yönlendirmesi/engel sayfası olabilir)";
                case SocketException { SocketErrorCode: SocketError.ConnectionReset }:
                    return "Bağlantı sıfırlandı (DPI)";
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData }:
                    return "DNS çözülemedi";
                case SocketException se:
                    return $"Ağ hatası ({se.SocketErrorCode})";
            }
        }
        return ex.Message;
    }

    private static string LastLines(string text, int count) =>
        string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(count));

    [DllImport("dnsapi.dll")]
    private static extern bool DnsFlushResolverCache();

    /// <summary>Önceki zehirli/engelli DNS cevapları önbellekte kalırsa sonuç yanlış çıkar.</summary>
    public static void FlushDnsCache()
    {
        try { DnsFlushResolverCache(); }
        catch (Exception ex) { Log.Warn($"DNS önbelleği temizlenemedi: {ex.Message}"); }
    }
}
