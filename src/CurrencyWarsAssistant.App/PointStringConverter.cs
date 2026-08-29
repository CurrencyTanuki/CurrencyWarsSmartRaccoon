using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CurrencyWarsAssistant.App;

/// <summary>
/// 把 "x,y x,y …" 格式的点串转换为 WPF PointCollection，
/// 供历史对局窗口的折线图绑定使用（空串/无效输入返回空集合）。
/// </summary>
public sealed class PointStringConverter : IValueConverter
{
    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        if (value is not string points || string.IsNullOrWhiteSpace(points))
        {
            return new PointCollection();
        }

        var collection = new PointCollection();
        foreach (var pair in points.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(',');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                collection.Add(new Point(x, y));
            }
        }

        return collection;
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
