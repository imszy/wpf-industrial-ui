using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PlantHmi.Simulation;

namespace PlantHmi.Controls;

/// <summary>
/// Hand-drawn animated process scheme: feed tank → pump → reactor → exchanger → silo,
/// connected by pipes with flowing chevrons, live vessel levels, per-unit status lamps,
/// a rotating impeller and a pulsing alarm beacon.
/// </summary>
public sealed class PipelineVisual : FrameworkElement
{
    private static readonly Typeface FaceSmall = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface FaceNumeric = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Brush TextDim = new SolidColorBrush(Color.FromRgb(0x93, 0xA4, 0xBF));
    private static readonly Brush TextMuted = new SolidColorBrush(Color.FromRgb(0x5C, 0x6E, 0x8A));
    private static readonly Brush TextBright = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF7));
    private static readonly Brush PipeShell = new SolidColorBrush(Color.FromRgb(0x13, 0x1F, 0x31));
    private static readonly Brush PipeEdge = new SolidColorBrush(Color.FromRgb(0x27, 0x38, 0x52));
    private static readonly Brush VesselShell = new SolidColorBrush(Color.FromRgb(0x0C, 0x15, 0x24));
    private static readonly Brush VesselEdge = new SolidColorBrush(Color.FromRgb(0x2A, 0x3C, 0x57));

    private static readonly Color ColCyan = Color.FromRgb(0x22, 0xD3, 0xEE);
    private static readonly Color ColGreen = Color.FromRgb(0x34, 0xD3, 0x99);
    private static readonly Color ColAmber = Color.FromRgb(0xFB, 0xBF, 0x24);
    private static readonly Color ColRed = Color.FromRgb(0xF8, 0x71, 0x71);
    private static readonly Color ColSlate = Color.FromRgb(0x3D, 0x4C, 0x63);

    private double _time;
    private double _flowDisplay;
    private double _impeller;
    private double _beacon;

    public PlantSimulator? Simulator { get; set; }

    public void Animate(double dt)
    {
        var sim = Simulator;
        _time += dt;

        double targetFlow = sim?.Flow ?? 0;
        _flowDisplay += (targetFlow - _flowDisplay) * Math.Min(1, dt * 3.0);
        _impeller += dt * (60 + _flowDisplay * 900);
        _beacon += dt;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 120 || h < 90) return;

        var sim = Simulator;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double pulse = 0.5 + 0.5 * Math.Sin(_beacon * 5.2);

        var inner = new Rect(16, 12, w - 32, h - 24);
        bool running = sim?.Running ?? false;

        double vesselH = inner.Height * 0.44;
        double vesselW = Math.Min(inner.Width * 0.125, vesselH * 0.78);
        double vesselTop = inner.Top + inner.Height * 0.20;
        double pipeH = Math.Clamp(inner.Height * 0.062, 10, 20);
        double pipeY = inner.Bottom - inner.Height * 0.24;

        double[] centers =
        {
            inner.Left + inner.Width * 0.11,
            inner.Left + inner.Width * 0.375,
            inner.Left + inner.Width * 0.625,
            inner.Left + inner.Width * 0.89,
        };

        var tanks = new[] { sim?.FeedTank, sim?.FeedPump, sim?.Reactor, sim?.ProductSilo };
        var levels = new[]
        {
            sim?.TankLevel ?? 0,
            running ? 0.62 + 0.1 * Math.Sin(_time * 1.3) : 0.15,
            sim is null ? 0 : Math.Clamp((sim.Temperature - 20) / 100.0, 0.05, 0.95),
            sim is null ? 0 : Math.Clamp(sim.Throughput / 140.0, 0.02, 0.95),
        };
        var liquid = new[] { ColCyan, ColGreen, ColAmber, ColCyan };
        var tags = new[] { "T-101", "P-102", "R-201", "S-401" };
        var names = new[] { "FEED", "PUMP", "REACTOR", "PRODUCT" };

        // ---------------- connecting pipe -------------------------------
        var pipeRect = new Rect(centers[0], pipeY, centers[3] - centers[0], pipeH);
        dc.DrawRoundedRectangle(PipeShell, new Pen(PipeEdge, 1.4), pipeRect, pipeH / 2, pipeH / 2);

        // flow chevrons inside the pipe
        double chevronSpan = pipeRect.Width;
        double spacing = Math.Max(26, pipeRect.Width / 16);
        double speed = 26 + _flowDisplay * 190;
        for (double x = 0; x < chevronSpan; x += spacing)
        {
            double pos = (x + (_time * speed) % spacing) % chevronSpan;
            double alpha = running ? 60 + 150 * Math.Min(1, _flowDisplay) : 26;
            var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(alpha, 0, 255), ColCyan.R, ColCyan.G, ColCyan.B));
            double cx = pipeRect.X + pos;
            double cy = pipeRect.Y + pipeH / 2;
            double s = pipeH * 0.26;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(cx - s, cy - s), false, false);
                g.LineTo(new Point(cx + s * 0.2, cy), true, false);
                g.LineTo(new Point(cx - s, cy + s), true, false);
            }

            geo.Freeze();
            dc.DrawGeometry(null, new Pen(brush, 2.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
        }

        // ---------------- vessels + connectors --------------------------
        for (int i = 0; i < centers.Length; i++)
        {
            double cx = centers[i];
            var state = tanks[i]?.State ?? UnitState.Stopped;
            var body = new Rect(cx - vesselW / 2, vesselTop, vesselW, vesselH);

            // vertical connector down to the pipe
            dc.DrawRectangle(PipeShell, new Pen(PipeEdge, 1.2), new Rect(cx - pipeH * 0.28, body.Bottom, pipeH * 0.56, pipeY - body.Bottom + 1));

            DrawVessel(dc, body, levels[i], liquid[i], state, running, dpi, _time);

            // status lamp above the vessel
            DrawLamp(dc, new Point(cx, vesselTop - 17), state, pulse, dpi);

            // tag + name under the pipe
            DrawText(dc, tags[i], 11, state == UnitState.Alarm ? new SolidColorBrush(ColRed) : TextBright, FaceNumeric, dpi, new Point(cx, pipeRect.Bottom + 16));
            DrawText(dc, names[i], 9.5, TextMuted, FaceSmall, dpi, new Point(cx, pipeRect.Bottom + 31));
        }

        // ---------------- pump impeller ---------------------------------
        var pumpCenter = new Point((centers[0] + centers[1]) / 2, pipeRect.Y + pipeH / 2);
        double pumpR = pipeH * 1.35;
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x0E, 0x1B, 0x2B)), new Pen(PipeEdge, 1.6), pumpCenter, pumpR, pumpR);
        for (int blade = 0; blade < 3; blade++)
        {
            double a = _impeller + blade * 120;
            var pen = new Pen(new SolidColorBrush(running ? ColGreen : ColSlate), 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen, pumpCenter, PointOn(pumpCenter, pumpR * 0.72, a));
        }

        dc.DrawEllipse(new SolidColorBrush(ColSlate), null, pumpCenter, 2.2, 2.2);
        DrawText(dc, $"FIC-201  {_flowDisplay:0.00}", 10.5, TextDim, FaceNumeric, dpi,
            new Point(pumpCenter.X, pumpCenter.Y + pumpR + 15));

        // ---------------- exchanger fins --------------------------------
        var exRect = new Rect(centers[2] - vesselW / 2, vesselTop, vesselW, vesselH);
        var finPen = new Pen(new SolidColorBrush(Color.FromArgb(150, ColCyan.R, ColCyan.G, ColCyan.B)), 1.2);
        for (int i = 1; i <= 6; i++)
        {
            double y = exRect.Top + exRect.Height * i / 7.0;
            dc.DrawLine(finPen, new Point(exRect.X + 4, y), new Point(exRect.Right - 4, y));
        }

        // ---------------- alarm beacon ----------------------------------
        bool anyAlarm = false;
        foreach (var t in tanks) if (t?.State == UnitState.Alarm) anyAlarm = true;

        var beacon = new Point(inner.Right - 12, inner.Top + 12);
        if (anyAlarm)
        {
            for (int ring = 0; ring < 3; ring++)
            {
                double phase = (_beacon * 0.9 + ring / 3.0) % 1.0;
                double r = 10 + phase * 26;
                byte a = (byte)Math.Clamp(120 * (1 - phase), 0, 255);
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(a, ColRed.R, ColRed.G, ColRed.B)), 2), beacon, r, r);
            }

            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(170 + 85 * pulse), ColRed.R, ColRed.G, ColRed.B)), null, beacon, 8, 8);
            DrawText(dc, "ALARM", 9.5, new SolidColorBrush(ColRed), FaceSmall, dpi, new Point(beacon.X - 30, beacon.Y + 20));
        }
        else
        {
            dc.DrawEllipse(new SolidColorBrush(running ? ColGreen : ColSlate), null, beacon, 7, 7);
            DrawText(dc, running ? "LINE OK" : "IDLE", 9.5, running ? new SolidColorBrush(ColGreen) : TextMuted, FaceSmall, dpi, new Point(beacon.X - 32, beacon.Y + 20));
        }

        // ---------------- header row ------------------------------------
        DrawText(dc, running ? "PROCESS FLOW · NOMINAL" : "PROCESS FLOW · STOPPED", 10.5,
            running ? new SolidColorBrush(ColGreen) : TextMuted, FaceSmall, dpi, new Point(inner.Left + 4, inner.Top + 4), TextAlign.Left);
    }

    private static void DrawVessel(DrawingContext dc, Rect rect, double level, Color liquid, UnitState state, bool running, double dpi, double time)
    {
        double r = 7;
        double radiusCorner = Math.Min(r, rect.Width / 2);
        dc.DrawRoundedRectangle(VesselShell, new Pen(VesselEdge, 1.4), rect, radiusCorner, radiusCorner);

        var clip = new RectangleGeometry(rect, radiusCorner, radiusCorner);
        dc.PushClip(clip);

        double lh = rect.Height * Math.Clamp(level, 0, 1);
        var liquidRect = new Rect(rect.X, rect.Bottom - lh, rect.Width, lh);
        var brush = new LinearGradientBrush(
            Color.FromArgb(225, liquid.R, liquid.G, liquid.B),
            Color.FromArgb(70, liquid.R, liquid.G, liquid.B), 90);
        dc.DrawRectangle(brush, null, liquidRect);

        if (running && lh > 2)
        {
            // rippling surface
            var wave = new StreamGeometry();
            using (var g = wave.Open())
            {
                g.BeginFigure(new Point(rect.X, liquidRect.Top + Math.Sin(time * 2.4) * 1.2), false, false);
                for (double x = 0; x <= rect.Width; x += 6)
                    g.LineTo(new Point(rect.X + x, liquidRect.Top + Math.Sin(time * 2.4 + x * 0.14) * 1.6), true, false);
            }

            wave.Freeze();
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)), 1.4), wave);
        }

        // bubbles for life
        if (running)
        {
            var bubble = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
            for (int i = 0; i < 3; i++)
            {
                double phase = (time * (0.35 + i * 0.12) + i * 0.37) % 1.0;
                double bx = rect.X + rect.Width * (0.25 + 0.22 * i);
                double by = liquidRect.Bottom - phase * lh;
                dc.DrawEllipse(bubble, null, new Point(bx, by), 1.6 + i * 0.5, 1.6 + i * 0.5);
            }
        }

        dc.Pop();

        // level scale
        var tickPen = new Pen(new SolidColorBrush(Color.FromArgb(120, 0x8A, 0xA0, 0xC0)), 1);
        for (int i = 1; i <= 3; i++)
        {
            double y = rect.Bottom - rect.Height * i / 4.0;
            dc.DrawLine(tickPen, new Point(rect.X + 3, y), new Point(rect.X + 10, y));
        }

        // percent label
        DrawText(dc, $"{level * 100:0}%", 10.5, TextDim, FaceNumeric, dpi, new Point(rect.X + rect.Width / 2, rect.Top + 12));

        if (state == UnitState.Alarm)
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(200, 0xF8, 0x71, 0x71)), 2), rect, radiusCorner, radiusCorner);
    }

    private static void DrawLamp(DrawingContext dc, Point center, UnitState state, double pulse, double dpi)
    {
        Color color = state switch
        {
            UnitState.Alarm => ColRed,
            UnitState.Warning => ColAmber,
            UnitState.Running => ColGreen,
            _ => ColSlate,
        };

        bool pulsing = state is UnitState.Alarm or UnitState.Warning;
        double r = state == UnitState.Alarm ? 6.0 + pulse * 1.6 : 5.5;

        if (pulsing)
        {
            byte halo = (byte)Math.Clamp(60 + 90 * pulse, 0, 255);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(halo, color.R, color.G, color.B)), null, center, r + 6, r + 6);
        }

        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(pulsing ? (byte)(150 + 105 * pulse) : (byte)230, color.R, color.G, color.B)), null, center, r, r);
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1), center, r * 0.55, r * 0.55);
    }

    private enum TextAlign { Left, Center }

    private static void DrawText(DrawingContext dc, string text, double size, Brush brush, Typeface face, double dpi, Point anchor, TextAlign align = TextAlign.Center)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, dpi);
        double x = align == TextAlign.Center ? anchor.X - ft.Width / 2 : anchor.X;
        dc.DrawText(ft, new Point(x, anchor.Y - ft.Height / 2));
    }

    private static Point PointOn(Point center, double radius, double angleDegrees)
    {
        double rad = angleDegrees * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(rad), center.Y - radius * Math.Sin(rad));
    }
}
