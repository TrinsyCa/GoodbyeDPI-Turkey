using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GoodbyeDPI.Gui.Controls;

public static class UiHelpers
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Windows 11: koyu pencere çerçevesi ve yuvarlak köşeler (WindowChrome ile kendi başlığımızı çizerken).</summary>
    public static void ApplyDarkRoundedFrame(Window window)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        int dark = 1, round = 2;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));  // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE = yuvarlak
    }

    public static void Animate(System.Windows.Media.Animation.IAnimatable target, DependencyProperty property, double to, int ms,
        System.Windows.Media.Animation.IEasingFunction? ease = null, double? from = null)
    {
        var animation = new System.Windows.Media.Animation.DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(ms)))
        {
            EasingFunction = ease ?? new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        if (from is { } f)
            animation.From = f;
        target.BeginAnimation(property, animation);
    }

    /// <summary>1536 → "1.5 KB", saniye başı değerler için "/s" eklenir.</summary>
    public static string FormatBytes(double bytes, bool perSecond = false)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        while (bytes >= 1024 && i < units.Length - 1)
        {
            bytes /= 1024;
            i++;
        }
        var text = i == 0 ? $"{bytes:0} {units[i]}" : $"{bytes:0.0} {units[i]}";
        return perSecond ? text + "/s" : text;
    }

    /// <summary>"2 sa 14 dk", "5 dk 3 sn" gibi.</summary>
    public static string FormatDuration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} g {t.Hours} sa"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours} sa {t.Minutes} dk"
        : t.TotalMinutes >= 1 ? $"{t.Minutes} dk {t.Seconds} sn"
        : $"{t.Seconds} sn";

    /// <summary>
    /// Bir görsel ağacı PNG olarak kaydeder (README görselleri). Pencere arka planı kökün parçası
    /// olmadığı için önce o çizilir; 1.5x ölçek yüksek DPI ekranlarda da net görünür.
    /// </summary>
    public static void SaveSnapshot(FrameworkElement root, Brush background, string path, double scale = 1.5)
    {
        var size = new Size(root.ActualWidth, root.ActualHeight);
        var bitmap = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);

        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
            dc.DrawRectangle(background, null, new Rect(size));
        bitmap.Render(backdrop);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
