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

    /// <summary>
    /// 只画 Y 轴刻度与网格（不画曲线/数据点/X 轴标签）。
    /// 用于悬浮看板图表左侧的固定刻度列（P0-4：图表主体可横向滚动，
    /// 但 Y 轴数值必须始终可见——用户 2026-08-06 拍板保留自动滚最右）。
    /// </summary>
    public static readonly DependencyProperty OnlyYAxisProperty =
        DependencyProperty.Register(
            nameof(OnlyYAxis),
            typeof(bool),
            typeof(DashboardTrendChart),
            new FrameworkPropertyMetadata(
                false,
                FrameworkPropertyMetadataOptions.AffectsMeasure |
                FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// 主图表是否绘制左侧 Y 轴刻度文字。当左侧有固定 OnlyYAxis 刻度列时
    /// 置 false，避免刻度重复绘制（P0-4）。
    /// </summary>
    public static readonly DependencyProperty ShowYAxisProperty =
        DependencyProperty.Register(
            nameof(ShowYAxis),
            typeof(bool),
            typeof(DashboardTrendChart),
            new FrameworkPropertyMetadata(
                true,
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

    public bool OnlyYAxis
    {
        get => (bool)GetValue(OnlyYAxisProperty);
        set => SetValue(OnlyYAxisProperty, value);
    }

    public bool ShowYAxis
    {
        get => (bool)GetValue(ShowYAxisProperty);
        set => SetValue(ShowYAxisProperty, value);
    }

    public Brush GridStroke
    {
        get => (Brush)GetValue(GridStrokeProperty);
        set => SetValue(GridStrokeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (OnlyYAxis)
        {
            // 固定刻度列：只容纳 Y 轴刻度区（LeftInset）+ 少量右边距。
            var axisDesiredHeight = double.IsFinite(availableSize.Height)
                ? availableSize.Height
                : 120;
            return new Size(LeftInset + 4, Math.Max(48, axisDesiredHeight));
        }

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
        // 固定刻度列（OnlyYAxis）宽度只容纳 Y 轴刻度区（≈40px），
        // plotWidth 必然 < 20——不能按主图标准早退，否则刻度列永远空白
        //（P0-4 review 抓到）。刻度列只画网格线+刻度文字，不需要横向绘图区。
        if (!OnlyYAxis && (plotWidth < 20 || plotHeight < 16))
        {
            return;
        }

        if (OnlyYAxis && plotHeight < 16)
        {
            return;
        }

        var rows = ItemsSource?.Cast<object>().ToArray() ?? [];
        if (rows.Length == 0)
        {
            if (OnlyYAxis)
            {
                // 刻度列无数据时仍画出 Y 轴骨架（网格+刻度），避免空白。
                DrawGridOnly(
                    drawingContext,
                    plotWidth,
                    plotHeight,
                    minimum: 0,
                    maximum: 1,
                    isLogarithmic: false);
                return;
            }

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

        // 奖励节点（1-8/2-6/3-6）保留数据点，但它们的值不参与 Y 轴范围
        // （奖励关敌人血量低，最终伤害失真会压扁图线）。
        var transformed = values
            .Where((value, index) =>
                value.HasValue && !IsRewardNodeAt(rows, index))
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

        if (OnlyYAxis)
        {
            // 固定刻度列：只画网格 + Y 轴刻度文字（与主图同一数据范围），
            // 不画曲线/数据点/X 轴标签。主图 ShowYAxis=false 时不再重复画刻度。
            DrawGridOnly(
                drawingContext,
                plotWidth,
                plotHeight,
                minimum,
                maximum,
                isLogarithmic);
            return;
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
            rows,
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

        DrawGridLinesAndLabels(
            drawingContext,
            plotWidth,
            plotHeight,
            minimum,
            maximum,
            isLogarithmic,
            drawLabels: ShowYAxis);
    }

    /// <summary>
    /// 固定刻度列专用（OnlyYAxis）：只画网格 + Y 轴刻度文字。
    /// 刻度列宽度只容纳 LeftInset，因此横网格线画到刻度列右缘即可，
    /// 与主图网格线在视觉上衔接（主图从 LeftInset 起画）。
    /// </summary>
    private void DrawGridOnly(
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
            new Point(0, TopInset + plotHeight),
            new Point(LeftInset, TopInset + plotHeight));

        DrawGridLinesAndLabels(
            drawingContext,
            plotWidth,
            plotHeight,
            minimum,
            maximum,
            isLogarithmic,
            drawLabels: true);
    }

    private void DrawGridLinesAndLabels(
        DrawingContext drawingContext,
        double plotWidth,
        double plotHeight,
        double minimum,
        double maximum,
        bool isLogarithmic,
        bool drawLabels)
    {
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
                new Pen(GridStroke, 0.75)
                {
                    DashStyle = new DashStyle([2, 3], 0)
                },
                new Point(LeftInset, y),
                new Point(LeftInset + plotWidth, y));

            if (!drawLabels)
            {
                continue;
            }

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
        IReadOnlyList<object> rows,
        double plotWidth,
        double plotHeight,
        double minimum,
        double maximum,
        bool isLogarithmic)
    {
        var linePen = new Pen(Stroke, 1.7);
        Point? previousConnectablePoint = null;
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            var isRewardNode = IsRewardNodeAt(rows, index);
            if (!value.HasValue)
            {
                // 奖励节点（1-8/2-6/3-6）在理论极限/行动值图中可能没有记录
                //（值为 null）。它本来就不参与连线，不应清空连线锚点——
                // 否则 1-7→1-9 会被 1-8 的 null 值打断（用户 2026-08-06 实测：
                // 理论极限/行动图 1-7、1-9 断开）。普通节点值缺失才断开。
                if (!isRewardNode)
                {
                    previousConnectablePoint = null;
                }

                continue;
            }

            var transformedValue = Transform(value.Value, isLogarithmic);
            var x = LeftInset + GetHorizontalOffset(index, values.Count, plotWidth);
            var yRatio = (transformedValue - minimum) / (maximum - minimum);
            var point = new Point(
                x,
                TopInset + plotHeight - (yRatio * plotHeight));
            // 奖励节点（1-8/2-6/3-6）画点但不断开连线：线从上一个正常节点
            // 直接连到下一个正常节点，奖励点只是孤立的格点（用户方案：
            // 2-5→2-7、1-7→1-9、3-7→3-9 直连，跳过奖励节点）。
            // 注意：奖励节点自身也不画线（避免 2-5→2-6 分叉）。
            if (!isRewardNode && previousConnectablePoint.HasValue)
            {
                drawingContext.DrawLine(linePen, previousConnectablePoint.Value, point);
            }

            drawingContext.DrawEllipse(Stroke, null, point, 2.6, 2.6);
            if (!isRewardNode)
            {
                previousConnectablePoint = point;
            }
        }
    }

    private void DrawNodeLabels(
        DrawingContext drawingContext,
        IReadOnlyList<object> rows,
        double plotWidth)
    {
        // 所有节点都画 X 轴标签（用户 2026-08-06 要求）：
        // 旧逻辑 labelStride=ceil(rows.Count/6) 抽样，节点较多时只画部分
        // index（如 8 个节点 stride=2 → 画 0,2,4,6 → 1-4、1-7 标签缺失）。
        // 图表主体可横向滚动（节点多时宽度自动拉开），不再抽样；
        // 1-5 等补给关无记录节点本就不在 rows 里，天然不出现。
        for (var index = 0; index < rows.Count; index++)
        {
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
        // 奖励节点（1-8/2-6/3-6）的值仍要读出来用于画点（格点保留），
        // 只是不参与连线与 Y 轴范围（在 DrawSeries/OnRender 处理）。
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

    /// <summary>
    /// 奖励关"不连线"判定（用户 2026-08-06 最终确认）：只有 1-8/2-6/3-6
    /// 敌人总血量低、最终伤害失真——图线保留数据点但不连线、不参与
    /// Y 轴范围；连线时跳过这些节点（1-7→1-9、2-5→2-7 等直连）。
    /// 1-1/1-2 虽是奖励关（标签语义，见 HistoricalDetailViewModels 的
    /// IsRewardNodeId = 5 个），但伤害正常，必须点格并连线。
    /// 这是按节点号硬编码的规则，不依赖页面识别（IsRewardNode 字段不可靠，
    /// 实测 1-2 漏标、1-3 误标）。
    /// </summary>
    private static bool IsRewardNodeAt(IReadOnlyList<object> rows, int index) =>
        index >= 0 &&
        index < rows.Count &&
        IsRewardNodeId(ReadProperty(rows[index], "NodeId")?.ToString());

    private static bool IsRewardNodeId(string? nodeId) => nodeId is
        "1-8" or "2-6" or "3-6";

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
