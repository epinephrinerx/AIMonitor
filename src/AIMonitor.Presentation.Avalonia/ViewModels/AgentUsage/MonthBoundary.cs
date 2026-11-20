namespace AIMonitor.Presentation.Avalonia.ViewModels;

/// <summary>
/// Month-boundary calculations shared by the ccusage-backed snapshot builders (R-015 E1, R-016).
/// </summary>
internal static class MonthBoundary
{
    /// <summary>First day of the next local month at 00:00 in <paramref name="zone"/> (E1).</summary>
    public static DateTimeOffset FirstOfNextMonth(DateOnly today, TimeZoneInfo zone)
    {
        var local = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
