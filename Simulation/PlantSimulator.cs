using System.Collections.ObjectModel;

namespace PlantHmi.Simulation;

/// <summary>
/// Deterministic-ish process simulator driving the whole HMI:
/// drifting engineering values, threshold alarms with hysteresis and per-unit health.
/// </summary>
public sealed class PlantSimulator
{
    private readonly Random _rng = new(20261009);
    private readonly Dictionary<string, AlarmRecord> _live = new();

    private double _disturbance;         // 0..1 extra process load
    private double _disturbanceTimer = 6;
    private double _disturbanceLength;
    private double _historyAccumulator;
    private double _oee = 91.4;

    public const double AmbientTemperature = 23.5;
    public const double AmbientPressure = 1.0;
    public const double SetpointTemperature = 82.0;
    public const double SetpointPressure = 4.6;
    public const double ThroughputSetpoint = 118.0;

    public double Temperature { get; private set; } = 23.5;
    public double Pressure { get; private set; } = 1.0;
    public double Throughput { get; private set; }
    public double Flow { get; private set; }
    public double TankLevel { get; private set; } = 0.64;
    public double Vibration { get; private set; } = 0.2;
    public double Oee => _oee;
    public double ProductQuality { get; private set; } = 98.2;

    public bool Running { get; private set; }
    public TimeSpan Uptime { get; private set; }
    public TimeSpan ShiftElapsed { get; } = TimeSpan.FromHours(6) - TimeSpan.FromMinutes(41);

    public SignalHistory TempHistory { get; } = new(260);
    public SignalHistory PressureHistory { get; } = new(260);
    public SignalHistory ThroughputHistory { get; } = new(260);

    public ObservableCollection<AlarmRecord> ActiveAlarms { get; } = new();
    public List<UnitLamp> Units { get; } = new()
    {
        new UnitLamp { Name = "Feed Tank T-101", Tag = "T-101" },
        new UnitLamp { Name = "Feed Pump P-102", Tag = "P-102" },
        new UnitLamp { Name = "Reactor R-201", Tag = "R-201" },
        new UnitLamp { Name = "Heat Exchanger E-301", Tag = "E-301" },
        new UnitLamp { Name = "Product Silo S-401", Tag = "S-401" },
    };

    public UnitLamp FeedTank => Units[0];
    public UnitLamp FeedPump => Units[1];
    public UnitLamp Reactor => Units[2];
    public UnitLamp Exchanger => Units[3];
    public UnitLamp ProductSilo => Units[4];

    public int UnacknowledgedCount
    {
        get
        {
            int n = 0;
            foreach (var a in ActiveAlarms) if (!a.Acknowledged) n++;
            return n;
        }
    }

    public event EventHandler? AlarmsChanged;

    public void Start()
    {
        if (Running) return;
        Running = true;
        _disturbance = 0;
        _disturbanceTimer = 8;
        Raise("COOLING LOOP", "Line start sequence complete — ramp to setpoint initiated", AlarmSeverity.Info);
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        Raise("LINE CONTROL", "Operator stop — controlled ramp-down in progress", AlarmSeverity.Warning);
    }

    /// <summary>Acknowledges every outstanding alarm (classic HMI ACK behaviour).</summary>
    public int AcknowledgeAll()
    {
        int n = 0;
        foreach (var alarm in ActiveAlarms.ToList())
        {
            if (alarm.Acknowledged) continue;
            alarm.Acknowledged = true;
            n++;
        }

        for (int i = ActiveAlarms.Count - 1; i >= 0; i--)
        {
            if (!ActiveAlarms[i].Active && ActiveAlarms[i].Acknowledged) ActiveAlarms.RemoveAt(i);
        }

        if (n > 0) AlarmsChanged?.Invoke(this, EventArgs.Empty);
        return n;
    }

    public void Update(double dt)
    {
        if (dt <= 0) return;
        if (dt > 0.5) dt = 0.5;

        UpdateDisturbance(dt);

        if (Running)
        {
            Uptime += TimeSpan.FromSeconds(dt);

            double tempTarget = SetpointTemperature + _disturbance * 15.5;
            Temperature += (tempTarget - Temperature) * Math.Min(1, dt * 0.35) + (_rng.NextDouble() - 0.5) * 0.35;

            double pressTarget = SetpointPressure + _disturbance * 2.4 + (Temperature - SetpointTemperature) * 0.035;
            Pressure += (pressTarget - Pressure) * Math.Min(1, dt * 0.6) + (_rng.NextDouble() - 0.5) * 0.05;
            Pressure = Math.Max(0.4, Pressure);

            double thruTarget = ThroughputSetpoint * (1 - 0.18 * _disturbance) + (_rng.NextDouble() - 0.5) * 2.4;
            Throughput += (thruTarget - Throughput) * Math.Min(1, dt * 0.5);

            double flowTarget = Math.Clamp(Throughput / ThroughputSetpoint, 0, 1.15);
            Flow += (flowTarget - Flow) * Math.Min(1, dt * 0.8);

            Vibration += ((1.6 + _disturbance * 3.4 + (_rng.NextDouble() - 0.5) * 0.3) - Vibration) * Math.Min(1, dt * 0.5);

            double drain = 0.0075 * dt * (0.5 + Flow);
            TankLevel -= drain;
            if (TankLevel < 0.22) TankLevel += 0.035 * dt;              // operator tops the feed up
            if (TankLevel < 0.05) TankLevel = 0.05;

            _oee = Math.Clamp(_oee + (91.6 - _disturbance * 6.5 - _oee) * dt * 0.05, 60, 99.5);
            ProductQuality = Math.Clamp(98.4 - _disturbance * 2.2 + (_rng.NextDouble() - 0.5) * 0.05, 80, 100);
        }
        else
        {
            Temperature += (AmbientTemperature - Temperature) * Math.Min(1, dt * 0.08);
            Pressure += (AmbientPressure - Pressure) * Math.Min(1, dt * 0.12);
            Throughput += (0 - Throughput) * Math.Min(1, dt * 0.5);
            Flow += (0 - Flow) * Math.Min(1, dt * 0.5);
            Vibration += (0.15 - Vibration) * Math.Min(1, dt * 0.4);
            TankLevel = Math.Min(1, TankLevel + 0.01 * dt);
            _oee = Math.Clamp(_oee + (0 - _oee) * dt * 0.02, 0, 99.5);
        }

        Temperature = Math.Clamp(Temperature, -10, 160);
        TankLevel = Math.Clamp(TankLevel, 0, 0.98);

        _historyAccumulator += dt;
        if (_historyAccumulator >= 0.4)
        {
            _historyAccumulator = 0;
            TempHistory.Add(Temperature);
            PressureHistory.Add(Pressure);
            ThroughputHistory.Add(Throughput);
        }

        EvaluateAlarms();
        EvaluateUnits();
    }

    private void UpdateDisturbance(double dt)
    {
        if (!Running)
        {
            _disturbance += (0 - _disturbance) * Math.Min(1, dt * 0.4);
            return;
        }

        if (_disturbanceLength > 0)
        {
            _disturbanceLength -= dt;
            if (_disturbanceLength <= 0)
            {
                _disturbanceLength = 0;
                _disturbance = 0;
                _disturbanceTimer = 15 + _rng.NextDouble() * 25;
            }
        }
        else
        {
            _disturbanceTimer -= dt;
            if (_disturbanceTimer <= 0)
            {
                _disturbanceLength = 9 + _rng.NextDouble() * 16;
                _disturbance = 0.35 + _rng.NextDouble() * 0.75;
                if (_rng.NextDouble() < 0.45)
                    Raise("E-301", "Coolant bypass valve stiction detected — heat removal degraded", AlarmSeverity.Warning);
            }
        }

        // gentle wobble while a disturbance is active
        if (_disturbanceLength > 0)
            _disturbance = Math.Clamp(_disturbance + (_rng.NextDouble() - 0.5) * dt * 0.25, 0, 1.2);
    }

    private void EvaluateAlarms()
    {
        Check("TEMP-HI", "R-201", "Reactor temperature high",
            Temperature, 90.5, 92.0, 88.5, 96.5, "°C", AlarmSeverity.Warning, AlarmSeverity.Critical);

        Check("PRES-HI", "R-201", "Reactor head pressure high",
            Pressure, 6.3, 6.3, 5.9, 7.4, "bar", AlarmSeverity.Warning, AlarmSeverity.Critical);

        Check("VIB-HI", "P-102", "Feed pump vibration above ISO zone B",
            Vibration, 4.0, 4.0, 3.6, 5.2, "mm/s", AlarmSeverity.Warning, AlarmSeverity.Critical);

        CheckLow("FLOW-LO", "FIC-201", "Process flow below minimum (line running)",
            Running ? Flow : 1.0, 0.35, 0.30, 0.42, 0.18, "x", AlarmSeverity.Warning, AlarmSeverity.Critical);

        CheckLow("LEVEL-LO", "T-101", "Feed tank level low",
            TankLevel, 0.22, 0.20, 0.26, 0.08, "frac", AlarmSeverity.Warning, AlarmSeverity.Critical);
    }

    private void Check(string tag, string source, string message, double value,
        double warnAt, double alarmAt, double clearAt, double criticalAt,
        string unit, AlarmSeverity warnSeverity, AlarmSeverity critSeverity)
    {
        bool alarm = value >= criticalAt;
        bool warn = value >= warnAt;

        if (alarm || warn)
        {
            var severity = alarm ? critSeverity : warnSeverity;
            if (!_live.TryGetValue(tag, out var record))
            {
                record = new AlarmRecord
                {
                    Tag = tag,
                    Source = source,
                    Message = message,
                    Severity = severity,
                    Unit = unit,
                    LimitValue = alarm ? criticalAt : warnAt,
                    PeakValue = value,
                };
                _live[tag] = record;
                ActiveAlarms.Insert(0, record);
                AlarmsChanged?.Invoke(this, EventArgs.Empty);
            }
            else if (severity != record.Severity)
            {
                record.Severity = severity;   // escalate/de-escalate
                AlarmsChanged?.Invoke(this, EventArgs.Empty);
            }

            if (value > _live[tag].PeakValue)
            {
                _live[tag].PeakValue = value;
                _live[tag].RaisePeak();
            }
        }
        else if (value <= clearAt && _live.TryGetValue(tag, out var existing))
        {
            existing.Active = false;
            _live.Remove(tag);
            if (existing.Acknowledged) ActiveAlarms.Remove(existing);
            AlarmsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CheckLow(string tag, string source, string message, double value,
        double warnAt, double alarmAt, double clearAt, double criticalAt,
        string unit, AlarmSeverity warnSeverity, AlarmSeverity critSeverity)
    {
        bool alarm = value <= criticalAt;
        bool warn = value <= warnAt;

        if (alarm || warn)
        {
            var severity = alarm ? critSeverity : warnSeverity;
            if (!_live.TryGetValue(tag, out var record))
            {
                record = new AlarmRecord
                {
                    Tag = tag,
                    Source = source,
                    Message = message,
                    Severity = severity,
                    Unit = unit,
                    LimitValue = alarm ? criticalAt : warnAt,
                    PeakValue = value,
                };
                _live[tag] = record;
                ActiveAlarms.Insert(0, record);
                AlarmsChanged?.Invoke(this, EventArgs.Empty);
            }
            else if (severity != record.Severity)
            {
                record.Severity = severity;
                AlarmsChanged?.Invoke(this, EventArgs.Empty);
            }

            if (value < _live[tag].PeakValue || _live[tag].PeakValue == 0)
            {
                _live[tag].PeakValue = value;
                _live[tag].RaisePeak();
            }
        }
        else if (value >= clearAt && _live.TryGetValue(tag, out var existing))
        {
            existing.Active = false;
            _live.Remove(tag);
            if (existing.Acknowledged) ActiveAlarms.Remove(existing);
            AlarmsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Raise(string source, string message, AlarmSeverity severity)
    {
        var record = new AlarmRecord
        {
            Tag = source,
            Source = source,
            Message = message,
            Severity = severity,
            Unit = string.Empty,
        };
        ActiveAlarms.Insert(0, record);
        while (ActiveAlarms.Count > 60) ActiveAlarms.RemoveAt(ActiveAlarms.Count - 1);
        AlarmsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EvaluateUnits()
    {
        FeedTank.State = TankLevel <= 0.22 ? UnitState.Warning : Running ? UnitState.Running : UnitState.Stopped;
        FeedTank.ValueText = $"{TankLevel * 100:0} %";

        FeedPump.State = Vibration >= 4.0 ? UnitState.Alarm : Running ? UnitState.Running : UnitState.Stopped;
        FeedPump.ValueText = $"{Vibration:0.0} mm/s";

        Reactor.State = Temperature >= 95 || Pressure >= 6.9 ? UnitState.Alarm
            : Temperature >= 90.5 || Pressure >= 6.3 ? UnitState.Warning
            : Running ? UnitState.Running : UnitState.Stopped;
        Reactor.ValueText = $"{Temperature:0.0} °C";

        Exchanger.State = _disturbance > 0.25 ? UnitState.Warning : Running ? UnitState.Running : UnitState.Stopped;
        Exchanger.ValueText = $"{(_disturbance > 0.25 ? "BYPASS" : Running ? "AUTO" : "IDLE")}";

        ProductSilo.State = Running && Throughput > 40 ? UnitState.Running : UnitState.Stopped;
        ProductSilo.ValueText = $"{Throughput:0.0} t/h";
    }
}
