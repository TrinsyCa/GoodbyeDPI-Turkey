using System.ComponentModel;
using System.Runtime.CompilerServices;
using GoodbyeDPI.Gui.Core;

namespace GoodbyeDPI.Gui.ViewModels;

public enum CompatKind { Untested, Testing, Full, Partial, None }

public sealed record SiteView(string Host, bool Ok, string Detail)
{
    public string Glyph => Ok ? "" : ""; // Onay / çarpı (Segoe Fluent Icons)
}

/// <summary>Yöntemler listesindeki bir satır.</summary>
public sealed class PresetRow(Preset preset) : INotifyPropertyChanged
{
    private PresetTestResult? _result;
    private bool _isTesting;
    private bool _isActive;
    private bool _isRecommended;

    public Preset Preset { get; } = preset;
    public string Name => Preset.Name;
    public string Arguments => Preset.Arguments;
    public string DnsNote => Preset.RedirectsDns
        ? "DNS'i kendisi yönlendirir"
        : "Windows DNS ayarını değiştirmeniz gerekir";

    public PresetTestResult? Result
    {
        get => _result;
        set
        {
            _result = value;
            OnChanged();
            OnChanged(nameof(Kind));
            OnChanged(nameof(StatusText));
            OnChanged(nameof(DetailText));
            OnChanged(nameof(Sites));
            OnChanged(nameof(HasResult));
            OnChanged(nameof(StartError));
        }
    }

    public bool HasResult => Result is not null;
    public string? StartError => Result?.StartError;

    /// <summary>Kartın altında gösterilen site bazında sonuç çipleri.</summary>
    public IReadOnlyList<SiteView> Sites => Result?.Sites
        .Select(s => new SiteView(new Uri(s.Url).Host, s.Ok, s.Ok ? $"{s.Milliseconds} ms" : s.Error ?? "Hata"))
        .ToList() ?? [];

    public bool IsTesting
    {
        get => _isTesting;
        set { _isTesting = value; OnChanged(); OnChanged(nameof(Kind)); OnChanged(nameof(StatusText)); }
    }

    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; OnChanged(); }
    }

    public bool IsRecommended
    {
        get => _isRecommended;
        set { _isRecommended = value; OnChanged(); }
    }

    public CompatKind Kind => IsTesting ? CompatKind.Testing
        : Result is null ? CompatKind.Untested
        : Result.AllPassed ? CompatKind.Full
        : Result.NonePassed ? CompatKind.None
        : CompatKind.Partial;

    public string StatusText => Kind switch
    {
        CompatKind.Testing => "Test ediliyor…",
        CompatKind.Untested => "Test edilmedi",
        CompatKind.Full => $"Tam uyumlu · {Result!.AverageMs:0} ms",
        CompatKind.Partial => $"Kısmen · {Result!.Passed}/{Result.Sites.Count} site",
        _ => "Çalışmıyor",
    };

    /// <summary>Her sitenin sonucu; satırın üzerine gelince ipucu olarak gösterilir.</summary>
    public string DetailText
    {
        get
        {
            if (Result is null)
                return "Henüz test edilmedi.";
            if (Result.StartError is not null)
                return Result.StartError;
            return string.Join(Environment.NewLine, Result.Sites.Select(s =>
                $"{(s.Ok ? "✓" : "✗")} {new Uri(s.Url).Host}  {(s.Ok ? $"{s.Milliseconds} ms" : s.Error)}"));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
