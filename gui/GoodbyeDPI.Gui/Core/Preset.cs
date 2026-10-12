using System.IO;
using System.Text.RegularExpressions;

namespace GoodbyeDPI.Gui.Core;

/// <summary>Bir GoodbyeDPI çalışma yöntemi: repodaki bir service_install_*.cmd dosyasının karşılığı.</summary>
public sealed record Preset(string Id, string Name, string Arguments, string Description, string? SourceFile)
{
    /// <summary>
    /// --dns-addr içermeyen yöntemler DNS'i kendileri yönlendirmez; ISS DNS'i zehirli olduğu için
    /// bunlar ancak Windows DNS ayarı değiştirilmişse çalışır (repodaki .cmd açıklamalarıyla aynı uyarı).
    /// </summary>
    public bool RedirectsDns => Arguments.Contains("--dns-addr", StringComparison.Ordinal);
}

/// <summary>
/// Yöntemleri repodaki/release klasöründeki service_install_*.cmd dosyalarından okur.
/// Böylece fork'a yeni bir .cmd eklendiğinde uygulama onu kendiliğinden listeler.
/// </summary>
public static partial class PresetLoader
{
    // sc create "GoodbyeDPI" binPath= "\"%CD%\%_arch%\goodbyedpi.exe\" -5 --set-ttl 5 ..." start= "auto"
    [GeneratedRegex(@"binPath=\s*""\\""[^""]*goodbyedpi\.exe\\""\s*(?<args>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex BinPathRegex();

    [GeneratedRegex(@"sc\s+description\s+""GoodbyeDPI""\s+""(?<desc>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex DescriptionRegex();

    [GeneratedRegex(@"alternative(?<n>\d*)", RegexOptions.IgnoreCase)]
    private static partial Regex AlternativeRegex();

    /// <summary>.cmd dosyaları bulunamazsa kullanılan, repodaki dosyaların birebir kopyası.</summary>
    private static readonly Preset[] BuiltIn =
    [
        new("default", "Varsayılan", "-5 --set-ttl 5 --dns-addr 77.88.8.8 --dns-port 1253 --dnsv6-addr 2a02:6b8::feed:0ff --dnsv6-port 1253", "Turkiye icin DNS zorlamasini kaldirir.", null),
        new("alt1", "Alternatif 1 (Superonline)", "--set-ttl 3", "Alternatif Metod", null),
        new("alt2", "Alternatif 2 (Superonline)", "-5", "Alternatif Metod 2", null),
        new("alt3", "Alternatif 3 (Superonline)", "--set-ttl 3 --dns-addr 77.88.8.8 --dns-port 1253 --dnsv6-addr 2a02:6b8::feed:0ff --dnsv6-port 1253", "Alternatif Metod 3", null),
        new("alt4", "Alternatif 4 (Superonline)", "-5 --dns-addr 77.88.8.8 --dns-port 1253 --dnsv6-addr 2a02:6b8::feed:0ff --dnsv6-port 1253", "Alternatif Metod 4", null),
        new("alt5", "Alternatif 5 (Superonline)", "-9 --dns-addr 77.88.8.8 --dns-port 1253 --dnsv6-addr 2a02:6b8::feed:0ff --dnsv6-port 1253", "Alternatif Metod 5", null),
        new("alt6", "Alternatif 6 (Superonline)", "-9", "Alternatif Metod 6", null),
    ];

    public static IReadOnlyList<Preset> Load(string? goodbyeDpiFolder)
    {
        var dir = FindScriptDirectory(goodbyeDpiFolder);
        if (dir is null)
        {
            Log.Warn("service_install_*.cmd dosyaları bulunamadı, yerleşik yöntem listesi kullanılıyor.");
            return BuiltIn;
        }

        var presets = new List<Preset>();
        foreach (var file in Directory.GetFiles(dir, "service_install_*.cmd"))
        {
            var text = File.ReadAllText(file);
            var bin = BinPathRegex().Match(text);
            if (!bin.Success)
            {
                Log.Warn($"{Path.GetFileName(file)} içinde binPath bulunamadı, atlandı.");
                continue;
            }
            var desc = DescriptionRegex().Match(text);
            var (id, name) = NameFromFile(Path.GetFileNameWithoutExtension(file));
            presets.Add(new Preset(id, name, bin.Groups["args"].Value.Trim(),
                desc.Success ? desc.Groups["desc"].Value : "", file));
        }

        if (presets.Count == 0)
            return BuiltIn;

        Log.Info($"{presets.Count} yöntem {dir} klasöründen yüklendi.");
        return presets.OrderBy(p => p.Id == "default" ? 0 : 1).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Release ZIP'inde .cmd'ler kök klasörde, repoda ise windows\ altında.</summary>
    private static string? FindScriptDirectory(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
            return null;
        foreach (var candidate in new[] { folder, Path.Combine(folder, "windows") })
        {
            if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "service_install_*.cmd").Length > 0)
                return candidate;
        }
        return null;
    }

    private static (string Id, string Name) NameFromFile(string fileName)
    {
        var alt = AlternativeRegex().Match(fileName);
        if (!alt.Success)
            return ("default", "Varsayılan");

        var n = alt.Groups["n"].Value;
        if (n.Length == 0)
            n = "1"; // "alternative_superonline" dosyası repoda 1. alternatif
        var isp = fileName.Contains("superonline", StringComparison.OrdinalIgnoreCase) ? " (Superonline)" : "";
        return ($"alt{n}", $"Alternatif {n}{isp}");
    }

    /// <summary>Kurulu servisin argümanlarından hangi yöntemin aktif olduğunu bulur.</summary>
    public static Preset? Match(IEnumerable<Preset> presets, string? serviceArguments)
    {
        if (serviceArguments is null)
            return null;
        var normalized = Normalize(serviceArguments);
        return presets.FirstOrDefault(p => Normalize(p.Arguments) == normalized);
    }

    private static string Normalize(string args) =>
        string.Join(' ', args.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
