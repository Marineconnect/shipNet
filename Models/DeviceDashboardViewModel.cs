namespace StarlinkDeviceManager.Models;

public class DeviceDashboardViewModel
{
    public string CustomerName { get; set; } = "Shipnet Operations";
    public string CustomerType { get; set; } = "Fleet Control Center";
    public List<DeviceListItemViewModel> Devices { get; set; } = [];
    public List<DeviceTenantOptionViewModel> Tenants { get; set; } = [];
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalDevices { get; set; }
    public string SearchTerm { get; set; } = string.Empty;
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalDevices / (double)PageSize);
    public bool HasPreviousPage => CurrentPage > 1;
    public bool HasNextPage => CurrentPage < TotalPages;
    public bool IsTenantScoped { get; set; }
    public int? CurrentTenantId { get; set; }
    public string? CurrentTenantName { get; set; }
    public bool CanManageDevices { get; set; } = true;
    public bool CanCreateSubscriptions { get; set; } = true;
    public bool CanViewMap { get; set; } = true;
    public bool CanManageDataOptIn { get; set; }
    public int? SelectedDeviceId { get; set; }
    public string ActiveDeviceTab { get; set; } = "synced";
    public DashboardKpiViewModel Kpi { get; set; } = new();
}

public class DashboardKpiViewModel
{
    public int Month { get; set; } = DateTime.Now.Month;
    public int Year { get; set; } = DateTime.Now.Year;
    public DateTime PeriodStart { get; set; } = new(DateTime.Now.Year, DateTime.Now.Month, 1);
    public DateTime PeriodEnd { get; set; } = new(DateTime.Now.Year, DateTime.Now.Month, DateTime.DaysInMonth(DateTime.Now.Year, DateTime.Now.Month));
    public decimal TotalRevenue { get; set; }
    public int ActiveKitCount { get; set; }
    public int BilledKitCount { get; set; }
    public decimal TotalCommission { get; set; }
    public List<int> Years { get; set; } = [];

    public string PeriodDisplay => $"{PeriodStart:dd/MM/yyyy} - {PeriodEnd:dd/MM/yyyy}";
}

public class DevicePageResult
{
    public List<DeviceListItemViewModel> Devices { get; set; } = [];
    public int TotalDevices { get; set; }
}

public class DeviceListItemViewModel
{
    public int Id { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceCode { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public int? TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string KitId { get; set; } = string.Empty;
    public string Availability { get; set; } = string.Empty;
    public string Latitude { get; set; } = string.Empty;
    public string Longitude { get; set; } = string.Empty;
    public string SystemType { get; set; } = string.Empty;
    public string KitNumber { get; set; } = string.Empty;
    public string ServiceLine { get; set; } = string.Empty;
    public string TrafficId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public DateTime? LastUpdateTime { get; set; }
    public DateTime? TokenExpiredTime { get; set; }
    public DateTime? LastSysnTime { get; set; }
    public decimal? UsageData { get; set; }
    public decimal? PriorityData { get; set; }
    public decimal? OverageData { get; set; }

    public string LastUpdateTimeVietnam =>
        LastUpdateTime.HasValue
            ? TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(LastUpdateTime.Value, DateTimeKind.Utc),
                TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"))
              .ToString("dd/MM/yyyy HH:mm:ss")
            : "-";

    public string TokenExpiredTimeVietnam =>
        TokenExpiredTime.HasValue
            ? TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(TokenExpiredTime.Value, DateTimeKind.Utc),
                TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"))
              .ToString("dd/MM/yyyy HH:mm:ss")
            : "-";

    public string LastSysnTimeVietnam =>
        LastSysnTime.HasValue
            ? TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(LastSysnTime.Value, DateTimeKind.Utc),
                TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"))
              .ToString("dd/MM/yyyy HH:mm:ss")
            : "-";

    public string TokenExpiredTimeUtcIso =>
        TokenExpiredTime.HasValue
            ? DateTime.SpecifyKind(TokenExpiredTime.Value, DateTimeKind.Utc).ToString("O")
            : string.Empty;

    public string UsageDataDisplay => UsageData.HasValue ? $"{UsageData.Value:0.##} GB" : "-";
    public string PriorityDataDisplay => PriorityData.HasValue ? $"{PriorityData.Value:0.##} GB" : "-";
    public bool IsSyncFresh =>
        LastSysnTime.HasValue
        && DateTime.SpecifyKind(LastSysnTime.Value, DateTimeKind.Utc) >= DateTime.UtcNow.AddHours(-1)
        && !string.IsNullOrWhiteSpace(Availability)
        && UsageData.HasValue
        && PriorityData.HasValue;
    public string PlanNameDisplay => string.IsNullOrWhiteSpace(PlanName) ? "-" : PlanName;
    public string VesselNameDisplay => string.IsNullOrWhiteSpace(VesselName) ? "-" : VesselName;
    public string TenantNameDisplay => string.IsNullOrWhiteSpace(TenantName) ? "-" : TenantName;
}

public class DeviceDetailViewModel
{
    public int Id { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceCode { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public int? TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string TokenString { get; set; } = string.Empty;
    public string KitId { get; set; } = string.Empty;
    public string Availability { get; set; } = string.Empty;
    public string Latitude { get; set; } = string.Empty;
    public string Longitude { get; set; } = string.Empty;
    public string SystemType { get; set; } = string.Empty;
    public string KitNumber { get; set; } = string.Empty;
    public string ServiceLine { get; set; } = string.Empty;
    public string TrafficId { get; set; } = string.Empty;
    public DateTime? LastUpdateTime { get; set; }
    public DateTime? TokenExpiredTime { get; set; }
    public DateTime? LastSysnTime { get; set; }
    public decimal? SubscriptionUsageGb { get; set; }
    public decimal? SubscriptionLimitGb { get; set; }
    public decimal? PriorityOverageGb { get; set; }
    public decimal? PriorityOverageLimitGb { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public bool? DataOptInEnabled { get; set; }

    public string LastUpdateTimeVietnam =>
        LastUpdateTime.HasValue
            ? TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(LastUpdateTime.Value, DateTimeKind.Utc),
                TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"))
              .ToString("dd/MM/yyyy HH:mm:ss")
            : "-";

    public string TokenExpiredTimeVietnam =>
        TokenExpiredTime.HasValue
            ? TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(TokenExpiredTime.Value, DateTimeKind.Utc),
                TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"))
              .ToString("dd/MM/yyyy HH:mm:ss")
            : "-";

    public string LastSysnTimeVietnam =>
        LastSysnTime.HasValue
            ? TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(LastSysnTime.Value, DateTimeKind.Utc),
                TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"))
              .ToString("dd/MM/yyyy HH:mm:ss")
            : "-";

    public string TokenExpiredTimeUtcIso =>
        TokenExpiredTime.HasValue
            ? DateTime.SpecifyKind(TokenExpiredTime.Value, DateTimeKind.Utc).ToString("O")
            : string.Empty;

    public string SubscriptionUsageDisplay => SubscriptionUsageGb.HasValue ? $"{SubscriptionUsageGb.Value:0.##} GB" : "-";
    public string SubscriptionLimitDisplay => SubscriptionLimitGb.HasValue ? $"{SubscriptionLimitGb.Value:0.##} GB" : "-";
    public string PriorityOverageDisplay => PriorityOverageGb.HasValue ? $"{PriorityOverageGb.Value:0.##} GB" : "-";
    public string PriorityOverageLimitDisplay => PriorityOverageLimitGb.HasValue ? $"{PriorityOverageLimitGb.Value:0.##} GB" : "-";
    public string UsageProgressPercent => SubscriptionUsageGb.HasValue && SubscriptionLimitGb.HasValue && SubscriptionLimitGb.Value > 0m
        ? Math.Clamp((double)(SubscriptionUsageGb.Value / SubscriptionLimitGb.Value * 100m), 0d, 100d).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
        : "0";

    public string MapEmbedUrl
    {
        get
        {
            if (!double.TryParse(Latitude, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(Longitude, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lng))
            {
                return string.Empty;
            }

            var delta = 0.04d;
            var left = lng - delta;
            var right = lng + delta;
            var top = lat + delta;
            var bottom = lat - delta;
            return $"https://www.openstreetmap.org/export/embed.html?bbox={left.ToString(System.Globalization.CultureInfo.InvariantCulture)}%2C{bottom.ToString(System.Globalization.CultureInfo.InvariantCulture)}%2C{right.ToString(System.Globalization.CultureInfo.InvariantCulture)}%2C{top.ToString(System.Globalization.CultureInfo.InvariantCulture)}&layer=mapnik&marker={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}%2C{lng.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }
    }
}

public class DeviceDataOptInManagementResult
{
    public bool Success { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string MessageEn { get; set; } = string.Empty;
    public int DeviceId { get; set; }
    public string TerminalId { get; set; } = string.Empty;
    public bool? CurrentEnabled { get; set; }
    public string ApiWarning { get; set; } = string.Empty;
    public List<DeviceDataOptInHistoryItem> History { get; set; } = [];
}

public class DeviceDataOptInHistoryItem
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public int? UserId { get; set; }
    public string PerformedBy { get; set; } = string.Empty;
    public DateTime PerformedAtUtc { get; set; }
    public bool? OldStatus { get; set; }
    public bool NewStatus { get; set; }
    public bool ApiSuccess { get; set; }
    public int? HttpStatusCode { get; set; }
    public string ApiResponse { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public long? KvhCommandId { get; set; }
    public string CommandStatus { get; set; } = string.Empty;
    public string JobStatus { get; set; } = string.Empty;
    public string VerificationStatus { get; set; } = string.Empty;
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? VerifiedAtUtc { get; set; }
    public string PerformedAtDisplay => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(PerformedAtUtc, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time")).ToString("dd/MM/yyyy HH:mm:ss");
}

public class UpdateDeviceDataOptInRequest
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
}

public class DeviceDataOptInChangeResult
{
    public bool Success { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string MessageEn { get; set; } = string.Empty;
    public int DeviceId { get; set; }
    public string TerminalId { get; set; } = string.Empty;
    public bool? OldStatus { get; set; }
    public bool NewStatus { get; set; }
    public int? HttpStatusCode { get; set; }
    public string ApiResponse { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public long? CommandId { get; set; }
    public int? RemainingSeconds { get; set; }
    public DateTime? NextAllowedAtUtc { get; set; }
    public DeviceDataOptInHistoryItem? HistoryItem { get; set; }
}

public class DeviceTenantOptionViewModel
{
    public int Id { get; set; }
    public string TenantName { get; set; } = string.Empty;
}

public class TelemetryTimelinePoint
{
    public long Timestamp { get; set; }
    public decimal Value { get; set; }
}

public class TelemetryTimelineResult
{
    public bool Success { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string MessageEn { get; set; } = string.Empty;
    public string RawResponse { get; set; } = string.Empty;
    public string TerminalId { get; set; } = string.Empty;
    public string Metric { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public long Start { get; set; }
    public long End { get; set; }
    public List<TelemetryTimelinePoint> Points { get; set; } = [];
}

public class DeviceWifiResult
{
    public bool Success { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string MessageEn { get; set; } = string.Empty;
    public string RawResponse { get; set; } = string.Empty;
    public string TerminalId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string Ssid { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool? Enabled { get; set; }
}

public class DevicePlanManagementResult
{
    public bool Success { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string MessageEn { get; set; } = string.Empty;
    public int DeviceId { get; set; }
    public int? TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public List<DevicePlanOptionViewModel> PlanOptions { get; set; } = [];
    public List<DevicePlanPriceViewModel> DevicePrices { get; set; } = [];
}

public class DevicePlanOptionViewModel
{
    public int TenantPricingId { get; set; }
    public int PricingPlanId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public string PlanCode { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
    public decimal BaseData { get; set; }
    public decimal ResellerPrice { get; set; }
    public decimal FinalPrice { get; set; }
    public decimal ResellerOverChargePrice { get; set; }
    public decimal FinalOverChargePrice { get; set; }
    public string DisplayName => $"{PlanName} ({PlanCode})";
}

public class DevicePlanPriceViewModel
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public int PricingPlanId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public string PlanCode { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
    public decimal BaseData { get; set; }
    public decimal ResellerPrice { get; set; }
    public decimal FinalPrice { get; set; }
    public decimal ResellerOverChargePrice { get; set; }
    public decimal FinalOverChargePrice { get; set; }
    public DateTime? UpdatedDate { get; set; }
    public string? UpdatedBy { get; set; }

    public string UpdatedDateDisplay => UpdatedDate.HasValue
        ? UpdatedDate.Value.ToString("dd/MM/yyyy HH:mm:ss")
        : "-";
}

public class SaveDevicePlanRequest
{
    public int DeviceId { get; set; }
    public int PricingPlanId { get; set; }
    public string Status { get; set; } = "active";
    public decimal ResellerPrice { get; set; }
    public decimal FinalPrice { get; set; }
    public decimal ResellerOverChargePrice { get; set; }
    public decimal FinalOverChargePrice { get; set; }
}

public class DeleteDevicePlanRequest
{
    public int DeviceId { get; set; }
    public int PricingPlanId { get; set; }
}

public class SaveDevicePlanResult
{
    public bool Success { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string MessageEn { get; set; } = string.Empty;
    public bool Created { get; set; }
    public DevicePlanPriceViewModel? Price { get; set; }
}

public class DeleteDevicePlanResult
{
    public bool Success { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string MessageEn { get; set; } = string.Empty;
}
