using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;

namespace Rimlight.SettingsUI;

public sealed class RelayCommand(Action<object?> run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run(parameter);
}

/// <summary>One editable color (primary or secondary): HSV for the wheel + brightness slider, hex, presets.</summary>
public sealed class ColorEditViewModel : INotifyPropertyChanged
{
    public static IReadOnlyList<string> Presets { get; } =
    [
        "#FF2E88", "#7B3CFF", "#FF8A3D", "#E0401E", "#FACC15",
        "#22D3EE", "#3B82F6", "#10B981", "#F472B6", "#FFFFFF",
    ];

    private readonly Func<string> _get;
    private readonly Action<string> _set;
    private double _h, _s, _v;
    private bool _isOpen;
    private bool _syncing;

    public ColorEditViewModel(string label, Func<string> get, Action<string> set)
    {
        Label = label;
        _get = get;
        _set = set;
        PickPresetCommand = new RelayCommand(p => { if (p is string hex) Hex = hex; });
        ToggleCommand = new RelayCommand(_ => IsOpen = !IsOpen);
        LoadFromSettings();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label { get; }
    public ICommand PickPresetCommand { get; }
    public ICommand ToggleCommand { get; }

    public bool IsOpen { get => _isOpen; set { _isOpen = value; Raise(); } }

    public double Hue { get => _h; set { _h = value; FromHsv(); } }
    public double Saturation { get => _s; set { _s = value; FromHsv(); } }
    /// <summary>0..100 brightness slider.</summary>
    public double Value { get => _v * 100; set { _v = Math.Clamp(value / 100, 0, 1); FromHsv(); } }

    public Color Color => ColorMath.TryParse(_get(), out var c) ? c : Colors.White;
    public Brush Brush => new SolidColorBrush(Color);

    public string Hex
    {
        get => _get();
        set
        {
            if (!ColorMath.TryParse(value, out var c)) return;
            _set(ColorMath.ToHex(c));
            LoadFromSettings();
        }
    }

    public void LoadFromSettings()
    {
        _syncing = true;
        (_h, _s, _v) = ColorMath.ToHsv(Color);
        _syncing = false;
        Raise(null);
    }

    private void FromHsv()
    {
        if (_syncing) return;
        _set(ColorMath.ToHex(ColorMath.FromHsv(_h, _s, _v)));
        Raise(nameof(Hue)); Raise(nameof(Saturation)); Raise(nameof(Value));
        Raise(nameof(Color)); Raise(nameof(Brush)); Raise(nameof(Hex));
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
