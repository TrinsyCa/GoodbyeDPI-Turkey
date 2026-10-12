using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GoodbyeDPI.Gui.Controls;

/// <summary>
/// Fare tekerleğiyle yumuşak kaydırma: ScrollViewer.VerticalOffset salt okunur olduğu için
/// canlandırılabilen gizli bir bağlı özellik üzerinden ScrollToVerticalOffset çağrılır.
/// Art arda tekerlek hareketleri hedefi biriktirir; animasyon kesilmeden yeni hedefe akar.
/// </summary>
public static class SmoothScroll
{
    private static readonly Duration ScrollDuration = new(TimeSpan.FromMilliseconds(420));
    private const double WheelStep = 1.1; // Bir tekerlek çentiği (120) ≈ 132 piksel

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static readonly DependencyProperty AnimatedOffsetProperty = DependencyProperty.RegisterAttached(
        "AnimatedOffset", typeof(double), typeof(SmoothScroll),
        new PropertyMetadata(0.0, (d, e) => ((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));

    private static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target", typeof(double?), typeof(SmoothScroll), new PropertyMetadata(null));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer)
            return;
        viewer.PreviewMouseWheel -= OnWheel;
        if ((bool)e.NewValue)
            viewer.PreviewMouseWheel += OnWheel;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var viewer = (ScrollViewer)sender;

        // PreviewMouseWheel dıştan içe ilerler. Fare iç içe kaydırılabilir bir alandaysa
        // (ör. sayfa içindeki log kutusu) olayı ona bırak.
        if (InnerViewer(e.OriginalSource as DependencyObject, viewer) is { } inner && CanScroll(inner, e.Delta))
            return;
        if (viewer.ScrollableHeight <= 0)
            return;

        e.Handled = true;
        var from = viewer.VerticalOffset;
        var start = (double?)viewer.GetValue(TargetProperty) ?? from;
        var target = Math.Clamp(start - e.Delta * WheelStep, 0, viewer.ScrollableHeight);
        viewer.SetValue(TargetProperty, target);

        var animation = new DoubleAnimation(from, target, ScrollDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) =>
        {
            // Animasyon bitince hedefi bırak: sonraki tekerlek gerçek konumdan başlasın
            if ((double?)viewer.GetValue(TargetProperty) == target)
                viewer.ClearValue(TargetProperty);
        };
        viewer.BeginAnimation(AnimatedOffsetProperty, animation);
    }

    private static ScrollViewer? InnerViewer(DependencyObject? source, ScrollViewer outer)
    {
        for (var d = source; d is not null && d != outer; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ScrollViewer sv && GetIsEnabled(sv))
                return sv;
        return null;
    }

    private static bool CanScroll(ScrollViewer viewer, int delta) =>
        delta > 0 ? viewer.VerticalOffset > 0 : viewer.VerticalOffset < viewer.ScrollableHeight;
}
