using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PlantHmi.Simulation;

namespace PlantHmi;

public partial class MainWindow : Window
{
    private const double SamplePeriod = 0.4;      // seconds between historian samples
    private const double WindowSeconds = 260 * SamplePeriod;

    private static readonly Color Cyan = Color.FromRgb(0x22, 0xD3, 0xEE);
    private static readonly Color Amber = Color.FromRgb(0xFB, 0xBF, 0x24);
    private static readonly Color Green = Color.FromRgb(0x34, 0xD3, 0x99);
    private static readonly Color Red = Color.FromRgb(0xF8, 0x71, 0x71);
    private static readonly Color Muted = Color.FromRgb(0x5C, 0x6E, 0x8A);

    private readonly PlantSimulator _sim = new();
    private readonly DispatcherTimer _logicTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly Stopwatch _wallClock = Stopwatch.StartNew();

    private TimeSpan _lastFrame;
    private double _chartAccumulator;
    private long _frameCount;
    private Storyboard? _ackStoryboard;
    private bool _chartReady;

    private ScottPlot.Plottables.Signal? _tempSeries;
    private ScottPlot.Plottables.Signal? _pressureSeries;
    private ScottPlot.Plottables.Signal? _throughputSeries;

    // SignalSourceDouble keeps a reference to these lists, so mutating them in place
    // updates the plotted data without rebuilding any plottable.
    private readonly List<double> _tempValues = new();
    private readonly List<double> _pressureValues = new();
    private readonly List<double> _throughputValues = new();

    public MainWindow()
    {
        InitializeComponent();

        ConfigureGauges();

        Pipeline.Simulator = _sim;
        LampList.ItemsSource = _sim.Units;
        AlarmList.ItemsSource = _sim.ActiveAlarms;
        _sim.AlarmsChanged += (_, _) => Dispatcher.BeginInvoke(UpdateAlarmVisuals);

        _logicTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(200) };
        _logicTimer.Tick += OnLogicTick;

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += OnClockTick;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    // =====================================================================
    //  Setup
    // =====================================================================

    private void ConfigureGauges()
    {
        GaugeTemp.Unit = "°C";
        GaugeTemp.Minimum = 0;
        GaugeTemp.Maximum = 140;
        GaugeTemp.WarningAt = 90.5;
        GaugeTemp.AlarmAt = 95;
        GaugeTemp.Setpoint = PlantSimulator.SetpointTemperature;
        GaugeTemp.ValueFormat = "0.0";
        GaugeTemp.Accent = Cyan;
        GaugeTemp.WarningText = "HIGH";
        GaugeTemp.AlarmText = "OVER TEMP";
        GaugeTemp.SnapToTarget();

        GaugePressure.Unit = "bar";
        GaugePressure.Minimum = 0;
        GaugePressure.Maximum = 8;
        GaugePressure.WarningAt = 6.3;
        GaugePressure.AlarmAt = 6.9;
        GaugePressure.Setpoint = PlantSimulator.SetpointPressure;
        GaugePressure.ValueFormat = "0.00";
        GaugePressure.Accent = Amber;
        GaugePressure.WarningText = "HIGH";
        GaugePressure.AlarmText = "OVER PRESS";
        GaugePressure.SnapToTarget();

        GaugeThroughput.Unit = "t/h";
        GaugeThroughput.Minimum = 0;
        GaugeThroughput.Maximum = 160;
        GaugeThroughput.WarningAt = 130;
        GaugeThroughput.AlarmAt = 148;
        GaugeThroughput.Setpoint = PlantSimulator.ThroughputSetpoint;
        GaugeThroughput.ValueFormat = "0";
        GaugeThroughput.Accent = Green;
        GaugeThroughput.WarningText = "HIGH";
        GaugeThroughput.AlarmText = "OVER RATE";
        GaugeThroughput.SnapToTarget();
    }

    /// <summary>
    /// Builds the ScottPlot/SkiaSharp trend. If the native Skia engine cannot start on the
    /// host the rest of the HMI keeps running in a degraded (chart-less) mode.
    /// </summary>
    private void TryConfigureChart()
    {
        try
        {
            ConfigureChart();
            _chartReady = true;
        }
        catch (Exception ex)
        {
            _chartReady = false;
            Trend.Visibility = Visibility.Collapsed;
            TxtTrendWindow.Text = "· chart engine unavailable";
            TxtTicker.Text = "Trend chart disabled — ScottPlot/SkiaSharp could not start: " + ex.Message;
        }
    }

    private void ConfigureChart()
    {
        var plot = Trend.Plot;

        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#0A111E");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#0A111E");
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#182740");
        plot.Grid.MinorLineColor = ScottPlot.Color.FromHex("#101B2C");
        plot.Axes.Color(ScottPlot.Color.FromHex("#7C8FA8"));

        _tempValues.AddRange(_sim.TempHistory.ToArray());
        _tempSeries = plot.Add.Signal(new ScottPlot.DataSources.SignalSourceDouble(_tempValues, SamplePeriod));
        _tempSeries.Color = ScottPlot.Color.FromHex("#22D3EE");
        _tempSeries.LineWidth = 2.2f;
        _tempSeries.LegendText = "Reactor temperature (°C)";

        _throughputValues.AddRange(_sim.ThroughputHistory.ToArray());
        _throughputSeries = plot.Add.Signal(new ScottPlot.DataSources.SignalSourceDouble(_throughputValues, SamplePeriod));
        _throughputSeries.Color = ScottPlot.Color.FromHex("#34D399");
        _throughputSeries.LineWidth = 1.8f;
        _throughputSeries.LegendText = "Throughput (t/h)";

        var rightAxis = plot.Axes.AddRightAxis();

        _pressureValues.AddRange(_sim.PressureHistory.ToArray());
        _pressureSeries = plot.Add.Signal(new ScottPlot.DataSources.SignalSourceDouble(_pressureValues, SamplePeriod));
        _pressureSeries.Color = ScottPlot.Color.FromHex("#FBBF24");
        _pressureSeries.LineWidth = 2.0f;
        _pressureSeries.LegendText = "Reactor pressure (bar)";
        _pressureSeries.Axes.YAxis = rightAxis;

        var setpointLine = plot.Add.HorizontalLine(PlantSimulator.SetpointTemperature);
        setpointLine.Color = ScottPlot.Color.FromHex("#22D3EE").WithAlpha(0.45);
        setpointLine.LineWidth = 1.2f;
        setpointLine.LinePattern = ScottPlot.LinePattern.Dashed;
        setpointLine.LegendText = "Temp setpoint 82.0 °C";

        // AxisBase carries the modern label API; the IXAxis/IYAxis interfaces used by
        // Axes.Bottom/Left only expose the obsolete Label style object.
        var bottomAxis = (ScottPlot.AxisPanels.AxisBase)plot.Axes.Bottom;
        var leftAxis = (ScottPlot.AxisPanels.AxisBase)plot.Axes.Left;
        var rightAxisBase = (ScottPlot.AxisPanels.AxisBase)rightAxis;
        bottomAxis.LabelText = "Time (s)";
        leftAxis.LabelText = "Temperature (°C) / Throughput (t/h)";
        plot.Axes.Bottom.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#7C8FA8");
        plot.Axes.Bottom.TickLabelStyle.FontSize = 11;
        plot.Axes.Left.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#7C8FA8");
        plot.Axes.Left.TickLabelStyle.FontSize = 11;
        plot.Axes.Right.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#C79A2E");
        plot.Axes.Right.TickLabelStyle.FontSize = 11;
        bottomAxis.LabelFontColor = ScottPlot.Color.FromHex("#93A4BF");
        leftAxis.LabelFontColor = ScottPlot.Color.FromHex("#93A4BF");
        rightAxisBase.LabelText = "Pressure (bar)";
        rightAxisBase.LabelFontColor = ScottPlot.Color.FromHex("#C79A2E");

        plot.ShowLegend(ScottPlot.Alignment.UpperRight);
        plot.Legend.BackgroundColor = ScottPlot.Color.FromHex("#0E1A2B");
        plot.Legend.OutlineColor = ScottPlot.Color.FromHex("#22344E");
        plot.Legend.FontColor = ScottPlot.Color.FromHex("#C6D4E6");
        plot.Legend.FontSize = 11;

        AutoScaleChart();
        Trend.Refresh();
    }

    private void AutoScaleChart()
    {
        var plot = Trend.Plot;
        plot.Axes.AutoScale();
        plot.Axes.SetLimitsX(0, WindowSeconds);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TryConfigureChart();       // after the visual tree exists so SkiaSharp has a surface
        CompositionTarget.Rendering += OnRendering;
        _lastFrame = _wallClock.Elapsed;
        _logicTimer.Start();
        _clockTimer.Start();
        OnClockTick(this, EventArgs.Empty);
        UpdateReadouts();
        UpdateAlarmVisuals();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _logicTimer.Stop();
        _clockTimer.Stop();
        _ackStoryboard?.Stop();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryEnableDarkTitleBar();
    }

    // =====================================================================
    //  Animation + simulation loops
    // =====================================================================

    /// <summary>
    /// Per-frame (vsync) animation: gauges ease toward their targets and the process
    /// scheme advances; the chart is refreshed at a lower rate because it re-renders in Skia.
    /// </summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        TimeSpan now = _wallClock.Elapsed;
        double dt = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        if (dt <= 0 || dt > 0.5) dt = 1.0 / 60.0;
        _frameCount++;

        GaugeTemp.Animate(dt);
        GaugePressure.Animate(dt);
        GaugeThroughput.Animate(dt);
        Pipeline.Animate(dt);

        _chartAccumulator += dt;
        if (_chartAccumulator >= 0.5)
        {
            _chartAccumulator = 0;
            UpdateChart();
        }
    }

    private void OnLogicTick(object? sender, EventArgs e)
    {
        const double dt = 0.2;
        _sim.Update(dt);

        GaugeTemp.Target = _sim.Temperature;
        GaugePressure.Target = _sim.Pressure;
        GaugeThroughput.Target = _sim.Throughput;
        GaugeTemp.Running = _sim.Running;
        GaugePressure.Running = _sim.Running;
        GaugeThroughput.Running = _sim.Running;

        UpdateReadouts();
        UpdateAlarmVisuals();
    }

    private void UpdateChart()
    {
        if (!_chartReady) return;

        Sync(_tempValues, _sim.TempHistory);
        Sync(_pressureValues, _sim.PressureHistory);
        Sync(_throughputValues, _sim.ThroughputHistory);

        AutoScaleChart();
        Trend.Refresh();
    }

    private static void Sync(List<double> target, SignalHistory history)
    {
        double[] latest = history.ToArray();
        target.Clear();
        target.AddRange(latest);
    }

    // =====================================================================
    //  Readouts
    // =====================================================================

    private void OnClockTick(object? sender, EventArgs e)
    {
        DateTime now = DateTime.Now;
        TxtClock.Text = now.ToString("HH:mm:ss");
        TxtDate.Text = now.ToString("ddd dd MMM yyyy") + "  ·  LOCAL";
        TxtScan.Text = $"SCAN {40 + Random.Shared.Next(0, 9)} ms";
        TxtFrames.Text = $"FRAMES {_frameCount / 1000} k";
    }

    private void UpdateReadouts()
    {
        bool running = _sim.Running;

        TxtLineState.Text = running ? "RUNNING" : "STOPPED";
        TxtLineState.Foreground = new SolidColorBrush(running ? Green : Muted);
        TxtUptime.Text = _sim.Uptime.ToString(@"hh\:mm\:ss");
        TxtOee.Text = $"{_sim.Oee:0.0} %";
        TxtFlow.Text = $"{_sim.Flow:0.00} x";
        TxtLevel.Text = $"{_sim.TankLevel * 100:0} %";
        TxtFlowMode.Text = $"AUTO · {_sim.Throughput:0.0} t/h";
        TxtTrendWindow.Text = _chartReady
            ? $"· {WindowSeconds:0} s rolling window · {_sim.TempHistory.Count} samples"
            : "· chart engine unavailable";

        SetChip(ChipTemp, TxtChipTemp, running
            ? _sim.Temperature >= 95 ? "OVER TEMP" : _sim.Temperature >= 90.5 ? "HIGH" : "NORMAL"
            : "STANDBY",
            running
                ? _sim.Temperature >= 95 ? Red : _sim.Temperature >= 90.5 ? Amber : Green
                : Muted);

        SetChip(ChipPressure, TxtChipPressure, running
            ? _sim.Pressure >= 6.9 ? "OVER PRESS" : _sim.Pressure >= 6.3 ? "HIGH" : "NORMAL"
            : "STANDBY",
            running
                ? _sim.Pressure >= 6.9 ? Red : _sim.Pressure >= 6.3 ? Amber : Green
                : Muted);

        SetChip(ChipThroughput, TxtChipThroughput, running
            ? _sim.Throughput >= 148 ? "OVER RATE" : _sim.Throughput >= 130 ? "HIGH" : "STABLE"
            : "STANDBY",
            running
                ? _sim.Throughput >= 148 ? Red : _sim.Throughput >= 130 ? Amber : Green
                : Muted);

        TxtBatch.Text = running ? $"BATCH #A7-{3391 + (int)(_sim.Uptime.TotalMinutes / 12)}" : "BATCH #A7-3391";

        LinkDot.Fill = new SolidColorBrush(running ? Green : Amber);
        TxtLink.Text = running ? "PLC LINK · ONLINE" : "PLC LINK · STANDBY";

        var latest = _sim.ActiveAlarms.Count > 0 ? _sim.ActiveAlarms[0] : null;
        TxtTicker.Text = latest is null
            ? (running
                ? "All loops within operating envelope — historian recording at 2.5 Hz."
                : "Line stopped — press START LINE to energise the process train.")
            : $"{latest.TimeText}  [{latest.SeverityText}]  {latest.Tag} — {latest.Message}";
    }

    private void UpdateAlarmVisuals()
    {
        int critical = 0, warning = 0, unack = 0;
        foreach (var alarm in _sim.ActiveAlarms)
        {
            if (alarm.Severity == AlarmSeverity.Critical) critical++;
            else if (alarm.Severity == AlarmSeverity.Warning) warning++;
            if (!alarm.Acknowledged) unack++;
        }

        TxtCritical.Text = critical.ToString();
        TxtWarning.Text = warning.ToString();
        TxtUnack.Text = unack.ToString();
        TxtAlarmCount.Text = $"{_sim.ActiveAlarms.Count} ACTIVE";
        TxtAckBadge.Text = unack.ToString();
        AckBadge.Visibility = unack > 0 ? Visibility.Visible : Visibility.Collapsed;
        AlarmEmpty.Visibility = _sim.ActiveAlarms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (unack > 0) StartAckPulse();
        else StopAckPulse();
    }

    private static void SetChip(Border border, TextBlock text, string label, Color color)
    {
        text.Text = label;
        text.Foreground = new SolidColorBrush(color);
        border.BorderBrush = new SolidColorBrush(color);
        border.Background = new SolidColorBrush(Color.FromArgb(38, color.R, color.G, color.B));
    }

    // =====================================================================
    //  Storyboards driven from code
    // =====================================================================

    /// <summary>Pulsing halo around the ACKNOWLEDGE button while unacknowledged alarms exist.</summary>
    private void StartAckPulse()
    {
        if (_ackStoryboard is not null) return;

        var animation = new DoubleAnimation
        {
            From = 0.10,
            To = 0.85,
            Duration = TimeSpan.FromMilliseconds(650),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };

        _ackStoryboard = new Storyboard();
        _ackStoryboard.Children.Add(animation);
        Storyboard.SetTarget(animation, AckPulse);
        Storyboard.SetTargetProperty(animation, new PropertyPath(nameof(UIElement.Opacity)));
        _ackStoryboard.Begin();
    }

    private void StopAckPulse()
    {
        if (_ackStoryboard is null) return;
        _ackStoryboard.Stop();
        _ackStoryboard = null;
        AckPulse.Opacity = 0;
    }

    // =====================================================================
    //  Commands
    // =====================================================================

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        _sim.Start();
        TxtHint.Text = "Line energised · ramp to 82.0 °C / 4.60 bar in progress.";
        UpdateReadouts();
        UpdateAlarmVisuals();
        FlashStatus("Start sequence accepted by PLC — interlocks healthy.", Green);
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _sim.Stop();
        TxtHint.Text = "Controlled ramp-down in progress · agitator coasting.";
        UpdateReadouts();
        UpdateAlarmVisuals();
        FlashStatus("Stop command issued — controlled ramp-down.", Amber);
    }

    private void OnAckClick(object sender, RoutedEventArgs e)
    {
        int acked = _sim.AcknowledgeAll();
        UpdateAlarmVisuals();
        TxtHint.Text = acked > 0
            ? $"{acked} alarm(s) acknowledged by M. Okafor · logged to historian."
            : "No unacknowledged alarms — nothing to acknowledge.";
        FlashStatus(acked > 0 ? $"{acked} alarm(s) acknowledged." : "Acknowledge: no pending alarms.", Green);
    }

    private void OnSilenceClick(object sender, RoutedEventArgs e)
    {
        FlashStatus("Horn silenced for 120 s (alarm banner still active).", Cyan);
    }

    private void FlashStatus(string message, Color color)
    {
        TxtTicker.Text = message;
        var brush = new SolidColorBrush(color);
        TxtTicker.Foreground = brush;

        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.6))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.4))));
        TxtTicker.BeginAnimation(OpacityProperty, fade);
    }

    // =====================================================================
    //  Windows integration
    // =====================================================================

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Requests the immersive dark title bar on Windows 10 1809+ / Windows 11.</summary>
    private void TryEnableDarkTitleBar()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int enabled = 1;
            const int DwmwaUseImmersiveDarkMode = 20;
            const int DwmwaUseImmersiveDarkModeLegacy = 19;
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        }
        catch
        {
            // purely cosmetic — never fail startup for a title bar tint
        }
    }
}
