using StarlinkDeviceManager.Services;

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

public sealed class KvhSyncHistoryFilter
{
    public string? Search { get; set; }
    public int? TenantId { get; set; }
    public string? Result { get; set; }
    public string? SyncSource { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
}

public sealed class KvhSolutionPageResult
{
    public string ActiveTab { get; set; } = "devices";
    public List<KvhSolutionListItemViewModel> Items { get; set; } = [];
    public List<KvhSyncBatchSummaryViewModel> RecentBatches { get; set; } = [];
    public List<DeviceTenantOptionViewModel> Tenants { get; set; } = [];
    public KvhSyncHistoryPageResult SyncHistory { get; set; } = new();
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

public sealed class KvhSyncHistoryPageResult
{
    public List<KvhSyncHistoryItemViewModel> Items { get; set; } = [];
    public KvhSyncHistoryFilter Filter { get; set; } = new();
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public int TotalItems { get; set; }
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalItems / (double)PageSize);
    public bool HasPreviousPage => CurrentPage > 1;
    public bool HasNextPage => CurrentPage < TotalPages;
    public int StartItem => TotalItems == 0 ? 0 : ((CurrentPage - 1) * PageSize) + 1;
    public int EndItem => TotalItems == 0 ? 0 : Math.Min(CurrentPage * PageSize, TotalItems);
}

public sealed class KvhSyncHistoryItemViewModel
{
    public long Id { get; set; }
    public int DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public string TerminalId { get; set; } = string.Empty;
    public string TrafficId { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public bool Success { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string ResponseJson { get; set; } = string.Empty;
    public int ReturnedCount { get; set; }
    public int? HttpStatusCode { get; set; }
    public string SyncSource { get; set; } = string.Empty;

    public string StartedDisplay => FormatUtc(StartedAtUtc);
    public string CompletedDisplay => FormatUtc(CompletedAtUtc);
    public string ResultKey => Success && ReturnedCount == 0 ? "Empty" : Success ? "Success" : "Failed";

    private static string FormatUtc(DateTime? value) =>
        ShipNetTimeZone.FormatVietnam(value, includeSeconds: true);
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
    public DateTime? SubscriptionEffectiveDateUtc { get; set; }
    public DateTime? ScheduledEffectiveDateUtc { get; set; }
    public decimal? AllowanceGb { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
    public DateTime? LastSyncAtUtc { get; set; }
    public string LastSyncStatus { get; set; } = string.Empty;
    public string LastSyncErrorCode { get; set; } = string.Empty;
    public bool HasPendingCommand { get; set; }
    public DateTime? CooldownUntilUtc { get; set; }

    public bool MissingTrafficId => string.IsNullOrWhiteSpace(TrafficId);
    public string NormalizedScheduledAction => KvhJsonHelpers.NormalizeScheduledAction(ScheduledAction);
    public bool HasScheduledPause => NormalizedScheduledAction == "SUSPEND" &&
        (!string.IsNullOrWhiteSpace(ScheduleId) || ScheduledEffectiveDateUtc.HasValue);
    public bool IsActive => Status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) || Status.Equals("Active", StringComparison.OrdinalIgnoreCase);
    public bool IsPaused => Status.Contains("SUSPEND", StringComparison.OrdinalIgnoreCase) || Status.Contains("PAUSE", StringComparison.OrdinalIgnoreCase);
    public bool CooldownActive => CooldownUntilUtc.HasValue && DateTime.SpecifyKind(CooldownUntilUtc.Value, DateTimeKind.Utc) > DateTime.UtcNow;
    public bool CanPause => !MissingTrafficId && IsActive && !HasScheduledPause && !HasPendingCommand && !CooldownActive;
    public bool CanResume => !MissingTrafficId && IsPaused && !HasPendingCommand && !CooldownActive;
    public bool CanCancelSchedule => !string.IsNullOrWhiteSpace(ScheduleId) && ScheduledEffectiveDateUtc.HasValue;
    public string AllowanceDisplay => AllowanceGb.HasValue ? $"{AllowanceGb.Value:0.##} GB" : "-";
    public string LastSyncDisplay => FormatUtc(LastSyncAtUtc);
    public string SubscriptionEffectiveDisplay => ShipNetTimeZone.FormatVietnam(SubscriptionEffectiveDateUtc);
    public string ScheduledEffectiveDisplay => ShipNetTimeZone.FormatVietnam(ScheduledEffectiveDateUtc, includeSuffix: true);
    public string LastUpdateDisplay => FormatUtc(LastUpdateTimeUtc);
    public string PauseDisabledReason => HasScheduledPause
        ? "Không thể Pause lại vì KVH đã có một yêu cầu Pause đang chờ SUSPEND có hiệu lực."
        : HasPendingCommand
            ? "Đang có lệnh KVH nội bộ chưa hoàn tất."
            : CooldownActive
                ? "Lệnh KVH đang trong thời gian cooldown."
                : string.Empty;
    public string OperationStateDisplay => HasScheduledPause
        ? "Đang chờ SUSPEND có hiệu lực"
        : IsPaused
            ? "Đã SUSPEND"
            : IsActive
                ? "Sẵn sàng"
                : "-";
    public string ScheduleNote => HasScheduledPause
        ? $"KVH đã tiếp nhận yêu cầu Pause. Subscription hiện vẫn {StatusDisplay} và sẽ chuyển sang SUSPEND vào {ScheduledEffectiveDisplay}."
        : string.Empty;
    public string StatusDisplay => string.IsNullOrWhiteSpace(Status) ? "UNKNOWN" : Status.Trim().ToUpperInvariant();

    private static string FormatUtc(DateTime? value) =>
        ShipNetTimeZone.FormatVietnam(value);
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

    public string SubscriptionStatusDisplay =>
        CurrentSubscriptions.Count == 0
            ? "Chưa có dữ liệu"
            : string.Join(", ", CurrentSubscriptions.Select(item => item.StatusDisplay).Distinct(StringComparer.OrdinalIgnoreCase));

    public bool HasProcessingSchedule => CurrentSubscriptions.Any(item => item.HasPendingCommand);

    public string ScheduleStatusDisplay
    {
        get
        {
            var processing = CurrentSubscriptions.FirstOrDefault(item => item.HasPendingCommand);
            if (processing is not null)
            {
                return $"Đang xử lý: {processing.PendingActionDisplay}";
            }

            var scheduled = CurrentSubscriptions
                .Where(item => !string.IsNullOrWhiteSpace(item.NormalizedScheduledAction))
                .Select(item => item.NormalizedScheduledAction)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return scheduled.Count == 0 ? "Không có lịch" : string.Join(", ", scheduled);
        }
    }
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
    public DateTime? ScheduledCreatedAtUtc { get; set; }
    public decimal? AllowanceGb { get; set; }
    public decimal? Proration { get; set; }
    public DateTime? SubscriptionEffectiveDateUtc { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
    public string RawSubscriptionJson { get; set; } = string.Empty;
    public bool HasPendingCommand { get; set; }
    public string PendingCommandType { get; set; } = string.Empty;
    public string PendingCommandStatus { get; set; } = string.Empty;
    public string PendingJobId { get; set; } = string.Empty;
    public string NormalizedScheduledAction => KvhJsonHelpers.NormalizeScheduledAction(ScheduledAction);
    public bool HasScheduledPause => NormalizedScheduledAction == "SUSPEND" &&
        (!string.IsNullOrWhiteSpace(ScheduleId) || ScheduledEffectiveDateUtc.HasValue);
    public bool IsActive => Status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase);
    public bool IsPaused => Status.Contains("SUSPEND", StringComparison.OrdinalIgnoreCase) || Status.Contains("PAUSE", StringComparison.OrdinalIgnoreCase);
    public bool CanPause => IsActive && !HasScheduledPause && !HasPendingCommand;
    public bool CanResume => IsPaused && !HasPendingCommand;
    public bool CanCancelSchedule => !string.IsNullOrWhiteSpace(ScheduleId) && ScheduledEffectiveDateUtc.HasValue;
    public string SubscriptionEffectiveDisplay => ShipNetTimeZone.FormatVietnam(SubscriptionEffectiveDateUtc);
    public string ScheduledEffectiveDisplay => ShipNetTimeZone.FormatVietnam(ScheduledEffectiveDateUtc, includeSuffix: true);
    public string OperationStateDisplay => HasScheduledPause
        ? "Đang chờ SUSPEND có hiệu lực"
        : IsPaused
            ? "Đã SUSPEND"
            : IsActive
                ? "Sẵn sàng"
                : "-";
    public string PauseDisabledReason => HasScheduledPause
        ? "Không thể Pause lại vì KVH đã có một yêu cầu Pause đang chờ SUSPEND có hiệu lực."
        : string.Empty;
    public string ScheduleNote => HasScheduledPause
        ? $"KVH đã tiếp nhận yêu cầu Pause. Subscription hiện vẫn {StatusDisplay} và sẽ chuyển sang SUSPEND vào {ScheduledEffectiveDisplay}."
        : string.Empty;
    public string StatusDisplay => string.IsNullOrWhiteSpace(Status) ? "UNKNOWN" : Status.Trim().ToUpperInvariant();
    public string PendingActionDisplay =>
        PendingCommandType.Contains("RESUME", StringComparison.OrdinalIgnoreCase) ? "Resume" :
        PendingCommandType.Contains("PAUSE", StringComparison.OrdinalIgnoreCase) ? "Pause" :
        PendingCommandType.Contains("CANCEL", StringComparison.OrdinalIgnoreCase) ? "Cancel schedule" :
        PendingCommandType;
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
        ShipNetTimeZone.FormatVietnam(value, includeSeconds: true);
}

public sealed class KvhSolutionCommandRequest
{
    public int DeviceId { get; set; }
    public long KvhSubscriptionId { get; set; }
}

public sealed class KvhSubscriptionActionContext
{
    public long? KvhSubscriptionId { get; set; }
    public string TrafficId { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string ScheduledAction { get; set; } = string.Empty;
    public string ScheduleId { get; set; } = string.Empty;
    public DateTime? ScheduledEffectiveDateUtc { get; set; }
    public bool HasPendingCommand { get; set; }
    public DateTime? CooldownUntilUtc { get; set; }
}

public sealed class KvhSubscriptionActionDecision
{
    public bool Allowed { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string MessageEn { get; set; } = string.Empty;
    public DateTime? ScheduledEffectiveDateUtc { get; set; }
    public DateTime? NextAllowedAtUtc { get; set; }
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
    public string DeviceName { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public string KitNumber { get; set; } = string.Empty;
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
