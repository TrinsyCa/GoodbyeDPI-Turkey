using System.IO;

namespace GoodbyeDPI.Gui.Core;

/// <summary>Arayüzdeki log paneli ve ProgramData altındaki log dosyası için basit kayıtçı.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static event Action<string>? LineAdded;

    public static string FilePath => Path.Combine(AppSettings.DataDirectory, "gui.log");

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {level}  {message}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.DataDirectory);
                // Dosya sınırsız büyümesin
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch
            {
                // Log yazılamaması uygulamayı durdurmamalı
            }
        }
        LineAdded?.Invoke(line);
    }

    /// <summary>
    /// Belirtilen günden eski satırları siler. Satırlar "yyyy-MM-dd HH:mm:ss" ile başlar; tarihi
    /// olmayan devam satırları (ör. hata yığını) bağlı oldukları satırla birlikte tutulur ya da silinir.
    /// </summary>
    public static void Prune(int keepDays)
    {
        var cutoff = DateTime.Now.AddDays(-keepDays);
        lock (Gate)
        {
            try
            {
                var old = FilePath + ".old";
                if (File.Exists(old) && File.GetLastWriteTime(old) < cutoff)
                    File.Delete(old);
                if (!File.Exists(FilePath))
                    return;

                var lines = File.ReadAllLines(FilePath);
                var kept = new List<string>(lines.Length);
                var keep = true;
                foreach (var line in lines)
                {
                    if (line.Length >= 19 && DateTime.TryParseExact(line[..19], "yyyy-MM-dd HH:mm:ss",
                            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at))
                        keep = at >= cutoff;
                    if (keep)
                        kept.Add(line);
                }
                if (kept.Count != lines.Length)
                    File.WriteAllLines(FilePath, kept);
            }
            catch
            {
                // Temizlik başarısız olursa bir sonraki denemede tekrar denenir
            }
        }
    }

    /// <summary>Tüm kayıtları siler (Kayıtlar sayfasındaki "Temizle").</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            File.Delete(FilePath + ".old");
            File.Delete(FilePath);
        }
    }
}
