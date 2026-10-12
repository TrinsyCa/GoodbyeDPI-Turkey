namespace GoodbyeDPI.Gui.Core;

/// <summary>Test akışındaki olaylar; arayüzler (Yöntemler sayfası, ilk kurulum ekranı) bunları canlı çizer.</summary>
public abstract record TestEvent;
public sealed record BaselineStarted : TestEvent;
public sealed record BaselineFinished(PresetTestResult Result) : TestEvent
{
    public int Blocked => Result.Sites.Count(s => !s.Ok);
}
public sealed record PresetStarted(Preset Preset, int Index, int Total) : TestEvent;
public sealed record SiteFinished(Preset Preset, SiteResult Site) : TestEvent;
public sealed record PresetFinished(Preset Preset, int Index, int Total, PresetTestResult Result) : TestEvent;
public sealed record ExternalRestartDetected : TestEvent;

public sealed record TestRunSummary(PresetTestResult Baseline, IReadOnlyDictionary<Preset, PresetTestResult> Results, int ExternalRestarts);

/// <summary>
/// Tüm yöntemleri sırayla test eder: önce goodbyedpi olmadan engel kontrolü, sonra her yöntem.
/// Servisi durdurmak/geri açmak çağıranın işidir (VPN duraklatma kararı arayüze bağlı).
/// </summary>
public static class TestOrchestrator
{
    /// <param name="report">Olaylar çağıranın senkronizasyon bağlamında (arayüz iş parçacığında) gelir.</param>
    public static async Task<TestRunSummary> RunAsync(string folder, IReadOnlyList<Preset> presets, IReadOnlyList<string> urls,
        Action<TestEvent> report, CancellationToken ct)
    {
        var restarts = 0;

        // Servis test sırasında dışarıdan açılırsa (ör. eski VPN script'i) iki goodbyedpi aynı anda
        // çalışır ve sonuçlar o yöntemi değil servisi ölçer. Her adımda kontrol edip yeniden durdur.
        async Task EnsureServiceStoppedAsync()
        {
            if (await Task.Run(DpiService.GetState, ct) is DpiServiceState.Running or DpiServiceState.Starting)
            {
                restarts++;
                Log.Warn("Test sırasında GoodbyeDPI servisi dışarıdan başlatıldı, tekrar durduruluyor.");
                report(new ExternalRestartDetected());
                await Task.Run(DpiService.Stop, ct);
            }
        }

        await EnsureServiceStoppedAsync();
        report(new BaselineStarted());
        var baseline = await TestRunner.RunBaselineAsync(urls, ct);
        var baselineEvent = new BaselineFinished(baseline);
        Log.Info($"Engelsiz kontrol: {baselineEvent.Blocked}/{baseline.Sites.Count} site engelli.");
        report(baselineEvent);

        var results = new Dictionary<Preset, PresetTestResult>();
        for (var i = 0; i < presets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var preset = presets[i];
            await EnsureServiceStoppedAsync();
            report(new PresetStarted(preset, i, presets.Count));
            var result = await TestRunner.RunPresetAsync(folder, preset, urls, ct, site => report(new SiteFinished(preset, site)));
            results[preset] = result;
            report(new PresetFinished(preset, i, presets.Count, result));
        }
        await EnsureServiceStoppedAsync();
        return new TestRunSummary(baseline, results, restarts);
    }
}
