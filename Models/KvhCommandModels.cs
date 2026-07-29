namespace StarlinkDeviceManager.Models;

public static class KvhCommandTypes
{
    public const string DataOptIn = "DATA_OPT_IN";
    public const string WifiUpdate = "WIFI_UPDATE";
    public const string Reboot = "REBOOT";
    public const string SubscriptionPause = "SUBSCRIPTION_PAUSE";
    public const string SubscriptionResume = "SUBSCRIPTION_RESUME";
    public const string SubscriptionCancelSchedule = "SUBSCRIPTION_CANCEL_SCHEDULE";
}

public static class KvhCommandStatuses
{
    public const string Submitting = "SUBMITTING";
    public const string Submitted = "SUBMITTED";
    public const string Pending = "PENDING";
    public const string Success = "SUCCESS";
    public const string Failed = "FAILED";
    public const string Timeout = "TIMEOUT";
    public const string Verifying = "VERIFYING";
    public const string Verified = "VERIFIED";
    public const string VerificationMismatch = "VERIFICATION_MISMATCH";
    public const string VerificationTimeout = "VERIFICATION_TIMEOUT";
    public const string Unknown = "UNKNOWN";
}

public static class KvhJobStatuses
{
    public const string Submitted = "SUBMITTED";
    public const string Pending = "Pending";
    public const string Success = "Success";
    public const string Failed = "Failed";
    public const string Unknown = "Unknown";
}

public static class KvhVerificationStatuses
{
    public const string Pending = "Pending";
    public const string Success = "Success";
    public const string Failed = "Failed";
    public const string Mismatch = "Mismatch";
    public const string Timeout = "Timeout";
    public const string Unknown = "Unknown";
}

public static class KvhErrorCodes
{
    public const string TerminalCommandCooldown = "kvh_terminal_command_cooldown";
    public const string CommandSubmitFailed = "kvh_command_submit_failed";
    public const string MissingJobId = "missing_kvh_job_id";
    public const string JobNotFound = "kvh_job_not_found";
    public const string JobApiError = "kvh_job_api_error";
    public const string JobInvalidJson = "kvh_job_invalid_json";
    public const string JobFailed = "kvh_job_failed";
    public const string JobTimeout = "kvh_job_timeout";
    public const string VerificationFailed = "kvh_verification_failed";
    public const string VerificationMismatch = "kvh_verification_mismatch";
    public const string TokenRefreshFailed = "kvh_token_refresh_failed";
    public const string TelemetryRateLimited = "telemetry_rate_limited";
    public const string TelemetryApiError = "telemetry_api_error";
    public const string TelemetryTimeout = "telemetry_timeout";
    public const string SubscriptionVerificationFailed = "kvh_subscription_verification_failed";
}

public sealed class KvhCommand
{
    public long Id { get; set; }
    public int DeviceId { get; set; }
    public string TerminalId { get; set; } = string.Empty;
    public string KvhDeviceId { get; set; } = string.Empty;
    public string TrafficId { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string ScheduleId { get; set; } = string.Empty;
    public long? KvhSubscriptionId { get; set; }
    public DateTime? CooldownUntilUtc { get; set; }
    public string CommandType { get; set; } = string.Empty;
    public string RequestedValue { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public int? HttpStatusCode { get; set; }
    public string CommandStatus { get; set; } = string.Empty;
    public string JobStatus { get; set; } = string.Empty;
    public string VerificationStatus { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty;
    public string SubmitResponseJson { get; set; } = string.Empty;
    public string JobResponseJson { get; set; } = string.Empty;
    public string VerificationResponseJson { get; set; } = string.Empty;
    public int? RequestedByUserId { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? LastPolledAtUtc { get; set; }
    public DateTime? NextPollAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? VerifiedAtUtc { get; set; }
    public int PollCount { get; set; }
    public int MaxPollCount { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public int VerificationAttemptCount { get; set; }
    public DateTime? NextVerificationAtUtc { get; set; }
}

public sealed class KvhCommandStatusDto
{
    public long Id { get; set; }
    public int DeviceId { get; set; }
    public string TerminalId { get; set; } = string.Empty;
    public string KvhDeviceId { get; set; } = string.Empty;
    public string CommandType { get; set; } = string.Empty;
    public string CommandStatus { get; set; } = string.Empty;
    public string JobStatus { get; set; } = string.Empty;
    public string VerificationStatus { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? LastPolledAtUtc { get; set; }
    public DateTime? NextPollAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? VerifiedAtUtc { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
}

public sealed class KvhCommandSubmitResult
{
    public bool Success { get; set; }
    public bool Unchanged { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string MessageEn { get; set; } = string.Empty;
    public int DeviceId { get; set; }
    public string TerminalId { get; set; } = string.Empty;
    public string KvhDeviceId { get; set; } = string.Empty;
    public long? CommandId { get; set; }
    public string JobId { get; set; } = string.Empty;
    public int? HttpStatusCode { get; set; }
    public string RawResponse { get; set; } = string.Empty;
    public bool? OldDataOptInStatus { get; set; }
    public bool? NewDataOptInStatus { get; set; }
    public int? RemainingSeconds { get; set; }
    public DateTime? NextAllowedAtUtc { get; set; }
}

public sealed class KvhJobMonitorOptions
{
    public const string SectionName = "KvhJobMonitor";
    public bool Enabled { get; set; } = true;
    public int WorkerIntervalSeconds { get; set; } = 10;
    public int BatchSize { get; set; } = 20;
    public int InitialPollDelaySeconds { get; set; } = 5;
    public int MaxPollCount { get; set; } = 40;
    public int CommandTimeoutMinutes { get; set; } = 15;
    public int TerminalCommandCooldownMinutes { get; set; } = 5;
    public int RebootVerificationTimeoutMinutes { get; set; } = 10;
    public int TelemetryTimeoutSeconds { get; set; } = 20;
    public int TelemetryMaxRangeSeconds { get; set; } = 86400;
}
