using System.Globalization;

namespace AIMonitor.Presentation.Wpf.Controls;

/// <summary>
/// Number and label formatting shared by the history charts. Mirrors 1.3.3 <c>formatting.py</c>
/// (compact / money / axis_tick / metric_label / day_label) so axis ticks and value labels read alike.
/// </summary>
public static class ChartFormat
{
    public const string ValueMetric = "Equivalent value";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>1,284 / 12.9K / 4.2M / 1.3B.</summary>
    public static string Compact(double value)
    {
        var magnitude = Math.Abs(value);
        if (magnitude >= 1_000_000_000)
        {
            return (value / 1_000_000_000).ToString("0.0", Inv).Replace(".0", "", StringComparison.Ordinal) + "B";
        }

        if (magnitude >= 1_000_000)
        {
            return (value / 1_000_000).ToString("0.0", Inv).Replace(".0", "", StringComparison.Ordinal) + "M";
        }

        if (magnitude >= 10_000)
        {
            return (value / 1_000).ToString("0.0", Inv).Replace(".0", "", StringComparison.Ordinal) + "K";
        }

        return value.ToString("#,##0", Inv);
    }

    public static string Money(double value)
    {
        if (value != 0 && Math.Abs(value) < 0.01)
        {
            return "<$0.01";
        }

        return Math.Abs(value) >= 1000 ? "$" + Compact(value) : "$" + value.ToString("#,##0.00", Inv);
    }

    public static string AxisTick(double value, string metric)
    {
        if (metric != ValueMetric)
        {
            return Compact(value);
        }

        if (value >= 1000)
        {
            return "$" + (value / 1000).ToString("0", Inv) + "K";
        }

        if (value >= 10)
        {
            return "$" + value.ToString("#,##0", Inv);
        }

        var text = value.ToString("#,##0.00", Inv);
        if (text.Contains('.', StringComparison.Ordinal))
        {
            text = text.TrimEnd('0').TrimEnd('.');
        }

        return "$" + text;
    }

    public static string MetricLabel(double value, string metric) =>
        metric == ValueMetric ? Money(value) : Compact(value);

    /// <summary>"Sep 9" - month abbreviation plus un-padded day.</summary>
    public static string DayLabel(DateOnly day) => day.ToString("MMM d", Inv);

    /// <summary>
    /// An axis maximum whose divisions land on round tick values: the *step* is rounded, so a peak of
    /// 7.1M over four divisions becomes 0 / 2M / 4M / 6M / 8M rather than 0 / 1.9M / 3.8M / 5.6M / 7.5M.
    /// </summary>
    public static double NiceAxisMax(double value, int divisions)
    {
        if (value <= 0)
        {
            return divisions;
        }

        var rawStep = value / divisions;
        var baseUnit = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        foreach (var step in new[] { 1.0, 2.0, 2.5, 5.0, 10.0 })
        {
            if (rawStep <= step * baseUnit)
            {
                return step * baseUnit * divisions;
            }
        }

        return 10 * baseUnit * divisions;
    }
}
