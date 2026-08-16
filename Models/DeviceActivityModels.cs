using System.Text.Json;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Models;

public static class DeviceActivityCategories
{
    public const string Device = "DEVICE";
    public const string Billing = "BILLING";
    public const string Payment = "PAYMENT";
    public const string Subscription = "SUBSCRIPTION";
    public const string Kvh = "KVH";
    public const string Data = "DATA";
    public const string Networking = "NETWORKING";
    public const string Plan = "PLAN";
    public const string System = "SYSTEM";
}

public static class DeviceActivityStatuses
{
    public const string Requested = "Requested";
    public const string Pending = "Pending";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
}

public static class DeviceActivityActions
{
    public const string DeviceCreated = "DEVICE_CREATED";
    public const string DeviceUpdated = "DEVICE_UPDATED";
    public const string BillingCycleCreated = "BILLING_CYCLE_CREATED";
    public const string BillingCycleUpdated = "BILLING_CYCLE_UPDATED";
    public const string InvoiceCreated = "INVOICE_CREATED";
    public const string InvoiceUpdated = "INVOICE_UPDATED";
    public const string InvoicePaid = "INVOICE_PAID";
    public const string InvoiceStatusChanged = "INVOICE_STATUS_CHANGED";
    public const string SubscriptionPauseRequested = "SUBSCRIPTION_PAUSE_REQUESTED";
    public const string SubscriptionPaused = "SUBSCRIPTION_PAUSED";
    public const string SubscriptionResumeRequested = "SUBSCRIPTION_RESUME_REQUESTED";
    public const string SubscriptionResumed = "SUBSCRIPTION_RESUMED";
    public const string SubscriptionResumeFailed = "SUBSCRIPTION_RESUME_FAILED";
    public const string SubscriptionResumeSkipped = "SUBSCRIPTION_RESUME_SKIPPED";
    public const string SubscriptionCancelScheduleRequested = "SUBSCRIPTION_CANCEL_SCHEDULE_REQUESTED";
    public const string DataOptInRequested = "DATA_OPT_IN_REQUESTED";
    public const string DataOptInCompleted = "DATA_OPT_IN_COMPLETED";
    public const string DataOptOutRequested = "DATA_OPT_OUT_REQUESTED";
    public const string DataOptOutCompleted = "DATA_OPT_OUT_COMPLETED";
    public const string WifiUpdateRequested = "WIFI_UPDATE_REQUESTED";
    public const string WifiUpdateCompleted = "WIFI_UPDATE_COMPLETED";
    public const string WifiUpdateFailed = "WIFI_UPDATE_FAILED";
    public const string RouterRebootRequested = "ROUTER_REBOOT_REQUESTED";
    public const string RouterRebootCompleted = "ROUTER_REBOOT_COMPLETED";
    public const string RouterRebootFailed = "ROUTER_REBOOT_FAILED";
    public const string PlanAssigned = "PLAN_ASSIGNED";
    public const string PlanUpdated = "PLAN_UPDATED";
    public const string PlanRemoved = "PLAN_REMOVED";
}

public static class DeviceActivitySources
{
    public const string BankTransfer = "BANK_TRANSFER";
    public const string ManualInvoiceUpdate = "MANUAL_INVOICE_UPDATE";
    public const string Dashboard = "DASHBOARD";
    public const string KvhWorker = "KVH_WORKER";
    public const string System = "SYSTEM";
}

public sealed class DeviceActivityLogEntry
{
    public int DeviceId { get; set; }
    public int? TenantId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? DetailJson { get; set; }
    public string? Source { get; set; }
    public int? UserId { get; set; }
    public string? PerformedBy { get; set; }
    public string? ReferenceType { get; set; }
    public string? ReferenceId { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime? CreatedAtUtc { get; set; }

    public static string ToSafeJson(object value)
    {
        var json = JsonSerializer.Serialize(value);
        return DeviceActivitySanitizer.Sanitize(json);
    }
}

public sealed class DeviceActivityFilter
{
    public string? Category { get; set; }
    public string? Status { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
}

public sealed class DeviceActivityPageResult
{
    public int DeviceId { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalItems { get; set; }
    public List<DeviceActivityItem> Items { get; set; } = [];
}

public sealed class DeviceActivityItem
{
    public long Id { get; set; }
    public DateTime TimeUtc { get; set; }
    public string TimeDisplay => ShipNetTimeZone.FormatVietnam(TimeUtc, includeSeconds: true);
    public string Category { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string OldValue { get; set; } = string.Empty;
    public string NewValue { get; set; } = string.Empty;
    public string Change => string.IsNullOrWhiteSpace(OldValue) && string.IsNullOrWhiteSpace(NewValue)
        ? "-"
        : $"{(string.IsNullOrWhiteSpace(OldValue) ? "-" : OldValue)} -> {(string.IsNullOrWhiteSpace(NewValue) ? "-" : NewValue)}";
    public string Summary { get; set; } = string.Empty;
    public string DetailJson { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string PerformedBy { get; set; } = string.Empty;
    public string ReferenceType { get; set; } = string.Empty;
    public string ReferenceId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public bool IsLegacy { get; set; }
}

public sealed class KvhPaymentResumeRequest
{
    public int SubscriptionId { get; set; }
    public string Source { get; set; } = string.Empty;
    public int? UserId { get; set; }
    public string PerformedBy { get; set; } = string.Empty;
    public string ReferenceType { get; set; } = string.Empty;
    public string ReferenceId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string DetailJson { get; set; } = string.Empty;
    public int? AllowedTenantId { get; set; }
    public int? AllowedDeviceId { get; set; }
}

public sealed class KvhPaymentResumeResult
{
    public bool Success { get; set; }
    public bool ResumeSubmitted { get; set; }
    public bool Skipped { get; set; }
    public int SubscriptionId { get; set; }
    public int DeviceId { get; set; }
    public long? KvhSubscriptionId { get; set; }
    public long? CommandId { get; set; }
    public string JobId { get; set; } = string.Empty;
    public string KvhStatus { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class KvhPaymentResumePrecheckResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public string KitNumber { get; set; } = string.Empty;
    public string KvhStatus { get; set; } = string.Empty;
    public bool CanResume { get; set; }
}

public static class DeviceActivitySanitizer
{
    private static readonly string[] SecretMarkers = ["accesstoken", "authorization", "api key", "apikey", "secret", "password", "client_secret"];

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = value;
        foreach (var marker in SecretMarkers)
        {
            if (sanitized.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                sanitized = RedactMarkerValue(sanitized, marker);
            }
        }

        return sanitized.Length <= 8000 ? sanitized : sanitized[..8000];
    }

    private static string RedactMarkerValue(string value, string marker)
    {
        var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var end = value.IndexOfAny([',', '\n', '\r', '}'], index);
            if (end < 0)
            {
                end = value.Length;
            }

            value = string.Concat(value.AsSpan(0, index), marker, ":***", value.AsSpan(end));
            index = value.IndexOf(marker, index + marker.Length + 4, StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }
}
