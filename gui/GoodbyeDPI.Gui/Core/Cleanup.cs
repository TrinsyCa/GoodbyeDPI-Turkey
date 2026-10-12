using System.Diagnostics;
using System.IO;

namespace GoodbyeDPI.Gui.Core;

/// <summary>
/// Kaldırma sırasında Setup tarafından "--cleanup" ile (SYSTEM olarak) çağrılır. Uygulamanın
/// sisteme bıraktığı ve dosyalar silinince sahipsiz kalacak şeyleri temizler.
/// </summary>
public static class Cleanup
{
    public static void Run()
    {
        Log.Info("Kaldırma temizliği başladı.");

        // Dosyalar silinebilsin diye çalışan arayüzü kapat
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Environment.ProcessPath!)))
        {
            using (p)
            {
                if (p.Id == Environment.ProcessId)
                    continue;
                try { p.Kill(); p.WaitForExit(5000); }
                catch (Exception ex) { Log.Warn($"Arayüz kapatılamadı (PID {p.Id}): {ex.Message}"); }
            }
        }
        TestRunner.KillOwnedProcesses();

        foreach (var task in new[] { ScheduledTasks.LauncherTask, ScheduledTasks.AutostartTask })
        {
            try
            {
                if (ScheduledTasks.Exists(task))
                    ScheduledTasks.Delete(task);
            }
            catch (Exception ex)
            {
                Log.Warn($"{task} silinemedi: {ex.Message}");
            }
        }

        // Servis bu kurulumla gelen goodbyedpi.exe'yi çalıştırıyorsa, dosya silinince bozuk kalır.
        // Kullanıcının kendi klasöründeki (ör. D:\Goodbyedpi) bir kurulum ise dokunma.
        var (exe, _) = DpiService.ReadInstalledCommand();
        if (exe is not null && Path.GetFullPath(exe).StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
        {
            try { DpiService.Remove(); }
            catch (Exception ex) { Log.Warn($"Servis kaldırılamadı: {ex.Message}"); }
        }

        Log.Info("Kaldırma temizliği bitti.");
    }
}
