using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System;

namespace CapstoneController.Controls;

public class CircularDialControl : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<CircularDialControl, double>(
            nameof(Value),
            defaultValue: 0d,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> FineTuneRangeProperty =
        AvaloniaProperty.Register<CircularDialControl, double>(nameof(FineTuneRange), 20d);

    public static readonly StyledProperty<double> StepProperty =
        AvaloniaProperty.Register<CircularDialControl, double>(nameof(Step), 0.1d);

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double FineTuneRange
    {
        get => GetValue(FineTuneRangeProperty);
        set => SetValue(FineTuneRangeProperty, value);
    }

    public double Step
    {
        get => GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    static CircularDialControl()
    {
        AffectsRender<CircularDialControl>(ValueProperty, FineTuneRangeProperty, StepProperty);
    }

    private bool _isDragging;
    private double _baseValue;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _baseValue = Value;
        _isDragging = true;
        e.Pointer.Capture(this);
        UpdateFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_isDragging)
            return;

        UpdateFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!_isDragging)
            return;

        _isDragging = false;
        // Re-center the jog dial after interaction.
        _baseValue = Value;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var delta = e.Delta.Y;
        if (Math.Abs(delta) < double.Epsilon)
            return;

        var step = Step;
        if (step <= 0)
            step = 0.1;

        var next = Value + (delta > 0 ? step : -step);
        SetCurrentValue(ValueProperty, Snap(next));
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // When not actively dragging, keep the dial centered (pointing up)
        // around the current value.
        if (!_isDragging)
            _baseValue = Value;

        var bounds = Bounds;
        var size = Math.Min(bounds.Width, bounds.Height);
        if (size <= 0)
            return;

        var center = bounds.Center;
        var radius = size * 0.48;
        var ringThickness = Math.Max(2, size * 0.06);

        var borderBrush = TryFindBrush("PanelBorderBrush") ?? Brushes.Gray;
        var accentBrush = TryFindBrush("AccentBrush") ?? Brushes.White;
        var backgroundBrush = TryFindBrush("InputBackgroundBrush") ?? Brushes.Black;

        var circleRect = new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2);

        // Base fill + ring
        context.DrawEllipse(backgroundBrush, null, center, radius, radius);
        context.DrawEllipse(null, new Pen(borderBrush, ringThickness), center, radius - ringThickness * 0.5, radius - ringThickness * 0.5);

        // Indicator
        var range = FineTuneRange;
        if (double.IsNaN(range) || double.IsInfinity(range) || range <= 0)
            range = 20;

        // Map base-range..base+range to [0..1]
        var min = _baseValue - range;
        var max = _baseValue + range;
        var t = (Value - min) / (max - min);
        t = Math.Clamp(t, 0, 1);

        // Sweep from left (−) to right (+) passing through up at center.
        // left  = 180°
        // center= 270° (up)
        // right = 360°
        var startAngle = DegreesToRadians(180);
        var endAngle = DegreesToRadians(360);
        var angle = startAngle + (endAngle - startAngle) * t;

        var inner = radius * 0.2;
        var outer = radius * 0.85;
        var p1 = new Point(center.X + Math.Cos(angle) * inner, center.Y + Math.Sin(angle) * inner);
        var p2 = new Point(center.X + Math.Cos(angle) * outer, center.Y + Math.Sin(angle) * outer);

        context.DrawLine(new Pen(accentBrush, Math.Max(2, size * 0.045), lineCap: PenLineCap.Round), p1, p2);

        // Center dot
        context.DrawEllipse(accentBrush, null, center, Math.Max(2, size * 0.04), Math.Max(2, size * 0.04));
    }

    private void UpdateFromPoint(Point p)
    {
        var bounds = Bounds;
        var center = bounds.Center;
        var dx = p.X - center.X;
        var dy = p.Y - center.Y;

        // Convert to angle in radians [-PI..PI]
        var angle = Math.Atan2(dy, dx);

        // Normalize to [0..2PI)
        if (angle < 0)
            angle += Math.PI * 2;

        // Sweep from 180° (left) to 360° (right) passing through 270° (up)
        var start = DegreesToRadians(180);
        var end = DegreesToRadians(360);
        var clampedAngle = Math.Clamp(angle, start, end);
        var t = (clampedAngle - start) / (end - start);

        var range = FineTuneRange;
        if (double.IsNaN(range) || double.IsInfinity(range) || range <= 0)
            range = 20;

        var raw = (_baseValue - range) + t * (range * 2);
        var snapped = Snap(raw);
        SetCurrentValue(ValueProperty, snapped);
    }

    private double Snap(double value)
    {
        var step = Step;
        if (step <= 0)
            return value;

        return Math.Round(value / step) * step;
    }

    private IBrush? TryFindBrush(string resourceKey)
    {
        if (Application.Current?.TryFindResource(resourceKey, out var value) == true && value is IBrush brush)
            return brush;

        return null;
    }

    private static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180d);
}
