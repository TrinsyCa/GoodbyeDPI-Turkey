namespace GoodbyeDPI.Gui.Core;

public static class PresetRanker
{
    /// <summary>
    /// Test sonuçlarından kullanıcıya "Önerilen" olarak gösterilecek yöntemi seçer.
    /// Sonucu olmayan (test edilmemiş) yöntemler listede null sonuçla gelir.
    ///
    /// Tartılması gerekenler:
    ///  - Tüm siteleri açan yöntem, birkaçını daha hızlı açandan her zaman iyi mi?
    ///  - DNS'i kendisi yönlendiren yöntemler (preset.RedirectsDns) Windows DNS ayarı gerektirmez;
    ///    ama test DNS ayarı değiştirilmiş bir ağda yapıldıysa DNS'siz yöntem de geçebilir.
    ///  - Ortalama süre (AverageMs) tek ölçümde gürültülü; küçük farklar anlamsız olabilir.
    /// </summary>
    public static Preset? PickRecommended(IReadOnlyList<(Preset Preset, PresetTestResult? Result)> results)
    {
        // Varsayılan kural: tüm siteleri açanlar arasından DNS'i kendisi yönlendireni tercih et,
        // sonra en hızlıyı seç. Tam uyumlu yoksa öneri yapma.
        return results
            .Where(r => r.Result is { AllPassed: true })
            .OrderByDescending(r => r.Preset.RedirectsDns)
            .ThenBy(r => r.Result!.AverageMs ?? double.MaxValue)
            .Select(r => r.Preset)
            .FirstOrDefault();
    }
}
