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
}
