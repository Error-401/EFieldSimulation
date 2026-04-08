using System.Globalization;
using System.Windows.Data;

namespace EFieldSimulation.Converters;

/// <summary>
/// Converts a Count to a pixel width for a simple bar chart.
/// ConverterParameter is the max bar width in pixels.
/// Scales linearly; clamps to [2, maxWidth].
/// </summary>
public class CountToBarWidthConverter : IValueConverter
{
    // We cache the max count seen to auto-scale. For simplicity,
    // use a fixed max of 200px and let the bar represent relative size.
    // A more sophisticated approach would use a MultiBinding.
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int count) return 2.0;

        double maxWidth = 200.0;
        if (parameter is string s && double.TryParse(s, CultureInfo.InvariantCulture, out double mw))
            maxWidth = mw;

        // Simple log-scale to handle wide ranges
        double logCount = count > 0 ? Math.Log10(count + 1) : 0;
        double logMax = 7.0; // log10(10M) — generous upper bound
        double width = Math.Clamp(logCount / logMax * maxWidth, 2.0, maxWidth);
        return width;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}