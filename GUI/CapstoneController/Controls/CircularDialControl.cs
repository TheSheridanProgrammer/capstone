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

    public static readonly StyledProperty<double> StepProperty =
        AvaloniaProperty.Register<CircularDialControl, double>(nameof(Step), 0.1d);

    // Hz added or removed for one full 360° turn.
    public static readonly StyledProperty<double> HzPerTurnProperty =
        AvaloniaProperty.Register<CircularDialControl, double>(nameof(HzPerTurn), 100d);

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Step
    {
        get => GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public double HzPerTurn
    {
        get => GetValue(HzPerTurnProperty);
        set => SetValue(HzPerTurnProperty, value);
    }

    static CircularDialControl()
    {
        AffectsRender<CircularDialControl>(ValueProperty, StepProperty, HzPerTurnProperty);
    }

    private bool _isDragging;
    private double _dragStartValue;
    private double _previousPointerAngle;
    private double _accumulatedDragAngle;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var pos = e.GetPosition(this);

        _isDragging = true;
        _dragStartValue = Value;
        _previousPointerAngle = GetPointerAngleRadians(pos);
        _accumulatedDragAngle = 0;

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_isDragging)
            return;

        var pos = e.GetPosition(this);
        var currentAngle = GetPointerAngleRadians(pos);

        var delta = NormalizeAngleDelta(currentAngle - _previousPointerAngle);
        _previousPointerAngle = currentAngle;

        _accumulatedDragAngle += delta;

        var turns = _accumulatedDragAngle / (Math.PI * 2);
        var hzPerTurn = HzPerTurn;
        if (double.IsNaN(hzPerTurn) || double.IsInfinity(hzPerTurn) || hzPerTurn <= 0)
            hzPerTurn = 100;

        // Screen coordinates make clockwise come out positive with this setup.
        var rawValue = _dragStartValue + (turns * hzPerTurn);
        SetCurrentValue(ValueProperty, Snap(Math.Max(0, rawValue)));

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!_isDragging)
            return;

        _isDragging = false;
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
        SetCurrentValue(ValueProperty, Snap(Math.Max(0, next)));
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        var size = Math.Min(bounds.Width, bounds.Height);
        if (size <= 0)
            return;

        var center = bounds.Center;
        var radius = size * 0.47;

        var outerRingThickness = Math.Max(2, size * 0.045);
        var innerRingThickness = Math.Max(1, size * 0.012);

        var borderBrush = TryFindBrush("PanelBorderBrush") ?? Brushes.Gray;
        var accentBrush = TryFindBrush("AccentBrush") ?? Brushes.DodgerBlue;
        var backgroundBrush = TryFindBrush("InputBackgroundBrush") ?? Brushes.Black;
        var highlightBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
        var tickBrush = TryFindBrush("TextSecondaryBrush") ?? Brushes.DarkGray;

        // Main body
        context.DrawEllipse(backgroundBrush, null, center, radius, radius);

        // Outer ring
        context.DrawEllipse(
            null,
            new Pen(borderBrush, outerRingThickness),
            center,
            radius - outerRingThickness * 0.5,
            radius - outerRingThickness * 0.5);

        // Inner subtle ring
        context.DrawEllipse(
            null,
            new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), innerRingThickness),
            center,
            radius * 0.72,
            radius * 0.72);

        // Soft top highlight
        var highlightRect = new Rect(center.X - radius * 0.72, center.Y - radius * 0.72, radius * 1.44, radius * 0.72);
        context.DrawEllipse(highlightBrush, null,
            new Point(highlightRect.Center.X, highlightRect.Center.Y),
            highlightRect.Width / 2,
            highlightRect.Height / 2);

        // 12 small ticks around the dial
        for (int i = 0; i < 12; i++)
        {
            var angle = (-Math.PI / 2) + (i * (Math.PI * 2 / 12.0));
            var tickOuter = radius * 0.88;
            var tickInner = i % 3 == 0 ? radius * 0.72 : radius * 0.78;

            var p1 = new Point(
                center.X + Math.Cos(angle) * tickInner,
                center.Y + Math.Sin(angle) * tickInner);

            var p2 = new Point(
                center.X + Math.Cos(angle) * tickOuter,
                center.Y + Math.Sin(angle) * tickOuter);

            context.DrawLine(new Pen(tickBrush, i % 3 == 0 ? 2 : 1), p1, p2);
        }

        // Pointer angle:
        // 12 o'clock is the baseline, and every full turn corresponds to HzPerTurn.
        var hzPerTurn = HzPerTurn;
        if (double.IsNaN(hzPerTurn) || double.IsInfinity(hzPerTurn) || hzPerTurn <= 0)
            hzPerTurn = 100;

        var turnsFromZero = Value / hzPerTurn;
        var pointerAngle = (-Math.PI / 2) + (turnsFromZero * Math.PI * 2);

        var pointerStart = new Point(
            center.X + Math.Cos(pointerAngle) * (radius * 0.18),
            center.Y + Math.Sin(pointerAngle) * (radius * 0.18));

        var pointerEnd = new Point(
            center.X + Math.Cos(pointerAngle) * (radius * 0.72),
            center.Y + Math.Sin(pointerAngle) * (radius * 0.72));

        // Pointer glow underlay
        context.DrawLine(
            new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), Math.Max(4, size * 0.06), lineCap: PenLineCap.Round),
            pointerStart,
            pointerEnd);

        // Pointer
        context.DrawLine(
            new Pen(accentBrush, Math.Max(3, size * 0.038), lineCap: PenLineCap.Round),
            pointerStart,
            pointerEnd);

        // Center cap
        context.DrawEllipse(borderBrush, null, center, radius * 0.09, radius * 0.09);
        context.DrawEllipse(accentBrush, null, center, radius * 0.045, radius * 0.045);
    }

    private double GetPointerAngleRadians(Point p)
    {
        var center = Bounds.Center;
        var dx = p.X - center.X;
        var dy = p.Y - center.Y;
        return Math.Atan2(dy, dx);
    }

    private static double NormalizeAngleDelta(double delta)
    {
        while (delta > Math.PI)
            delta -= Math.PI * 2;

        while (delta < -Math.PI)
            delta += Math.PI * 2;

        return delta;
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
}