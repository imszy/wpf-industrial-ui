using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PlantHmi.Controls;

/// <summary>
/// Hand-drawn (no XAML) radial ring gauge: track + value arc + ticks + needle +
/// digital readout, with warning/alarm colouring and a pulsing alarm halo.
/// </summary>
public sealed class RadialGauge : FrameworkElement
{
    private const double StartAngle = 225.0;   // maths convention, 0° = +X
    private const double Sweep = 270.0;        // drawn clockwise

    private static readonly Typeface FaceRegular = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface FaceBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface FaceNumeric = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface FaceNumericLight = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Color ColorOk = Color.FromRgb(0x34, 0xD3, 0x99);
    private static readonly Color ColorWarn = Color.FromRgb(0xFB, 0xBF, 0x24);
    private static readonly Color ColorAlarm = Color.FromRgb(0xF8, 0x71, 0x71);
    private static readonly Color ColorIdle = Color.FromRgb(0x47, 0x5A, 0x77);
    private static readonly Brush TrackBrush = new SolidColorBrush(Color.FromRgb(0x18, 0x26, 0x3B));
    private static readonly Brush TickBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x3F, 0x5C));
    private static readonly Brush TickMajorBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0xA0, 0xC0));
    private static readonly Brush TextDim = new SolidColorBrush(Color.FromRgb(0x93, 0xA4, 0xBF));
    private static readonly Brush TextMuted = new SolidColorBrush(Color.FromRgb(0x5C, 0x6E, 0x8A));
    private static readonly Brush TextBright = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF7));

    static RadialGauge()
    {
        TrackBrush.Freeze();
        TickBrush.Freeze();
        TickMajorBrush.Freeze();
        TextDim.Freeze();
        TextMuted.Freeze();
        TextBright.Freeze();
    }

    public string Unit { get; set; } = string.Empty;
    public string ValueFormat { get; set; } = "0.0";
    public string NormalText { get; set; } = "NORMAL";
    public string WarningText { get; set; } = "HIGH";
    public string AlarmText { get; set; } = "ALARM";
    public string IdleText { get; set; } = "STANDBY";

    public double Minimum { get; set; }
    public double Maximum { get; set; } = 100;
    public double Target { get; set; }
    public double Displayed { get; private set; }
    public double Setpoint { get; set; } = double.NaN;
    public double WarningAt { get; set; } = double.PositiveInfinity;
    public double AlarmAt { get; set; } = double.PositiveInfinity;
    public Color Accent { get; set; } = Color.FromRgb(0x22, 0xD3, 0xEE);
    public bool Running { get; set; }
    public bool ShowTicks { get; set; } = true;

    public void SnapToTarget()
    {
        Displayed = Target;
        InvalidateVisual();
    }

    /// <summary>Exponential approach so the needle/ring never jumps.</summary>
    public void Animate(double dt)
    {
        double k = 1 - Math.Exp(-dt * 2.6);
        if (Math.Abs(Target - Displayed) < 0.0005) Displayed = Target;
        else Displayed += (Target - Displayed) * k;
        InvalidateVisual();
    }

    private double Fraction => Math.Clamp((Displayed - Minimum) / Math.Max(0.0001, Maximum - Minimum), 0, 1);

    private Color StateColor
    {
        get
        {
            if (!Running) return ColorIdle;
            if (Displayed >= AlarmAt) return ColorAlarm;
            if (Displayed >= WarningAt) return ColorWarn;
            return Accent;
        }
    }

    private string StateText
    {
        get
        {
            if (!Running) return IdleText;
            if (Displayed >= AlarmAt) return AlarmText;
            if (Displayed >= WarningAt) return WarningText;
            return NormalText;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40) return;

        double side = Math.Min(w, h * 1.35);
        double radius = side * 0.365;
        double thickness = Math.Clamp(side * 0.055, 7, 15);
        var center = new Point(w / 2, h * 0.485);

        double pulse = 0.5 + 0.5 * Math.Sin(Environment.TickCount64 / 1000.0 * 5.0);
        Color state = StateColor;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(0x11, 0x1B, 0x2C)), 1),
            center, radius + thickness + 4, radius + thickness + 4);

        // --- alarm halo -------------------------------------------------
        if (Running && Displayed >= AlarmAt)
        {
            var haloColor = Color.FromArgb((byte)(18 + 70 * pulse), state.R, state.G, state.B);
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(haloColor), thickness * 0.5 + 8), center, radius + thickness + 6, radius + thickness + 6);
        }

        // --- track ------------------------------------------------------
        var trackPen = new Pen(TrackBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, trackPen, BuildArc(center, radius, StartAngle, -Sweep));

        // --- value arc (+ glow) ----------------------------------------
        double sweep = -Sweep * Fraction;
        if (Math.Abs(sweep) > 0.35)
        {
            var accentBrush = new SolidColorBrush(state);
            var glow = new Pen(new SolidColorBrush(Color.FromArgb(48, state.R, state.G, state.B)), thickness * 2.2)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            dc.DrawGeometry(null, glow, BuildArc(center, radius, StartAngle, sweep));

            var mainPen = new Pen(accentBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, mainPen, BuildArc(center, radius, StartAngle, sweep));

            // bright leading dot
            Point tip = PointOn(center, radius, StartAngle + sweep);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), null, tip, thickness * 0.20, thickness * 0.20);
        }

        // --- ticks ------------------------------------------------------
        if (ShowTicks)
        {
            const int ticks = 45;
            for (int i = 0; i <= ticks; i++)
            {
                bool major = i % 5 == 0;
                double angle = StartAngle - Sweep * i / ticks;
                double inner = radius + thickness * 0.62 + (major ? 2 : 4);
                double outer = inner + (major ? 9 : 5);
                var pen = new Pen(major ? TickMajorBrush : TickBrush, major ? 1.6 : 1.0);
                dc.DrawLine(pen, PointOn(center, inner, angle), PointOn(center, outer, angle));
            }
        }

        // --- setpoint marker -------------------------------------------
        if (!double.IsNaN(Setpoint) && Setpoint >= Minimum && Setpoint <= Maximum)
        {
            double sf = (Setpoint - Minimum) / Math.Max(0.0001, Maximum - Minimum);
            double angle = StartAngle - Sweep * sf;
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)), 2.4);
            dc.DrawLine(pen, PointOn(center, radius - thickness * 0.85, angle), PointOn(center, radius + thickness * 0.85, angle));
        }

        // --- min / max ---------------------------------------------------
        DrawText(dc, $"{Minimum:0}", 10.5, TextMuted, FaceNumericLight, dpi,
            PointOn(center, radius + thickness + 16, StartAngle), TextAlign.Center);
        DrawText(dc, $"{Maximum:0}", 10.5, TextMuted, FaceNumericLight, dpi,
            PointOn(center, radius + thickness + 16, StartAngle - Sweep), TextAlign.Center);

        // --- needle ------------------------------------------------------
        double needleAngle = StartAngle - Sweep * Fraction;
        var needleColor = new SolidColorBrush(state);
        var needlePen = new Pen(needleColor, Math.Max(2.0, thickness * 0.24))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        dc.DrawLine(needlePen, PointOn(center, radius * 0.10, needleAngle + 180), PointOn(center, radius * 0.80, needleAngle));
        dc.DrawEllipse(needleColor, new Pen(new SolidColorBrush(Color.FromRgb(0x08, 0x11, 0x1E)), 2), center, thickness * 0.34, thickness * 0.34);

        // --- digital readout --------------------------------------------
        string value = Displayed.ToString(ValueFormat, CultureInfo.InvariantCulture);
        double valueSize = Math.Clamp(radius * 0.46, 17, 44);
        DrawText(dc, value, valueSize, TextBright, FaceNumeric, dpi,
            new Point(center.X, center.Y - radius * 0.30), TextAlign.Center);

        if (!string.IsNullOrEmpty(Unit))
        {
            DrawText(dc, Unit, Math.Clamp(valueSize * 0.38, 10, 15), TextDim, FaceBold, dpi,
                new Point(center.X, center.Y + radius * 0.02), TextAlign.Center);
        }

        // --- state pill ---------------------------------------------------
        var stateBrush = new SolidColorBrush(state);
        string stateText = StateText;
        var stateFt = new FormattedText(stateText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, FaceBold, 10.5, stateBrush, dpi);
        var pillRect = new Rect(
            center.X - stateFt.Width / 2 - 9,
            center.Y - radius * 0.72 - stateFt.Height / 2 - 3,
            stateFt.Width + 18,
            stateFt.Height + 6);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(Running && Displayed >= WarningAt ? 40 + 30 * pulse : 26), state.R, state.G, state.B)),
            new Pen(new SolidColorBrush(Color.FromArgb(140, state.R, state.G, state.B)), 1), pillRect, 4, 4);
        dc.DrawText(stateFt, new Point(pillRect.X + 9, pillRect.Y + 3));

        // --- percent label bottom ----------------------------------------
        DrawText(dc, $"{Fraction * 100:0} % of range", 10, TextMuted, FaceNumericLight, dpi,
            new Point(center.X, center.Y + radius * 0.62), TextAlign.Center);
    }

    private enum TextAlign { Left, Center }

    private static void DrawText(DrawingContext dc, string text, double size, Brush brush, Typeface face, double dpi, Point anchor, TextAlign align)
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

    private static Geometry BuildArc(Point center, double radius, double startAngle, double sweep)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            Point start = PointOn(center, radius, startAngle);
            Point end = PointOn(center, radius, startAngle + sweep);
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, Math.Abs(sweep) > 180,
                sweep >= 0 ? SweepDirection.Counterclockwise : SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }
}
