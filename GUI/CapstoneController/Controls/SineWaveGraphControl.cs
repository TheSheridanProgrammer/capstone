using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Media.TextFormatting;

namespace CapstoneController.Controls;

public sealed class SineWaveGraphControl : Control
{
    public static readonly StyledProperty<double> FrequencyHzProperty =
        AvaloniaProperty.Register<SineWaveGraphControl, double>(nameof(FrequencyHz));

    public static readonly StyledProperty<bool> IsAnimatedProperty =
        AvaloniaProperty.Register<SineWaveGraphControl, bool>(nameof(IsAnimated));

    public double FrequencyHz
    {
        get => GetValue(FrequencyHzProperty);
        set => SetValue(FrequencyHzProperty, value);
    }

    public bool IsAnimated
    {
        get => GetValue(IsAnimatedProperty);
        set => SetValue(IsAnimatedProperty, value);
    }

    private readonly DispatcherTimer _timer;
    private double _phaseOffset;

    static SineWaveGraphControl()
    {
        AffectsRender<SineWaveGraphControl>(FrequencyHzProperty, IsAnimatedProperty);

        IsAnimatedProperty.Changed.AddClassHandler<SineWaveGraphControl>((control, _) =>
        {
            control.UpdateTimerState();
        });
    }

    public SineWaveGraphControl()
    {
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };

        _timer.Tick += (_, _) =>
        {
            // Phase advance (keeps motion consistent with frequency).
            var frequencyHz = Math.Max(0, FrequencyHz);
            _phaseOffset += (Math.PI * 2) * frequencyHz * _timer.Interval.TotalSeconds;
            if (_phaseOffset > (Math.PI * 2))
                _phaseOffset %= (Math.PI * 2);

            InvalidateVisual();
        };

        UpdateTimerState();
    }

    private void UpdateTimerState()
    {
        if (IsAnimated)
            _timer.Start();
        else
            _timer.Stop();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        const double padding = 18;
        var plotRect = bounds.Deflate(padding);
        if (plotRect.Width <= 0 || plotRect.Height <= 0)
            return;

        var frameBrush = ResolveBrush(
            "SystemControlForegroundBaseHighBrush",
            "ThemeForegroundBrush");

        var accentBrush = ResolveBrush(
            "SystemAccentColor",
            "SystemAccentBrush",
            "SystemControlForegroundAccentBrush",
            "SystemControlHighlightAccentBrush") ?? frameBrush;

        frameBrush ??= Brushes.Gray;
        accentBrush ??= Brushes.Gray;

        var framePen = new Pen(frameBrush, 1);
        var axisPen = new Pen(frameBrush, 1);
        var gridPen = new Pen(WithOpacity(frameBrush, 0.25), 1);
        var tickPen = new Pen(WithOpacity(frameBrush, 0.7), 1);
        var wavePen = new Pen(accentBrush, 3, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        context.DrawRectangle(null, framePen, plotRect);

        var midY = plotRect.Top + (plotRect.Height / 2);
        var leftX = plotRect.Left;

        // Axes: Y axis at left edge, X axis at midline.
        context.DrawLine(axisPen, new Point(leftX, plotRect.Top), new Point(leftX, plotRect.Bottom));
        context.DrawLine(axisPen, new Point(plotRect.Left, midY), new Point(plotRect.Right, midY));

        // Grid
        const int vDivs = 10;
        const int hDivs = 4;
        for (var i = 1; i < vDivs; i++)
        {
            var x = plotRect.Left + (plotRect.Width * i / vDivs);
            context.DrawLine(gridPen, new Point(x, plotRect.Top), new Point(x, plotRect.Bottom));
        }
        for (var i = 1; i < hDivs; i++)
        {
            var y = plotRect.Top + (plotRect.Height * i / hDivs);
            context.DrawLine(gridPen, new Point(plotRect.Left, y), new Point(plotRect.Right, y));
        }

        var frequencyHz = Math.Max(0, FrequencyHz);

        // Pick a time window that keeps the waveform readable (roughly ~4 cycles on screen).
        var timeWindowSeconds = frequencyHz <= 0 ? 1.0 : Math.Clamp(4.0 / frequencyHz, 0.2, 2.0);
        var amplitude = plotRect.Height * 0.35;

        // Ticks + labels
        DrawXTicks(context, plotRect, midY, tickPen, frameBrush, timeWindowSeconds);
        DrawYTicks(context, plotRect, leftX, tickPen, frameBrush);

        var samples = (int)Math.Clamp(plotRect.Width * 1.2, 200, 900);

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (var i = 0; i < samples; i++)
            {
                var t = i / (double)(samples - 1);
                var x = plotRect.Left + (t * plotRect.Width);
                var timeSeconds = t * timeWindowSeconds;
                var phase = (Math.PI * 2) * frequencyHz * timeSeconds + _phaseOffset;
                var y = midY - (Math.Sin(phase) * amplitude);

                if (i == 0)
                    g.BeginFigure(new Point(x, y), isFilled: false);
                else
                    g.LineTo(new Point(x, y));
            }
        }

        context.DrawGeometry(null, wavePen, geometry);
    }

    private static void DrawXTicks(
        DrawingContext context,
        Rect plotRect,
        double midY,
        Pen tickPen,
        IBrush labelBrush,
        double timeWindowSeconds)
    {
        // 0%, 25%, 50%, 75%, 100%
        var fractions = new[] { 0.0, 0.25, 0.5, 0.75, 1.0 };
        foreach (var f in fractions)
        {
            var x = plotRect.Left + (plotRect.Width * f);
            context.DrawLine(tickPen, new Point(x, midY - 6), new Point(x, midY + 6));

            var value = timeWindowSeconds * f;
            DrawLabel(context, labelBrush, new Point(x - 10, plotRect.Bottom + 4), value.ToString("0.###", CultureInfo.InvariantCulture) + "s", 12);
        }

        DrawLabel(context, labelBrush, new Point(plotRect.Right - 72, plotRect.Bottom + 22), "Time", 12);
    }

    private static void DrawYTicks(
        DrawingContext context,
        Rect plotRect,
        double leftX,
        Pen tickPen,
        IBrush labelBrush)
    {
        // -1, 0, +1 labels (normalized amplitude)
        var midY = plotRect.Top + (plotRect.Height / 2);
        var topY = plotRect.Top + (plotRect.Height * 0.15);
        var bottomY = plotRect.Bottom - (plotRect.Height * 0.15);

        context.DrawLine(tickPen, new Point(leftX - 6, midY), new Point(leftX + 6, midY));
        context.DrawLine(tickPen, new Point(leftX - 6, topY), new Point(leftX + 6, topY));
        context.DrawLine(tickPen, new Point(leftX - 6, bottomY), new Point(leftX + 6, bottomY));

        DrawLabel(context, labelBrush, new Point(plotRect.Left + 6, topY - 8), "+1", 12);
        DrawLabel(context, labelBrush, new Point(plotRect.Left + 6, midY - 8), "0", 12);
        DrawLabel(context, labelBrush, new Point(plotRect.Left + 6, bottomY - 8), "-1", 12);

        DrawLabel(context, labelBrush, new Point(plotRect.Left + 6, plotRect.Top - 18), "Amplitude", 12);
    }

    private static void DrawLabel(DrawingContext context, IBrush brush, Point origin, string text, double fontSize)
    {
        var layout = new TextLayout(
            text,
            typeface: Typeface.Default,
            fontSize: fontSize,
            foreground: brush);

        layout.Draw(context, origin);
    }

    private static IBrush WithOpacity(IBrush brush, double opacity)
    {
        if (brush is ISolidColorBrush solid)
            return new SolidColorBrush(solid.Color, opacity);

        return brush;
    }

    private IBrush? ResolveBrush(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (this.TryFindResource(key, ThemeVariant.Default, out var value) && value is not null)
            {
                switch (value)
                {
                    case IBrush brush:
                        return brush;
                    case Color color:
                        return new SolidColorBrush(color);
                }
            }
        }

        return null;
    }
}
