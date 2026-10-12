using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace GoodbyeDPI.Gui.Core;

/// <summary>
/// Yönetici yetkisi ve tek kopya kontrolü.
///
/// Akış:
///  1. Yetkisiz açılış + çalışan kopya var  -> o kopyanın penceresini göster, çık.
///  2. Yetkisiz açılış + başlatıcı görev var -> görevi çalıştır (UAC yok), çık.
///  3. Yetkisiz açılış, görev yok (ilk açılış) -> "runas" ile bir kez UAC iste, çık.
///  4. Yönetici kopya: başlatıcı görevi kaydeder; sonraki açılışlar 2. adımdan geçer.
/// </summary>
public static class Elevation
{
    private const string ShowEventName = @"Local\GoodbyeDPI-Turkey-GUI-Show";

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Çalışan bir kopya varsa onun penceresini öne getirmesini ister.</summary>
    public static bool SignalExistingInstance()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ShowEventName, out var handle))
                return false;
            using (handle)
                handle.Set();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Var ama erişemiyoruz: yine de çalışan bir kopya var demektir
            return true;
        }
    }

    /// <summary>
    /// Yönetici kopya için "pencereyi göster" olayını oluşturur. Yetkisiz kopyaların da Set()
    /// edebilmesi için olaya mevcut kullanıcıya açık bir ACL verilir.
    /// Başka bir yönetici kopya zaten çalışıyorsa null döner.
    /// </summary>
    public static EventWaitHandle? CreateShowEvent()
    {
        var security = new EventWaitHandleSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new EventWaitHandleAccessRule(user,
            EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
        security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            EventWaitHandleRights.FullControl, AccessControlType.Allow));

        var handle = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ShowEventName, out var createdNew, security);
        if (createdNew)
            return handle;
        handle.Set();
        handle.Dispose();
        return null;
    }

    /// <summary>UAC ile kendini yönetici olarak yeniden başlatır. Kullanıcı reddederse false.</summary>
    public static bool RelaunchElevated(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false;
        }
    }
}
