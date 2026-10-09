using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace PlantHmi.Simulation;

/// <summary>Frozen brushes shared by the HMI view models.</summary>
public static class HmiBrushes
{
    public static readonly Brush Critical = Freeze("#F87171");
    public static readonly Brush Warning = Freeze("#FBBF24");
    public static readonly Brush Info = Freeze("#22D3EE");
    public static readonly Brush Good = Freeze("#34D399");
    public static readonly Brush Idle = Freeze("#3D4C63");

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Severity of a plant alarm.</summary>
public enum AlarmSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>Operating state of a process unit, drives lamp colour + pulsing.</summary>
public enum UnitState
{
    Stopped,
    Running,
    Warning,
    Alarm,
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>A single alarm entry rendered in the alarm list.</summary>
public sealed class AlarmRecord : ObservableObject
{
    private bool _acknowledged;
    private bool _active = true;
    private AlarmSeverity _severity;
    private double _peakValue;

    public required string Source { get; init; }
    public required string Message { get; init; }
    public required string Tag { get; init; }
    public DateTime RaisedAt { get; init; } = DateTime.Now;
    public string Unit { get; init; } = string.Empty;

    public AlarmSeverity Severity
    {
        get => _severity;
        set
        {
            if (Set(ref _severity, value))
            {
                Raise(nameof(SeverityText));
                Raise(nameof(SeverityColor));
            }
        }
    }

    public double LimitValue { get; init; }

    public double PeakValue
    {
        get => _peakValue;
        set
        {
            if (Set(ref _peakValue, value)) Raise(nameof(PeakText));
        }
    }

    public string LimitText => $"{LimitValue:0.0} {Unit}".Trim();
    public string PeakText => $"{PeakValue:0.0} {Unit}".Trim();

    public void RaisePeak() => Raise(nameof(PeakText));

    public bool Acknowledged
    {
        get => _acknowledged;
        set
        {
            if (Set(ref _acknowledged, value))
            {
                Raise(nameof(AckText));
                Raise(nameof(RowOpacity));
            }
        }
    }

    public bool Active
    {
        get => _active;
        set
        {
            if (Set(ref _active, value)) Raise(nameof(RowOpacity));
        }
    }

    public string AckText => Acknowledged ? "ACK" : "UNACK";
    public double RowOpacity => Acknowledged ? 0.55 : 1.0;

    public bool HasValues => !string.IsNullOrEmpty(Unit);

    public string TimeText => RaisedAt.ToString("HH:mm:ss");
    public string SeverityText => Severity.ToString().ToUpperInvariant();

    public Brush SeverityColor => Severity switch
    {
        AlarmSeverity.Critical => HmiBrushes.Critical,
        AlarmSeverity.Warning => HmiBrushes.Warning,
        _ => HmiBrushes.Info,
    };

    public string Detail => $"{Tag} · {Source}";
}

/// <summary>One process unit shown in the status-lamp panel and the pipeline scheme.</summary>
public sealed class UnitLamp : ObservableObject
{
    private UnitState _state = UnitState.Stopped;
    private string _valueText = "--";

    public required string Name { get; init; }
    public required string Tag { get; init; }

    public UnitState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                Raise(nameof(IsAlarm));
                Raise(nameof(IsWarning));
                Raise(nameof(LampColor));
                Raise(nameof(StateText));
            }
        }
    }

    public string ValueText
    {
        get => _valueText;
        set => Set(ref _valueText, value);
    }

    public bool IsAlarm => State == UnitState.Alarm;
    public bool IsWarning => State == UnitState.Warning;

    public Brush LampColor => State switch
    {
        UnitState.Alarm => HmiBrushes.Critical,
        UnitState.Warning => HmiBrushes.Warning,
        UnitState.Running => HmiBrushes.Good,
        _ => HmiBrushes.Idle,
    };

    public string StateText => State switch
    {
        UnitState.Alarm => "ALARM",
        UnitState.Warning => "WARN",
        UnitState.Running => "RUN",
        _ => "STOP",
    };
}

/// <summary>Rolling plant signal used by the trend chart and the KPI strip.</summary>
public sealed class SignalHistory
{
    private readonly double[] _buffer;
    private int _count;

    public SignalHistory(int capacity)
    {
        _buffer = new double[capacity];
        Capacity = capacity;
    }

    public int Capacity { get; }
    public int Count => _count;

    public double Latest => _count == 0 ? 0 : _buffer[_count - 1];

    public void Add(double value)
    {
        if (_count < Capacity)
        {
            _buffer[_count++] = value;
            return;
        }

        Array.Copy(_buffer, 1, _buffer, 0, Capacity - 1);
        _buffer[Capacity - 1] = value;
    }

    /// <summary>Oldest to newest copy, padded at the left with the first sample.</summary>
    public double[] ToArray()
    {
        var result = new double[Capacity];
        if (_count == 0) return result;
        int pad = Capacity - _count;
        for (int i = 0; i < pad; i++) result[i] = _buffer[0];
        Array.Copy(_buffer, 0, result, pad, _count);
        return result;
    }

    public double Min() => _count == 0 ? 0 : ToArray().Min();
    public double Max() => _count == 0 ? 0 : ToArray().Max();
}
