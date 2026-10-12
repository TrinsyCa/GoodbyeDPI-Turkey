using System.Diagnostics;
using System.Management;

namespace GoodbyeDPI.Gui.Core;

/// <summary>Bulunan eski VPN script'i kalıntıları: zamanlanmış görevler ve çalışan PowerShell işlemleri.</summary>
public sealed record LegacySwitch(IReadOnlyList<string> TaskPaths, IReadOnlyList<int> ProcessIds)
{
    public bool Found => TaskPaths.Count > 0 || ProcessIds.Count > 0;
}

/// <summary>
/// GoodbyeDPI servisini bu uygulamanın dışında yöneten şeyleri bulur. Bilinen tek örnek, uygulamadan
/// önce elle kurulan vpn-goodbyedpi-switch.ps1: VPN kapalıyken servisi her 2 saniyede bir geri açtığı
/// için kullanıcının "kapat" demesini ve testleri boşa çıkarır.
/// </summary>
public static class ConflictDetector
{
    private const string ScriptMarker = "vpn-goodbyedpi-switch";

    /// <summary>
    /// Görev adına güvenmez: script farklı bir adla görev yapılmış ya da doğrudan bir PowerShell
    /// penceresinden çalıştırılmış olabilir. Görevlerin XML'inde ve işlemlerin komut satırında arar.
    /// Yönetici yetkisi gerekir (SYSTEM görevleri ve işlemleri başka türlü görünmez).
    /// </summary>
    public static LegacySwitch FindLegacyVpnSwitch()
    {
        var tasks = new List<string>();
        try
        {
            dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            scheduler.Connect();
            CollectTasks(scheduler.GetFolder("\\"), tasks);
        }
        catch (Exception ex)
        {
            Log.Warn($"Zamanlanmış görevler taranamadı: {ex.Message}");
        }

        var pids = new List<int>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'powershell.exe' OR Name = 'pwsh.exe'");
            foreach (var mo in searcher.Get().Cast<ManagementObject>())
            {
                using (mo)
                {
                    if ((mo["CommandLine"] as string)?.Contains(ScriptMarker, StringComparison.OrdinalIgnoreCase) == true)
                        pids.Add(Convert.ToInt32(mo["ProcessId"]));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"PowerShell işlemleri taranamadı: {ex.Message}");
        }

        return new LegacySwitch(tasks, pids);
    }

    private static void CollectTasks(dynamic folder, List<string> found)
    {
        foreach (dynamic task in folder.GetTasks(1)) // TASK_ENUM_HIDDEN
        {
            string xml = task.Xml;
            string name = task.Name;
            if (xml.Contains(ScriptMarker, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, ScheduledTasks.LegacyVpnSwitchTask, StringComparison.OrdinalIgnoreCase))
                found.Add((string)task.Path);
        }
        foreach (dynamic sub in folder.GetFolders(0))
        {
            string path = sub.Path;
            if (!path.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase)) // Windows'un yüzlerce görevi; script orada olmaz
                CollectTasks(sub, found);
        }
    }

    /// <summary>Önce görevleri siler (yeniden başlatmasınlar diye), sonra çalışan işlemleri sonlandırır.</summary>
    public static void RemoveLegacyVpnSwitch(LegacySwitch legacy)
    {
        foreach (var path in legacy.TaskPaths)
            ScheduledTasks.Delete(path);

        foreach (var pid in legacy.ProcessIds)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill();
                Log.Info($"Eski VPN script'i durduruldu (PID {pid}).");
            }
            catch (ArgumentException)
            {
                // Görev silinirken zaten kapanmış
            }
        }
    }
}
