using System.Net.NetworkInformation;
using System.ServiceProcess;

namespace GoodbyeDPI.Gui.Core;

/// <summary>
/// VPN bağlantısını izler. GoodbyeDPI tüm ağ kartlarındaki trafiği değiştirdiği için VPN tünelinin
/// içine de sahte paket/DNS yönlendirmesi sokar ve bağlantıyı bozar (ERR_CONNECTION_RESET,
/// ERR_SSL_PROTOCOL_ERROR). Bu yüzden VPN açıkken servis durdurulur.
/// </summary>
public sealed class VpnWatcher : IDisposable
{
    // Adaptör açıklamasında/isminde aranan ifadeler (Hyper-V/WSL sanal kartlarını yakalamayacak şekilde)
    private static readonly string[] AdapterKeywords =
    [
        "ProtonVPN", "Proton VPN", "WireGuard", "Wintun", "TAP-Windows", "OpenVPN",
        "NordLynx", "Mullvad", "Windscribe", "ExpressVPN", "Surfshark", "Cloudflare WARP",
    ];

    // Tünel adaptörü oluşmadan önce başlayan servisler: GoodbyeDPI'ı el sıkışmadan önce durdurmak için
    private static readonly string[] VpnServices = ["ProtonVPN WireGuard"];

    private readonly System.Threading.Timer _timer;
    private int _busy;

    public bool IsVpnActive { get; private set; }
    public string? ActiveAdapter { get; private set; }

    /// <summary>VPN durumu değiştiğinde (true = bağlandı). İş parçacığı havuzundan tetiklenir.</summary>
    public event Action<bool>? VpnStateChanged;

    public VpnWatcher()
    {
        _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;
        try
        {
            var adapter = FindVpnAdapter();
            var active = adapter is not null || AnyVpnServiceRunning();
            ActiveAdapter = adapter ?? (active ? VpnServices[0] : null);
            if (active != IsVpnActive)
            {
                IsVpnActive = active;
                Log.Info(active ? $"VPN algılandı ({ActiveAdapter})." : "VPN bağlantısı kapandı.");
                VpnStateChanged?.Invoke(active);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"VPN kontrolü başarısız: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>İzleyici kurmadan anlık kontrol (ilk kurulum ekranı için).</summary>
    public static bool DetectNow() => FindVpnAdapter() is not null || AnyVpnServiceRunning();

    private static string? FindVpnAdapter() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .FirstOrDefault(n => AdapterKeywords.Any(k =>
                n.Description.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                n.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
            ?.Description;

    private static bool AnyVpnServiceRunning()
    {
        foreach (var name in VpnServices)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
                    return true;
            }
            catch (InvalidOperationException)
            {
                // Bu VPN kurulu değil
            }
        }
        return false;
    }

    public void Dispose() => _timer.Dispose();
}
