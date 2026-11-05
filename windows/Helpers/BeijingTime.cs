namespace HeartRate.Helpers;

/// <summary>
/// 北京时间（UTC+8）换算：历史记录与图表时间轴一律按北京时间显示，
/// 与电脑当前所在时区无关。系统缺少 "China Standard Time" 时退化为固定 +8 偏移。
/// </summary>
public static class BeijingTime
{
    private static readonly TimeZoneInfo Zone = Resolve();

    private static TimeZoneInfo Resolve()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
        catch (Exception)
        {
            return TimeZoneInfo.CreateCustomTimeZone("CST+8", TimeSpan.FromHours(8), "北京时间", "北京时间");
        }
    }

    /// <summary>当前北京时间。</summary>
    public static DateTimeOffset Now => ToBeijing(DateTimeOffset.UtcNow);

    /// <summary>任意时刻换算为北京时间（偏移 +08:00）。</summary>
    public static DateTimeOffset ToBeijing(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, Zone);
}
