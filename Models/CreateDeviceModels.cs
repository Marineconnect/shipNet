namespace StarlinkDeviceManager.Models;

public class CreateDeviceRequest
{
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceCode { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public int? TenantId { get; set; }
}

public class CreateDeviceResult
{
    public bool Success { get; set; }
    public bool IsDuplicate { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string MessageEn { get; set; } = string.Empty;
    public string ApiResult { get; set; } = string.Empty;
    public int? DeviceId { get; set; }
}

public class UpdateDeviceRequest
{
    public int Id { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceCode { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public int? TenantId { get; set; }
}

public class UpdateDeviceResult
{
    public bool Success { get; set; }
    public bool IsDuplicate { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string MessageEn { get; set; } = string.Empty;
    public string ApiResult { get; set; } = string.Empty;
    public int? DeviceId { get; set; }
}

public class DeleteDeviceResult
{
    public bool Success { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string MessageEn { get; set; } = string.Empty;
}

public class UpdateDeviceWifiRequest
{
    public int Id { get; set; }
    public string Ssid { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}

public class DeviceCommandResult
{
    public bool Success { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string MessageEn { get; set; } = string.Empty;
    public string RawResponse { get; set; } = string.Empty;
    public string TerminalId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
}

public class RefreshDeviceResult
{
    public bool Success { get; set; }
    public bool Refreshed { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string MessageEn { get; set; } = string.Empty;
    public string ApiResult { get; set; } = string.Empty;
    public int DeviceId { get; set; }
    public string Availability { get; set; } = string.Empty;
    public string LastUpdateTimeVietnam { get; set; } = "-";
    public string TokenExpiredTimeVietnam { get; set; } = "-";
    public string TokenExpiredTimeUtc { get; set; } = string.Empty;
    public string LastSysnTimeVietnam { get; set; } = "-";
    public string UsageDataDisplay { get; set; } = "-";
    public string PriorityDataDisplay { get; set; } = "-";
    public string PlanName { get; set; } = string.Empty;
}
