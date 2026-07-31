namespace StarlinkDeviceManager.Services;

public static class ShipNetTimeZone
{
    public const string DisplaySuffix = "UTC+7";

    public static DateTime ToVietnamTime(DateTime utcValue)
    {
        var value = DateTime.SpecifyKind(utcValue, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(value, ResolveVietnamTimeZone());
    }

    public static string FormatVietnam(DateTime? utcValue, bool includeSeconds = false, bool includeSuffix = false)
    {
        if (!utcValue.HasValue) return "-";
        var pattern = includeSeconds ? "dd/MM/yyyy HH:mm:ss" : "dd/MM/yyyy HH:mm";
        var text = ToVietnamTime(utcValue.Value).ToString(pattern);
        return includeSuffix ? $"{text} {DisplaySuffix}" : text;
    }

    private static TimeZoneInfo ResolveVietnamTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
        }
    }
}
