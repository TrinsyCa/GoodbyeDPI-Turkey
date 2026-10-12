using System.IO;
using System.Text.Json;

namespace GoodbyeDPI.Gui.Core;

/// <summary>%ProgramData%\GoodbyeDPI-Turkey-GUI\settings.json içinde saklanan kullanıcı ayarları.</summary>
public sealed class AppSettings
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GoodbyeDPI-Turkey-GUI");

    private static string FilePath => Path.Combine(DataDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>goodbyedpi.exe'nin bulunduğu release klasörü (içinde x86_64\ ve .cmd dosyaları olan).</summary>
    public string? GoodbyeDpiFolder { get; set; }

    /// <summary>VPN bağlanınca GoodbyeDPI servisini durdur, VPN kapanınca geri başlat.</summary>
    public bool AutoPauseOnVpn { get; set; } = true;

    /// <summary>
    /// Servisi VPN yüzünden biz durdurduk mu? Sadece bizim durdurduğumuzu geri açarız; kullanıcı
    /// elle kapattıysa VPN kapanınca açılmaz. Uygulama bu arada kapanıp açılırsa diye kalıcı.
    /// </summary>
    public bool PausedByVpn { get; set; }

    /// <summary>Windows oturumu açılınca uygulamayı tepside başlat.</summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>Pencere kapatılınca uygulamayı kapatma, tepsiye küçült.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>
    /// Yöntem testinde kullanılan siteler. Türkiye'de erişim engeli olan siteler seçilmeli;
    /// engelsiz bir site test sonucunu bir şey söylemez hale getirir.
    /// </summary>
    public List<string> TestUrls { get; set; } =
    [
        "https://discord.com/",
        "https://cdn.discordapp.com/",
        "https://www.wattpad.com/",
    ];

    /// <summary>Son test sonuçları (anahtar: yöntem argümanları). Uygulama yeniden açılınca test baştan yapılmasın.</summary>
    public Dictionary<string, PresetTestResult> LastResults { get; set; } = [];

    /// <summary>Son testin yapıldığı siteler; siteler değişince eski sonuçlar geçersiz sayılır.</summary>
    public List<string> LastResultUrls { get; set; } = [];

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Warn($"Ayarlar okunamadı, varsayılanlar kullanılıyor: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error($"Ayarlar kaydedilemedi: {ex.Message}");
        }
    }
}
