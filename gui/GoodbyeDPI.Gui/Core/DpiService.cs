using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using Microsoft.Win32;

namespace GoodbyeDPI.Gui.Core;

public enum DpiServiceState
{
    NotInstalled,
    Stopped,
    Starting,
    Running,
    Stopping,
}

/// <summary>
/// "GoodbyeDPI" Windows servisini yönetir. Repodaki service_install_*.cmd ve service_remove.cmd
/// dosyalarının yaptığını yapar; servis olduğu için goodbyedpi arka planda, konsol penceresi
/// açık kalmadan ve yeniden başlatmadan sonra da çalışır.
/// </summary>
public static class DpiService
{
    public const string ServiceName = "GoodbyeDPI";
    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\" + ServiceName;

    public static string ArchFolder => Environment.Is64BitOperatingSystem ? "x86_64" : "x86";

    public static string ExePath(string folder) => Path.Combine(folder, ArchFolder, "goodbyedpi.exe");

    public static bool IsValidFolder(string? folder) => !string.IsNullOrEmpty(folder) && File.Exists(ExePath(folder));

    /// <summary>
    /// goodbyedpi release klasörünü bulur: ayarlardaki klasör, uygulamanın yanı / bir üstü,
    /// ya da kurulu servisin işaret ettiği klasör (kullanıcının mevcut kurulumu).
    /// </summary>
    public static string? LocateFolder(string? configured)
    {
        var candidates = new List<string?> { configured, AppContext.BaseDirectory, Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('\\')) };
        var (exe, _) = ReadInstalledCommand();
        if (exe is not null)
            candidates.Add(Path.GetDirectoryName(Path.GetDirectoryName(exe))); // ...\x86_64\goodbyedpi.exe -> ...
        return candidates.FirstOrDefault(IsValidFolder);
    }

    public static DpiServiceState GetState()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Running => DpiServiceState.Running,
                ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => DpiServiceState.Starting,
                ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => DpiServiceState.Stopping,
                _ => DpiServiceState.Stopped,
            };
        }
        catch (InvalidOperationException)
        {
            return DpiServiceState.NotInstalled;
        }
    }

    /// <summary>Kurulu servisin exe yolu ve argümanları (ImagePath kaydından).</summary>
    public static (string? Exe, string? Arguments) ReadInstalledCommand()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServiceKey);
        if (key?.GetValue("ImagePath") is not string imagePath)
            return (null, null);

        imagePath = imagePath.Trim();
        if (imagePath.StartsWith('"'))
        {
            var end = imagePath.IndexOf('"', 1);
            if (end > 0)
                return (imagePath[1..end], imagePath[(end + 1)..].Trim());
        }
        var space = imagePath.IndexOf(' ');
        return space < 0 ? (imagePath, "") : (imagePath[..space], imagePath[(space + 1)..].Trim());
    }

    /// <summary>Seçilen yöntemi servis olarak kurar (varsa yapılandırmasını günceller) ve başlatır.</summary>
    public static void Install(string folder, Preset preset)
    {
        var exe = ExePath(folder);
        if (!File.Exists(exe))
            throw new FileNotFoundException("goodbyedpi.exe bulunamadı", exe);

        Stop();
        // binPath= "\"C:\...\goodbyedpi.exe\" <args>"  (repodaki .cmd dosyalarıyla aynı biçim)
        var binPath = $"binPath= \"\\\"{exe}\\\" {preset.Arguments}\" start= auto";
        if (GetState() == DpiServiceState.NotInstalled)
            Sc($"create \"{ServiceName}\" {binPath}");
        else
            // sc delete + create yerine config: silme "marked for deletion" durumunda takılabiliyor
            Sc($"config \"{ServiceName}\" {binPath}");

        Sc($"description \"{ServiceName}\" \"Turkiye icin DNS zorlamasini kaldirir. {preset.Name}\"");
        Log.Info($"Servis kuruldu: {preset.Name} ({preset.Arguments})");
        Start();
    }

    public static void Start()
    {
        using var sc = new ServiceController(ServiceName);
        if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
            return;
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
        Log.Info("GoodbyeDPI servisi başlatıldı.");
    }

    public static void Stop()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status is ServiceControllerStatus.Stopped)
                return;
            if (sc.Status is not ServiceControllerStatus.StopPending)
                sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            Log.Info("GoodbyeDPI servisi durduruldu.");
        }
        catch (InvalidOperationException)
        {
            // Kurulu değil
        }
    }

    /// <summary>service_remove.cmd ile aynı: servisi ve WinDivert sürücülerini kaldırır.</summary>
    public static void Remove()
    {
        Stop();
        Sc($"delete \"{ServiceName}\"", throwOnError: false);
        foreach (var driver in new[] { "WinDivert", "WinDivert14" })
        {
            Sc($"stop \"{driver}\"", throwOnError: false);
            Sc($"delete \"{driver}\"", throwOnError: false);
        }
        Log.Info("GoodbyeDPI servisi ve WinDivert sürücüleri kaldırıldı.");
    }

    /// <summary>turkey_dnsredir*.cmd ile elle başlatılmış (servis dışı) goodbyedpi.exe işlemleri.</summary>
    public static List<Process> FindManualProcesses()
    {
        var servicePid = GetServiceProcessId();
        return Process.GetProcessesByName("goodbyedpi")
            .Where(p => p.Id != servicePid && !TestRunner.IsOwnedTestProcess(p.Id))
            .ToList();
    }

    private static int? GetServiceProcessId()
    {
        // sc queryex çıktısında PID satırı
        var output = Sc($"queryex \"{ServiceName}\"", throwOnError: false);
        var line = output.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("PID", StringComparison.OrdinalIgnoreCase));
        return line is not null && int.TryParse(line.Split(':').Last().Trim(), out var pid) && pid > 0 ? pid : null;
    }

    private static string Sc(string arguments, bool throwOnError = true)
    {
        var psi = new ProcessStartInfo("sc.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (throwOnError && p.ExitCode != 0)
            throw new InvalidOperationException($"sc {arguments.Split(' ')[0]} başarısız ({p.ExitCode}): {output.Trim()}");
        return output;
    }
}
