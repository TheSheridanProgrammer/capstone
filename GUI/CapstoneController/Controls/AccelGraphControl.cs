using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;

namespace CapstoneController.Controls;

public sealed class AccelGraphControl : Control
{
    public static readonly StyledProperty<double[]?> SamplesProperty =
        AvaloniaProperty.Register<AccelGraphControl, double[]?>(nameof(Samples));

    public static readonly StyledProperty<double[]?> FitSamplesProperty =
        AvaloniaProperty.Register<AccelGraphControl, double[]?>(nameof(FitSamples));

    public static readonly StyledProperty<bool> ShowFitProperty =
        AvaloniaProperty.Register<AccelGraphControl, bool>(nameof(ShowFit), true);

    public static readonly StyledProperty<string> YLabelProperty =
        AvaloniaProperty.Register<AccelGraphControl, string>(nameof(YLabel), "Accel Y");

    public double[]? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public double[]? FitSamples
    {
        get => GetValue(FitSamplesProperty);
        set => SetValue(FitSamplesProperty, value);
    }

    public bool ShowFit
    {
        get => GetValue(ShowFitProperty);
        set => SetValue(ShowFitProperty, value);
    }

    public string YLabel
    {
        get => GetValue(YLabelProperty);
        set => SetValue(YLabelProperty, value);
    }

    static AccelGraphControl()
    {
        AffectsRender<AccelGraphControl>(SamplesProperty, FitSamplesProperty, ShowFitProperty, YLabelProperty);
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
            "ThemeForegroundBrush") ?? Brushes.Gray;

        var accentBrush = ResolveBrush(
            "SystemAccentColor",
            "SystemAccentBrush",
            "SystemControlForegroundAccentBrush",
            "SystemControlHighlightAccentBrush") ?? frameBrush;

        var framePen = new Pen(frameBrush, 1);
        var gridPen = new Pen(WithOpacity(frameBrush, 0.18), 1);
        var wavePen = new Pen(accentBrush, 3, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var fitPen = new Pen(WithOpacity(frameBrush, 0.85), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        context.DrawRectangle(null, framePen, plotRect);

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

        var samples = Samples;
        if (samples is null || samples.Length < 2)
        {
            DrawLabel(context, WithOpacity(frameBrush, 0.8), new Point(plotRect.Left + 8, plotRect.Top + 8), "No accel samples", 14);
            return;
        }

        // Normalize to fit vertical range.
        var min = samples[0];
        var max = samples[0];
        for (var i = 1; i < samples.Length; i++)
        {
            var v = samples[i];
            if (v < min) min = v;
            if (v > max) max = v;
        }

        var span = max - min;
        if (!(span > 1e-9))
            span = 1.0;

        double MapY(double v)
        {
            var t = (v - min) / span;
            return plotRect.Bottom - (t * plotRect.Height);
        }

        var geometry = BuildLineGeometry(plotRect, samples, MapY);
        context.DrawGeometry(null, wavePen, geometry);

        if (ShowFit && FitSamples is { Length: > 1 } fit && fit.Length == samples.Length)
        {
            var fitGeo = BuildLineGeometry(plotRect, fit, MapY);
            context.DrawGeometry(null, fitPen, fitGeo);
        }

        DrawLabel(context, WithOpacity(frameBrush, 0.85), new Point(plotRect.Left + 8, plotRect.Top - 18), YLabel, 12);
        DrawLabel(context, WithOpacity(frameBrush, 0.75), new Point(plotRect.Right - 130, plotRect.Bottom + 6), $"min={min:0.##} max={max:0.##}", 12);
    }

    private static StreamGeometry BuildLineGeometry(Rect plotRect, double[] values, Func<double, double> mapY)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (var i = 0; i < values.Length; i++)
            {
                var t = i / (double)(values.Length - 1);
                var x = plotRect.Left + (t * plotRect.Width);
                var y = mapY(values[i]);

                if (i == 0)
                    g.BeginFigure(new Point(x, y), isFilled: false);
                else
                    g.LineTo(new Point(x, y));
            }
        }

        return geometry;
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
