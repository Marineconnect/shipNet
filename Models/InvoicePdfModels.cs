using Microsoft.AspNetCore.Http;

namespace StarlinkDeviceManager.Models;

public sealed class InvoicePdfIntegrationOptions
{
    public const string SectionName = "InvoicePdfIntegration";

    public bool Enabled { get; set; }
    public string PublicBaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string HeaderName { get; set; } = "X-ShipNet-Api-Key";
}

public sealed class InvoicePdfStorageOptions
{
    public const string SectionName = "InvoicePdfStorage";

    public string RootPath { get; set; } = string.Empty;
    public int MaxFileSizeMb { get; set; } = 20;
}

public sealed class InvoicePdfUploadRequest
{
    public string InvoiceCode { get; set; } = string.Empty;
    public IFormFile? File { get; set; }
    public string TransactionCode { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public DateTime? GeneratedAt { get; set; }
    public string ExternalReference { get; set; } = string.Empty;
    public int? UploadedByUserId { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
    public int? AllowedTenantId { get; set; }
    public int? AllowedDeviceId { get; set; }
}

public sealed class InvoicePdfUploadResult
{
    public bool Success { get; set; } = true;
    public int InvoiceId { get; set; }
    public string InvoiceCode { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string ContentType { get; set; } = "application/pdf";
    public DateTime UploadedAt { get; set; }
    public int Version { get; set; }
    public bool Replaced { get; set; }
    public bool Unchanged { get; set; }
    public string ViewUrl { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
}

public sealed class InvoicePdfFileViewModel
{
    public bool Available { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/pdf";
    public long Size { get; set; }
    public string SizeDisplay { get; set; } = string.Empty;
    public int Version { get; set; }
    public string SourceSystem { get; set; } = string.Empty;
    public DateTime? UploadedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
    public string ViewUrl { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public bool CanReplace { get; set; }
    public bool CanDelete { get; set; }
}

public sealed class InvoicePdfRecord
{
    public long Id { get; set; }
    public int InvoiceId { get; set; }
    public int SubscriptionId { get; set; }
    public int TenantId { get; set; }
    public int DeviceId { get; set; }
    public string InvoiceCode { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/pdf";
    public long FileSize { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public int Version { get; set; }
    public string SourceSystem { get; set; } = string.Empty;
    public string ExternalReference { get; set; } = string.Empty;
    public int? UploadedByUserId { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
    public DateTime UploadedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class InvoicePdfOpenResult
{
    public InvoicePdfRecord Record { get; set; } = new();
    public Stream Stream { get; set; } = Stream.Null;
}

public sealed class InvoicePdfError(string errorCode, string message, string messageEn, int statusCode) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
    public string MessageEn { get; } = messageEn;
    public int StatusCode { get; } = statusCode;
}

public sealed class InvoiceIntegrationLogOptions
{
    public const string SectionName = "InvoiceIntegrationLog";

    public int RetentionDays { get; set; } = 180;
    public int MaxPayloadDisplayLength { get; set; } = 200000;
}

public sealed class InvoiceIntegrationLogEntry
{
    public long Id { get; set; }
    public int? InvoiceId { get; set; }
    public string InvoiceCode { get; set; } = string.Empty;
    public string TransactionCode { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public string TargetSystem { get; set; } = string.Empty;
    public string RabbitExchange { get; set; } = string.Empty;
    public string RabbitRoutingKey { get; set; } = string.Empty;
    public string RabbitQueue { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string FileOriginalName { get; set; } = string.Empty;
    public string FileStoredName { get; set; } = string.Empty;
    public long? FileSize { get; set; }
    public int? FileVersion { get; set; }
    public int? HttpStatusCode { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public long? DurationMs { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

public sealed class InvoiceIntegrationLogListItem
{
    public long Id { get; set; }
    public string InvoiceCode { get; set; } = string.Empty;
    public string TransactionCode { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public string TargetSystem { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileSizeDisplay { get; set; } = string.Empty;
    public int? FileVersion { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedAtDisplay => CreatedAtUtc == DateTime.MinValue ? "-" : CreatedAtUtc.ToString("dd/MM/yyyy HH:mm:ss");
    public bool HasPayload { get; set; }
}
