namespace StarlinkDeviceManager.Models;

public class KvhSubscriptionSyncResult
{
    public bool Success { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string MessageEn { get; set; } = string.Empty;
    public int DeviceId { get; set; }
    public string TerminalId { get; set; } = string.Empty;
    public string TrafficId { get; set; } = string.Empty;
    public int ReturnedCount { get; set; }
    public int InsertedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int DeactivatedCount { get; set; }
    public int CurrentCount { get; set; }
    public string RawResponse { get; set; } = string.Empty;
    public bool UsedStoredTrafficId { get; set; }
}

public sealed class KvhDeviceSyncResult : KvhSubscriptionSyncResult;

public sealed class KvhSolutionFilter
{
    public string? Search { get; set; }
    public string? Status { get; set; }
    public string? Region { get; set; }
    public int? TenantId { get; set; }
    public string? SyncState { get; set; }
}

public sealed class KvhSolutionPageResult
{
    public List<KvhSolutionListItemViewModel> Items { get; set; } = [];
    public List<KvhSyncBatchSummaryViewModel> RecentBatches { get; set; } = [];
    public List<DeviceTenantOptionViewModel> Tenants { get; set; } = [];
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public int TotalItems { get; set; }
    public KvhSolutionFilter Filter { get; set; } = new();
    public bool IsTenantScoped { get; set; }
    public bool CanManageSolutions { get; set; }
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalItems / (double)PageSize);
    public bool HasPreviousPage => CurrentPage > 1;
    public bool HasNextPage => CurrentPage < TotalPages;
    public int StartItem => TotalItems == 0 ? 0 : ((CurrentPage - 1) * PageSize) + 1;
    public int EndItem => TotalItems == 0 ? 0 : Math.Min(CurrentPage * PageSize, TotalItems);
}

public sealed class KvhSolutionListItemViewModel
{
    public int DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string TerminalId { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public int? TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string KitNumber { get; set; } = string.Empty;
    public string Availability { get; set; } = string.Empty;
    public DateTime? LastUpdateTimeUtc { get; set; }
    public string TrafficId { get; set; } = string.Empty;
    public long? KvhSubscriptionId { get; set; }
    public string Region { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string ScheduledAction { get; set; } = string.Empty;
    public string ScheduleId { get; set; } = string.Empty;
    public DateTime? ScheduledEffectiveDateUtc { get; set; }
    public decimal? AllowanceGb { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
    public DateTime? LastSyncAtUtc { get; set; }
    public string LastSyncStatus { get; set; } = string.Empty;
    public string LastSyncErrorCode { get; set; } = string.Empty;
    public bool HasPendingCommand { get; set; }
    public DateTime? CooldownUntilUtc { get; set; }

    public bool MissingTrafficId => string.IsNullOrWhiteSpace(TrafficId);
    public bool HasScheduledPause => ScheduledAction.Contains("pause", StringComparison.OrdinalIgnoreCase) ||
        ScheduledAction.Contains("suspend", StringComparison.OrdinalIgnoreCase);
    public bool IsActive => Status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) || Status.Equals("Active", StringComparison.OrdinalIgnoreCase);
    public bool IsPaused => Status.Contains("SUSPEND", StringComparison.OrdinalIgnoreCase) || Status.Contains("PAUSE", StringComparison.OrdinalIgnoreCase);
    public bool CooldownActive => CooldownUntilUtc.HasValue && DateTime.SpecifyKind(CooldownUntilUtc.Value, DateTimeKind.Utc) > DateTime.UtcNow;
    public bool CanPause => !MissingTrafficId && IsActive && !HasScheduledPause && !HasPendingCommand && !CooldownActive;
    public bool CanResume => !MissingTrafficId && IsPaused && !HasPendingCommand && !CooldownActive;
    public string AllowanceDisplay => AllowanceGb.HasValue ? $"{AllowanceGb.Value:0.##} GB" : "-";
    public string LastSyncDisplay => FormatUtc(LastSyncAtUtc);
    public string ScheduledEffectiveDisplay => FormatUtc(ScheduledEffectiveDateUtc);
    public string LastUpdateDisplay => FormatUtc(LastUpdateTimeUtc);

    private static string FormatUtc(DateTime? value) =>
        value.HasValue
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time")).ToString("dd/MM/yyyy HH:mm")
            : "-";
}

public sealed class KvhSolutionDetailViewModel
{
    public int DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string TerminalId { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public string KitNumber { get; set; } = string.Empty;
    public string KitId { get; set; } = string.Empty;
    public string ServiceLine { get; set; } = string.Empty;
    public string Availability { get; set; } = string.Empty;
    public DateTime? LastUpdateTimeUtc { get; set; }
    public string TrafficId { get; set; } = string.Empty;
    public bool CanManageSolutions { get; set; }
    public List<KvhSubscriptionEntryViewModel> CurrentSubscriptions { get; set; } = [];
    public List<KvhSubscriptionSyncLogViewModel> SyncLogs { get; set; } = [];
    public List<KvhCommandStatusDto> RecentCommands { get; set; } = [];
}

public sealed class KvhSubscriptionEntryViewModel
{
    public long Id { get; set; }
    public string SubscriptionKey { get; set; } = string.Empty;
    public string TrafficId { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string PlanJson { get; set; } = string.Empty;
    public string OptInStatus { get; set; } = string.Empty;
    public string OptInJson { get; set; } = string.Empty;
    public string ScheduledAction { get; set; } = string.Empty;
    public string ScheduleId { get; set; } = string.Empty;
    public DateTime? ScheduledEffectiveDateUtc { get; set; }
    public decimal? AllowanceGb { get; set; }
    public decimal? Proration { get; set; }
    public DateTime? EffectiveDateUtc { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
    public string RawSubscriptionJson { get; set; } = string.Empty;
}

public sealed class KvhSubscriptionSyncLogViewModel
{
    public long Id { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public bool Success { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string TrafficId { get; set; } = string.Empty;
    public int ReturnedCount { get; set; }
    public int? HttpStatusCode { get; set; }
    public string SyncSource { get; set; } = string.Empty;
    public string ResponseJson { get; set; } = string.Empty;

    public string StartedDisplay => FormatUtc(StartedAtUtc);
    public string CompletedDisplay => FormatUtc(CompletedAtUtc);

    private static string FormatUtc(DateTime? value) =>
        value.HasValue
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time")).ToString("dd/MM/yyyy HH:mm:ss")
            : "-";
}

public sealed class KvhSolutionCommandRequest
{
    public int DeviceId { get; set; }
    public long KvhSubscriptionId { get; set; }
}

public sealed class KvhBatchCreateRequest
{
    public string Mode { get; set; } = string.Empty;
    public List<int> DeviceIds { get; set; } = [];
    public int? TenantId { get; set; }
    public long? SourceBatchId { get; set; }
}

public sealed class KvhBatchCreateResult
{
    public bool Success { get; set; }
    public long BatchId { get; set; }
    public int TotalItems { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class KvhSyncBatchSummaryViewModel
{
    public long Id { get; set; }
    public string BatchType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int TotalItems { get; set; }
    public int PendingItems { get; set; }
    public int ProcessingItems { get; set; }
    public int SuccessItems { get; set; }
    public int FailedItems { get; set; }
    public int EmptyItems { get; set; }
    public int SkippedItems { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public int ProcessedItems => SuccessItems + FailedItems + EmptyItems + SkippedItems;
    public int ProgressPercent => TotalItems <= 0 ? 0 : Math.Min(100, (int)Math.Round(ProcessedItems * 100.0 / TotalItems));
    public bool IsRunning => Status is "CREATED" or "QUEUED" or "RUNNING";
}

public sealed class KvhSyncBatchDetail : KvhSyncBatchSummaryViewModel
{
    public List<KvhSyncBatchItemViewModel> Items { get; set; } = [];
}

public sealed class KvhSyncBatchItemViewModel
{
    public long Id { get; set; }
    public long BatchId { get; set; }
    public int DeviceId { get; set; }
    public string TerminalId { get; set; } = string.Empty;
    public string TrafficId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int? ReturnedCount { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
}

public sealed class KvhBulkSyncOptions
{
    public const string SectionName = "KvhBulkSync";
    public bool Enabled { get; set; } = true;
    public int WorkerIntervalSeconds { get; set; } = 10;
    public int BatchSize { get; set; } = 10;
    public int MaxConcurrentRequests { get; set; } = 3;
    public int RequestsPerMinute { get; set; } = 180;
    public int MaxAttempts { get; set; } = 3;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int RetryBaseDelaySeconds { get; set; } = 30;
}
