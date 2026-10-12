using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace GoodbyeDPI.Gui.Core;

/// <summary>
/// "En yüksek yetkiyle çalış" ayarlı zamanlanmış görevler. Görev Zamanlayıcı bu görevleri
/// UAC sormadan yönetici olarak başlatır; görevi oluşturmak için ise bir kez yönetici olmak yeterli.
/// </summary>
public static class ScheduledTasks
{
    /// <summary>Tetikleyicisi yok; uygulama yetkisiz açıldığında bunu çalıştırıp yükseltilmiş kopyayı başlatır.</summary>
    public const string LauncherTask = "GoodbyeDPI-Turkey-GUI";

    /// <summary>Oturum açılınca uygulamayı tepside başlatır.</summary>
    public const string AutostartTask = "GoodbyeDPI-Turkey-GUI-Autostart";

    /// <summary>Daha önce elle kurduğumuz PowerShell VPN script'inin görevi. Uygulamayla çakışır.</summary>
    public const string LegacyVpnSwitchTask = "VPN-GoodbyeDPI-Switch";

    public static string ExePath => Environment.ProcessPath!;

    public static bool Exists(string name) => Schtasks($"/Query /TN \"{name}\"").ExitCode == 0;

    /// <summary>Görev var ve bu exe'yi çalıştırıyor mu? (Uygulama taşındıysa eski yolu başlatmasın.)</summary>
    public static bool PointsToCurrentExe(string name)
    {
        var (exitCode, xml) = Schtasks($"/Query /TN \"{name}\" /XML");
        return exitCode == 0 && xml.Contains(SecurityElement.Escape(ExePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Yetkisiz bir işlemden çağrılabilir: görevi başlatır.</summary>
    public static bool Run(string name) => Schtasks($"/Run /TN \"{name}\"").ExitCode == 0;

    public static void Delete(string name)
    {
        Schtasks($"/End /TN \"{name}\"");
        var result = Schtasks($"/Delete /TN \"{name}\" /F");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Görev silinemedi: {result.Output.Trim()}");
        Log.Info($"Zamanlanmış görev silindi: {name}");
    }

    /// <summary>Başlatıcı görevi geçerli exe yoluyla oluşturur/günceller. Yönetici gerektirir.</summary>
    public static void EnsureLauncher() => Register(LauncherTask, arguments: "", logonTrigger: false,
        "GoodbyeDPI Turkey GUI'yi UAC onayı sormadan yönetici olarak başlatır.");

    public static void SetAutostart(bool enabled)
    {
        if (enabled)
            Register(AutostartTask, arguments: "--tray", logonTrigger: true,
                "Oturum açılınca GoodbyeDPI Turkey GUI'yi tepside başlatır.");
        else if (Exists(AutostartTask))
            Delete(AutostartTask);
    }

    private static void Register(string name, string arguments, bool logonTrigger, string description)
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var trigger = logonTrigger
            ? $"<LogonTrigger><Enabled>true</Enabled><UserId>{sid}</UserId><Delay>PT5S</Delay></LogonTrigger>"
            : "";

        // schtasks'in komut satırı seçenekleri pil/süre sınırı ayarlarını açamıyor; XML bunlar için gerekli.
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>{SecurityElement.Escape(description)}</Description></RegistrationInfo>
              <Triggers>{trigger}</Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{sid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <Priority>5</Priority>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(ExePath)}</Command>
                  <Arguments>{SecurityElement.Escape(arguments)}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        var file = Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            var result = Schtasks($"/Create /TN \"{name}\" /XML \"{file}\" /F");
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Görev oluşturulamadı: {result.Output.Trim()}");
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static (int ExitCode, string Output) Schtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }
}
