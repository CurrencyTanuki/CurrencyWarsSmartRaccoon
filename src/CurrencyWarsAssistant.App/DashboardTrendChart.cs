using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace CurrencyWarsAssistant.App;

public sealed class DashboardTrendChart : FrameworkElement
{
    private const double LeftInset = 36;
    private const double TopInset = 6;
    private const double RightInset = 7;
    private const double BottomInset = 17;
    private static readonly FontFamily ChartFont = new("Segoe UI");
    private static readonly Regex NumberPattern = new(
        @"[-+]?\d+(?:\.\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable),
            typeof(DashboardTrendChart),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnItemsSourceChanged));

    public static readonly DependencyProperty MetricProperty =
        DependencyProperty.Register(
            nameof(Metric),
            typeof(string),
            typeof(DashboardTrendChart),
            new FrameworkPropertyMetadata(
                "FinalDamage",
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ScaleLabelProperty =
        DependencyProperty.Register(
            nameof(ScaleLabel),
            typeof(string),
            typeof(DashboardTrendChart),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(
            nameof(Stroke),
            typeof(Brush),
            typeof(DashboardTrendChart),
            new FrameworkPropertyMetadata(
                Brushes.DeepSkyBlue,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridStrokeProperty =
        DependencyProperty.Register(
            nameof(GridStroke),
            typeof(Brush),
            typeof(DashboardTrendChart),
            new FrameworkPropertyMetadata(
                new SolidColorBrush(Color.FromArgb(72, 151, 169, 188)),
                FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string Metric
    {
        get => (string)GetValue(MetricProperty);
        set => SetValue(MetricProperty, value);
    }

    public string ScaleLabel
    {
        get => (string)GetValue(ScaleLabelProperty);
        set => SetValue(ScaleLabelProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush GridStroke
    {
        get => (Brush)GetValue(GridStrokeProperty);
        set => SetValue(GridStrokeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var itemCount = ItemsSource?.Cast<object>().Count() ?? 0;
        var desiredWidth = LeftInset + RightInset + Math.Max(220, itemCount * 42d);
        var desiredHeight = double.IsFinite(availableSize.Height)
            ? availableSize.Height
            : 120;
        return new Size(desiredWidth, Math.Max(48, desiredHeight));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var plotWidth = Math.Max(0, ActualWidth - LeftInset - RightInset);
        var plotHeight = Math.Max(0, ActualHeight - TopInset - BottomInset);
        if (plotWidth < 20 || plotHeight < 16)
        {
            return;
        }

        var rows = ItemsSource?.Cast<object>().ToArray() ?? [];
        if (rows.Length == 0)
        {
            DrawEmptyState(drawingContext);
            return;
        }

        var values = new double?[rows.Length];
        var isLogarithmic = UsesLogarithmicScale();
        for (var index = 0; index < rows.Length; index++)
        {
            values[index] = ReadMetricValue(rows[index]);
            if (isLogarithmic && values[index] is <= 0)
            {
                values[index] = null;
            }
        }

        var transformed = values
            .Where(value => value.HasValue)
            .Select(value => Transform(value!.Value, isLogarithmic))
            .ToArray();
        if (transformed.Length == 0)
        {
            DrawGrid(drawingContext, plotWidth, plotHeight, 0, 1, isLogarithmic);
            DrawEmptyState(drawingContext);
            DrawNodeLabels(drawingContext, rows, plotWidth);
            return;
        }

        var minimum = transformed.Min();
        var maximum = transformed.Max();
        ExpandRange(ref minimum, ref maximum);
        if (!isLogarithmic)
        {
            // Every dashboard metric is intrinsically non-negative. Range
            // padding is visual only and must never invent negative damage,
            // gold or action tick labels.
            minimum = Math.Max(0, minimum);
        }
        if (!isLogarithmic && UsesIntegerAxis())
        {
            minimum = Math.Floor(minimum);
            maximum = Math.Ceiling(maximum);
            if (maximum <= minimum)
            {
                maximum = minimum + 1;
            }
        }

        DrawGrid(
            drawingContext,
            plotWidth,
            plotHeight,
            minimum,
            maximum,
            isLogarithmic);
        DrawSeries(
            drawingContext,
            values,
            plotWidth,
            plotHeight,
            minimum,
            maximum,
            isLogarithmic);
        DrawNodeLabels(drawingContext, rows, plotWidth);
    }

    private static void OnItemsSourceChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        var chart = (DashboardTrendChart)dependencyObject;
        if (eventArgs.OldValue is INotifyCollectionChanged oldCollection)
        {
            CollectionChangedEventManager.RemoveHandler(
                oldCollection,
                chart.OnCollectionChanged);
        }

        if (eventArgs.NewValue is INotifyCollectionChanged newCollection)
        {
            CollectionChangedEventManager.AddHandler(
                newCollection,
                chart.OnCollectionChanged);
        }

        chart.InvalidateMeasure();
        chart.InvalidateVisual();
    }

    private void OnCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs eventArgs)
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void DrawGrid(
        DrawingContext drawingContext,
        double plotWidth,
        double plotHeight,
        double minimum,
        double maximum,
        bool isLogarithmic)
    {
        var gridPen = new Pen(GridStroke, 0.75)
        {
            DashStyle = new DashStyle([2, 3], 0)
        };
        var axisPen = new Pen(GridStroke, 1);
        drawingContext.DrawLine(
            axisPen,
            new Point(LeftInset, TopInset),
            new Point(LeftInset, TopInset + plotHeight));
        drawingContext.DrawLine(
            axisPen,
            new Point(LeftInset, TopInset + plotHeight),
            new Point(LeftInset + plotWidth, TopInset + plotHeight));

        // The dashboard uses four vertically stacked mini charts. Three labels
        // keep the y axis readable without sacrificing the chart's trend shape.
        var gridLineCount = UsesIntegerAxis() && maximum - minimum < 2
            ? 2
            : 3;
        for (var index = 0; index < gridLineCount; index++)
        {
            var ratio = index / (double)(gridLineCount - 1);
            var y = TopInset + (plotHeight * ratio);
            drawingContext.DrawLine(
                gridPen,
                new Point(LeftInset, y),
                new Point(LeftInset + plotWidth, y));

            var transformedValue = maximum - ((maximum - minimum) * ratio);
            var displayValue = isLogarithmic
                ? Math.Pow(10, transformedValue)
                : transformedValue;
            DrawText(
                drawingContext,
                FormatAxisValue(displayValue, UsesIntegerAxis()),
                new Point(0, y - 6),
                8,
                new SolidColorBrush(Color.FromRgb(166, 181, 198)),
                LeftInset - 4,
                TextAlignment.Right);
        }
    }

    private void DrawSeries(
        DrawingContext drawingContext,
        IReadOnlyList<double?> values,
        double plotWidth,
        double plotHeight,
        double minimum,
        double maximum,
        bool isLogarithmic)
    {
        var linePen = new Pen(Stroke, 1.7);
        Point? previousPoint = null;
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (!value.HasValue)
            {
                previousPoint = null;
                continue;
            }

            var transformedValue = Transform(value.Value, isLogarithmic);
            var x = LeftInset + GetHorizontalOffset(index, values.Count, plotWidth);
            var yRatio = (transformedValue - minimum) / (maximum - minimum);
            var point = new Point(
                x,
                TopInset + plotHeight - (yRatio * plotHeight));
            if (previousPoint.HasValue)
            {
                drawingContext.DrawLine(linePen, previousPoint.Value, point);
            }

            drawingContext.DrawEllipse(Stroke, null, point, 2.6, 2.6);
            previousPoint = point;
        }
    }

    private void DrawNodeLabels(
        DrawingContext drawingContext,
        IReadOnlyList<object> rows,
        double plotWidth)
    {
        var labelStride = Math.Max(1, (int)Math.Ceiling(rows.Count / 6d));
        for (var index = 0; index < rows.Count; index++)
        {
            if (index % labelStride != 0 && index != rows.Count - 1)
            {
                continue;
            }

            var nodeId = ReadProperty(rows[index], "NodeId")?.ToString();
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                continue;
            }

            var x = LeftInset + GetHorizontalOffset(index, rows.Count, plotWidth);
            DrawText(
                drawingContext,
                nodeId,
                new Point(x - 14, ActualHeight - BottomInset + 2),
                7.5,
                new SolidColorBrush(Color.FromRgb(191, 204, 218)),
                28,
                TextAlignment.Center);
        }
    }

    private void DrawEmptyState(DrawingContext drawingContext)
    {
        DrawText(
            drawingContext,
            "暂无有效数据",
            new Point(LeftInset + 8, Math.Max(8, ActualHeight / 2 - 7)),
            9,
            new SolidColorBrush(Color.FromRgb(137, 153, 170)),
            Math.Max(0, ActualWidth - LeftInset - RightInset - 16),
            TextAlignment.Center);
    }

    private void DrawText(
        DrawingContext drawingContext,
        string text,
        Point origin,
        double fontSize,
        Brush foreground,
        double width,
        TextAlignment alignment)
    {
        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(ChartFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            fontSize,
            foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, width),
            MaxTextHeight = fontSize + 4,
            TextAlignment = alignment,
            Trimming = TextTrimming.CharacterEllipsis
        };
        drawingContext.DrawText(formattedText, origin);
    }

    private double? ReadMetricValue(object row)
    {
        if (ExcludesRewardNodes() && ReadBooleanProperty(row, "IsRewardNode"))
        {
            return null;
        }

        var propertyNames = Metric switch
        {
            "RemainingAction" => new[]
            {
                "RemainingActionValue",
                "RemainingAction",
                "RemainingActionDisplay"
            },
            "AbsoluteGold" => new[]
            {
                "AbsoluteGold",
                "GoldAbsolute",
                "Gold",
                "AbsoluteGoldDisplay"
            },
            "TheoreticalDamage" => new[]
            {
                "TheoreticalDamage",
                "TheoreticalDamageValue",
                "TheoreticalDamageDisplay"
            },
            _ => new[]
            {
                "FinalDamage",
                "FinalDamageValue",
                "Damage",
                "FinalDamageDisplay"
            }
        };

        foreach (var propertyName in propertyNames)
        {
            var value = ReadProperty(row, propertyName);
            if (TryConvertNumber(value, out var number))
            {
                return number;
            }
        }

        return null;
    }

    private bool ExcludesRewardNodes() =>
        Metric is "TheoreticalDamage";

    private bool UsesIntegerAxis() =>
        Metric is "RemainingAction" or "AbsoluteGold";

    private bool UsesLogarithmicScale()
    {
        if (Metric is "RemainingAction" or "AbsoluteGold")
        {
            return false;
        }

        return ScaleLabel.Contains("对数", StringComparison.Ordinal) ||
               ScaleLabel.Contains("log", StringComparison.OrdinalIgnoreCase);
    }

    private static object? ReadProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        return property?.GetValue(instance);
    }

    private static bool ReadBooleanProperty(object instance, string propertyName)
    {
        var value = ReadProperty(instance, propertyName);
        return value is bool result && result;
    }

    private static bool TryConvertNumber(object? value, out double number)
    {
        switch (value)
        {
            case null:
                number = 0;
                return false;
            case byte byteValue:
                number = byteValue;
                return true;
            case short shortValue:
                number = shortValue;
                return true;
            case int intValue:
                number = intValue;
                return true;
            case long longValue:
                number = longValue;
                return true;
            case float floatValue when float.IsFinite(floatValue):
                number = floatValue;
                return true;
            case double doubleValue when double.IsFinite(doubleValue):
                number = doubleValue;
                return true;
            case decimal decimalValue:
                number = (double)decimalValue;
                return true;
            case string text:
                return TryParseDisplayNumber(text, out number);
            default:
                number = 0;
                return false;
        }
    }

    private static bool TryParseDisplayNumber(string text, out double number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(text) ||
            text.Contains('—') ||
            text.Contains("未知", StringComparison.Ordinal) ||
            text.Contains("暂不可见", StringComparison.Ordinal) ||
            text.Contains("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = text.Replace(",", string.Empty, StringComparison.Ordinal);
        var match = NumberPattern.Match(normalized);
        if (!match.Success ||
            !double.TryParse(
                match.Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out number))
        {
            number = 0;
            return false;
        }

        if (normalized.Contains('亿'))
        {
            number *= 100_000_000d;
        }
        else if (normalized.Contains('万'))
        {
            number *= 10_000d;
        }
        else if (normalized.Contains('千'))
        {
            number *= 1_000d;
        }

        return double.IsFinite(number);
    }

    private static double Transform(double value, bool isLogarithmic) =>
        isLogarithmic ? Math.Log10(value) : value;

    private static void ExpandRange(ref double minimum, ref double maximum)
    {
        if (Math.Abs(maximum - minimum) < 0.000_001)
        {
            var padding = Math.Max(Math.Abs(maximum) * 0.1, 1);
            minimum -= padding;
            maximum += padding;
            return;
        }

        var rangePadding = (maximum - minimum) * 0.08;
        minimum -= rangePadding;
        maximum += rangePadding;
    }

    private static double GetHorizontalOffset(
        int index,
        int count,
        double plotWidth) =>
        count <= 1 ? plotWidth / 2 : plotWidth * index / (count - 1d);

    private static string FormatAxisValue(double value, bool integerOnly)
    {
        if (integerOnly)
        {
            return Math.Round(value, MidpointRounding.AwayFromZero)
                .ToString("0", CultureInfo.InvariantCulture);
        }

        var absoluteValue = Math.Abs(value);
        return absoluteValue switch
        {
            >= 100_000_000 => $"{value / 100_000_000d:0.##}亿",
            >= 10_000 => $"{value / 10_000d:0.##}万",
            >= 1_000 => $"{value / 1_000d:0.#}千",
            _ => $"{value:0.#}"
        };
    }
}
