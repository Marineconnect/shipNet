namespace StarlinkDeviceManager.Models;

public static class KvhSubscriptionOperationTypes
{
    public const string Pause = "PAUSE";
    public const string Resume = "RESUME";

    public static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return normalized is Pause or Resume ? normalized : string.Empty;
    }
}

public static class KvhSubscriptionOperationBatchStatuses
{
    public const string Draft = "DRAFT";
    public const string Validating = "VALIDATING";
    public const string Ready = "READY";
    public const string Queued = "QUEUED";
    public const string Running = "RUNNING";
    public const string Verifying = "VERIFYING";
    public const string Completed = "COMPLETED";
    public const string CompletedWithErrors = "COMPLETED_WITH_ERRORS";
    public const string Failed = "FAILED";
    public const string CancelRequested = "CANCEL_REQUESTED";
    public const string Cancelled = "CANCELLED";
}

public static class KvhSubscriptionOperationItemStatuses
{
    public const string Draft = "DRAFT";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string Ready = "READY";
    public const string Queued = "QUEUED";
    public const string WaitingCooldown = "WAITING_COOLDOWN";
    public const string Submitting = "SUBMITTING";
    public const string Submitted = "SUBMITTED";
    public const string JobPending = "JOB_PENDING";
    public const string JobSuccess = "JOB_SUCCESS";
    public const string JobFailed = "JOB_FAILED";
    public const string Verifying = "VERIFYING";
    public const string Verified = "VERIFIED";
    public const string VerificationMismatch = "VERIFICATION_MISMATCH";
    public const string RetryWait = "RETRY_WAIT";
    public const string Skipped = "SKIPPED";
    public const string Cancelled = "CANCELLED";
    public const string Timeout = "TIMEOUT";

    public static readonly HashSet<string> TerminalStates = new(StringComparer.OrdinalIgnoreCase)
    {
        Verified, JobFailed, VerificationMismatch, ValidationFailed, Skipped, Cancelled, Timeout
    };
}

public sealed class KvhSubscriptionOperationOptions
{
    public const string SectionName = "KvhSubscriptionOperations";
    public bool Enabled { get; set; } = true;
    public int WorkerIntervalSeconds { get; set; } = 15;
    public int JobPollIntervalSeconds { get; set; } = 120;
    public int VerificationIntervalSeconds { get; set; } = 120;
    public int BatchSize { get; set; } = 20;
    public int MaxConcurrentSubmits { get; set; } = 3;
    public int MaxConcurrentPolls { get; set; } = 5;
    public int RequestsPerMinute { get; set; } = 180;
    public int MaxSubmitAttempts { get; set; } = 3;
    public int MaxPollCount { get; set; } = 30;
    public int CommandTimeoutMinutes { get; set; } = 60;
    public int MaxVerificationAttempts { get; set; } = 10;
    public int TerminalCommandCooldownMinutes { get; set; } = 5;
    public int MaxImportRows { get; set; } = 5000;
    public int MaxImportFileSizeMb { get; set; } = 5;
}

public sealed class KvhSubscriptionOperationFilter
{
    public string? Search { get; set; }
    public string? OperationType { get; set; }
    public string? Status { get; set; }
    public int? TenantId { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? CreatedBy { get; set; }
}

public sealed class KvhSubscriptionOperationIndexViewModel
{
    public List<KvhSubscriptionOperationBatchListItem> Items { get; set; } = [];
    public List<DeviceTenantOptionViewModel> Tenants { get; set; } = [];
    public KvhSubscriptionOperationFilter Filter { get; set; } = new();
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public int TotalItems { get; set; }
    public bool CanManage { get; set; }
    public bool IsTenantScoped { get; set; }
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalItems / (double)PageSize);
    public bool HasPreviousPage => CurrentPage > 1;
    public bool HasNextPage => CurrentPage < TotalPages;
    public int StartItem => TotalItems == 0 ? 0 : ((CurrentPage - 1) * PageSize) + 1;
    public int EndItem => TotalItems == 0 ? 0 : Math.Min(CurrentPage * PageSize, TotalItems);
}

public sealed class KvhSubscriptionOperationBatchListItem
{
    public long Id { get; set; }
    public string BatchCode { get; set; } = string.Empty;
    public string BatchName { get; set; } = string.Empty;
    public string OperationType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public int TotalItems { get; set; }
    public int DraftItems { get; set; }
    public int QueuedItems { get; set; }
    public int SubmittingItems { get; set; }
    public int PendingItems { get; set; }
    public int JobSuccessItems { get; set; }
    public int JobFailedItems { get; set; }
    public int VerifiedItems { get; set; }
    public int VerificationMismatchItems { get; set; }
    public int SkippedItems { get; set; }
    public int CancelledItems { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int CompletedItems => VerifiedItems + JobFailedItems + VerificationMismatchItems + SkippedItems + CancelledItems;
    public int ProgressPercent => TotalItems <= 0 ? 0 : Math.Min(100, (int)Math.Round(CompletedItems * 100.0 / TotalItems));
    public bool CanStart => Status is KvhSubscriptionOperationBatchStatuses.Draft or KvhSubscriptionOperationBatchStatuses.Ready;
    public bool CanEdit => Status == KvhSubscriptionOperationBatchStatuses.Draft;
    public bool CanCancel => Status is KvhSubscriptionOperationBatchStatuses.Queued or KvhSubscriptionOperationBatchStatuses.Running or KvhSubscriptionOperationBatchStatuses.Verifying;
    public string CreatedDisplay => FormatUtc(CreatedAtUtc);
    public string StartedDisplay => FormatUtc(StartedAtUtc);
    public string CompletedDisplay => FormatUtc(CompletedAtUtc);

    private static string FormatUtc(DateTime? value) =>
        value.HasValue ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time")).ToString("dd/MM/yyyy HH:mm") : "-";
}

public sealed class KvhSubscriptionOperationCreateRequest
{
    public string BatchName { get; set; } = string.Empty;
    public string OperationType { get; set; } = KvhSubscriptionOperationTypes.Pause;
    public int? TenantId { get; set; }
    public DateTime? ScheduledStartAtUtc { get; set; }
    public string? Description { get; set; }
}

public sealed class KvhSubscriptionOperationAddDevicesRequest
{
    public List<int> DeviceIds { get; set; } = [];
}

public sealed class KvhSubscriptionOperationRemoveItemRequest
{
    public long ItemId { get; set; }
}

public sealed class KvhSubscriptionOperationDetailViewModel
{
    public long Id { get; set; }
    public int? TenantId { get; set; }
    public string BatchCode { get; set; } = string.Empty;
    public string BatchName { get; set; } = string.Empty;
    public string OperationType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ScheduledStartAtUtc { get; set; }
    public int TotalItems { get; set; }
    public int ReadyItems { get; set; }
    public int QueuedItems { get; set; }
    public int PendingItems { get; set; }
    public int JobSuccessItems { get; set; }
    public int JobFailedItems { get; set; }
    public int VerifyingItems { get; set; }
    public int VerifiedItems { get; set; }
    public int VerificationMismatchItems { get; set; }
    public int SkippedItems { get; set; }
    public int CancelledItems { get; set; }
    public bool CanManage { get; set; }
    public List<KvhSubscriptionOperationItemViewModel> Items { get; set; } = [];
    public List<KvhSubscriptionOperationDeviceOption> DeviceOptions { get; set; } = [];
    public int CompletedItems => VerifiedItems + JobFailedItems + VerificationMismatchItems + SkippedItems + CancelledItems;
    public int ProcessingItems => QueuedItems + PendingItems + JobSuccessItems + VerifyingItems;
    public int ProgressPercent => TotalItems <= 0 ? 0 : Math.Min(100, (int)Math.Round(CompletedItems * 100.0 / TotalItems));
    public bool CanStart => CanManage && Status is (KvhSubscriptionOperationBatchStatuses.Draft or KvhSubscriptionOperationBatchStatuses.Ready);
    public bool CanEdit => CanManage && Status == KvhSubscriptionOperationBatchStatuses.Draft;
    public bool CanCancel => CanManage && Status is (KvhSubscriptionOperationBatchStatuses.Queued or KvhSubscriptionOperationBatchStatuses.Running or KvhSubscriptionOperationBatchStatuses.Verifying);
    public string CreatedDisplay => FormatUtc(CreatedAtUtc);
    public string ScheduledStartDisplay => FormatUtc(ScheduledStartAtUtc);

    private static string FormatUtc(DateTime? value) =>
        value.HasValue ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time")).ToString("dd/MM/yyyy HH:mm") : "-";
}

public sealed class KvhSubscriptionOperationItemViewModel
{
    public long Id { get; set; }
    public int? DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public string KitNumber { get; set; } = string.Empty;
    public string TerminalId { get; set; } = string.Empty;
    public string TrafficId { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string SubscriptionStatus { get; set; } = string.Empty;
    public string ScheduledAction { get; set; } = string.Empty;
    public string OperationType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long? KvhCommandId { get; set; }
    public string JobId { get; set; } = string.Empty;
    public string JobStatus { get; set; } = string.Empty;
    public string VerificationStatus { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public int PollCount { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public int? HttpStatusCode { get; set; }
    public string SubmitResponseJson { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty;
    public string CommandSubmitResponseJson { get; set; } = string.Empty;
    public string JobResponseJson { get; set; } = string.Empty;
    public string VerificationResponseJson { get; set; } = string.Empty;
    public string OperationLogJson { get; set; } = string.Empty;
    public string UpdatedDisplay => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(UpdatedAtUtc, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time")).ToString("dd/MM/yyyy HH:mm");
}

public sealed class KvhSubscriptionOperationDeviceOption
{
    public int DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public string KitNumber { get; set; } = string.Empty;
    public string TerminalId { get; set; } = string.Empty;
    public string TrafficId { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string SubscriptionStatus { get; set; } = string.Empty;
    public string ScheduledAction { get; set; } = string.Empty;
    public string Availability { get; set; } = string.Empty;
}

public sealed class KvhSubscriptionOperationImportPreview
{
    public string PreviewToken { get; set; } = string.Empty;
    public int TotalRows { get; set; }
    public int ValidRows { get; set; }
    public int WarningRows { get; set; }
    public int ErrorRows { get; set; }
    public int DuplicateRows { get; set; }
    public int UnknownKitRows { get; set; }
    public List<KvhSubscriptionOperationImportRow> Rows { get; set; } = [];
}

public sealed class KvhSubscriptionOperationImportRow
{
    public int RowNumber { get; set; }
    public string KitNumber { get; set; } = string.Empty;
    public string TerminalId { get; set; } = string.Empty;
    public string TrafficId { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string OperationType { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public bool IsDuplicate { get; set; }
    public bool HasWarning { get; set; }
    public string Message { get; set; } = string.Empty;
    public int? DeviceId { get; set; }
    public long? KvhSubscriptionId { get; set; }
}
