using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Cloris.Aion2Flow.Controls;

public sealed record DpsChartSeries(int CombatantId, IBrush Brush, double[] Values, bool IsHighlighted);

/// <summary>Lightweight line chart of rolling DPS per combatant.</summary>
public sealed class DpsChartView : Control
{
    public static readonly StyledProperty<IReadOnlyList<DpsChartSeries>?> SeriesProperty =
        AvaloniaProperty.Register<DpsChartView, IReadOnlyList<DpsChartSeries>?>(nameof(Series));

    public static readonly StyledProperty<double> MaxValueProperty =
        AvaloniaProperty.Register<DpsChartView, double>(nameof(MaxValue));

    private static readonly Pen GridPen = new(new ImmutableSolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), 1);

    static DpsChartView()
    {
        AffectsRender<DpsChartView>(SeriesProperty, MaxValueProperty);
    }

    public IReadOnlyList<DpsChartSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public double MaxValue
    {
        get => GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 2 || height <= 2)
            return;

        for (var i = 1; i <= 3; i++)
        {
            var y = Math.Round(height * i / 4d) + 0.5;
            context.DrawLine(GridPen, new Point(0, y), new Point(width, y));
        }

        var series = Series;
        var maxValue = MaxValue;
        if (series is null || series.Count == 0 || maxValue <= 0)
            return;

        var length = 0;
        foreach (var s in series)
            length = Math.Max(length, s.Values.Length);
        if (length < 2)
            return;

        var usableHeight = height - 2;
        var step = width / (length - 1);

        // Draw non-highlighted series first so the local player's line stays on top.
        foreach (var s in series)
        {
            if (!s.IsHighlighted)
                DrawSeries(context, s, step, usableHeight, maxValue);
        }

        foreach (var s in series)
        {
            if (s.IsHighlighted)
                DrawSeries(context, s, step, usableHeight, maxValue);
        }
    }

    private static void DrawSeries(DrawingContext context, DpsChartSeries series, double step, double usableHeight, double maxValue)
    {
        var values = series.Values;
        if (values.Length < 2)
            return;

        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            geometryContext.BeginFigure(ToPoint(0, values[0], step, usableHeight, maxValue), isFilled: false);
            for (var i = 1; i < values.Length; i++)
                geometryContext.LineTo(ToPoint(i, values[i], step, usableHeight, maxValue));
            geometryContext.EndFigure(isClosed: false);
        }

        var pen = new Pen(series.Brush, series.IsHighlighted ? 2.2 : 1.3, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
        context.DrawGeometry(null, pen, geometry);
    }

    private static Point ToPoint(int index, double value, double step, double usableHeight, double maxValue)
    {
        var ratio = Math.Clamp(value / maxValue, 0d, 1d);
        return new Point(index * step, 1 + usableHeight * (1 - ratio));
    }
}
