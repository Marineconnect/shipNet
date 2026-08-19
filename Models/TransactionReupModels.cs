using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace StarlinkDeviceManager.Models;

public static class TransactionReupStatuses
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Published = "Published";
    public const string PublishFailed = "PublishFailed";
    public const string WaitingPdf = "WaitingPdf";
    public const string Done = "Done";
    public const string Error = "Error";
    public const string Invalid = "Invalid";
    public const string Duplicate = "Duplicate";
    public const string Skipped = "Skipped";
}

public static class TransactionReupSourceTypes
{
    public const string ExcelImport = "EXCEL_IMPORT";
    public const string TransactionSelection = "TRANSACTION_SELECTION";
}

public sealed class TransactionReupImportViewModel
{
    [Required]
    public IFormFile? File { get; set; }

    [Range(1, int.MaxValue)]
    public int StartInvoiceNumber { get; set; }
}

public sealed class TransactionReupWorkerResultRequest
{
    public string TransactionCode { get; set; } = string.Empty;
    public string InvoiceCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string LogFile { get; set; } = string.Empty;
    public string LogContent { get; set; } = string.Empty;
    public string OutputFile { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = "InvoiceWorker";
    public string RawMessage { get; set; } = string.Empty;
}

public sealed class TransactionReupItemResultRequest
{
    public string Status { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string ExternalReference { get; set; } = string.Empty;
    public string InvoiceCode { get; set; } = string.Empty;
    public string TransactionCode { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = "InvoiceWorker";
}

public sealed class TransactionReupIndexViewModel
{
    public List<TransactionReupBatchViewModel> Batches { get; set; } = [];
    public TransactionReupImportViewModel Import { get; set; } = new();
    public string Message { get; set; } = string.Empty;
    public bool IsSuccess { get; set; }
}

public sealed class TransactionReupBatchViewModel
{
    public int Id { get; set; }
    public string BatchCode { get; set; } = string.Empty;
    public string SourceType { get; set; } = TransactionReupSourceTypes.ExcelImport;
    public string OriginalFileName { get; set; } = string.Empty;
    public string ImportedByUsername { get; set; } = string.Empty;
    public DateTime ImportedAtUtc { get; set; }
    public int InvoiceStartNumber { get; set; }
    public int InvoiceEndNumber { get; set; }
    public int NextInvoiceNumber { get; set; }
    public int TotalRows { get; set; }
    public int ValidRows { get; set; }
    public int PublishedRows { get; set; }
    public int FailedRows { get; set; }
    public int SkippedRows { get; set; }
    public int DuplicateRows { get; set; }
    public string Status { get; set; } = string.Empty;
    public string ImportedAtDisplay => ImportedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
    public bool IsTransactionSelection => string.Equals(SourceType, TransactionReupSourceTypes.TransactionSelection, StringComparison.OrdinalIgnoreCase);
    public string SourceDisplay => IsTransactionSelection ? "Transaction History" : "Excel Import";
}

public sealed class TransactionReupDetailsViewModel
{
    public TransactionReupBatchViewModel Batch { get; set; } = new();
    public List<TransactionReupItemViewModel> Items { get; set; } = [];
}

public sealed class TransactionReupItemViewModel
{
    public int Id { get; set; }
    public int SourceInvoiceId { get; set; }
    public int RowNumber { get; set; }
    public string SourceTransactionCode { get; set; } = string.Empty;
    public string SourceRequestCode { get; set; } = string.Empty;
    public string InvoiceCode { get; set; } = string.Empty;
    public decimal GrossAmountVnd { get; set; }
    public string ValidationStatus { get; set; } = string.Empty;
    public string PublishStatus { get; set; } = string.Empty;
    public int PublishAttemptCount { get; set; }
    public string RabbitMessageId { get; set; } = string.Empty;
    public string RabbitCorrelationId { get; set; } = string.Empty;
    public string PublishMessage { get; set; } = string.Empty;
    public string PublishLogs { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string PdfFileName { get; set; } = string.Empty;
    public string PdfStorageKey { get; set; } = string.Empty;
    public long PdfSize { get; set; }
    public string PdfSha256 { get; set; } = string.Empty;
    public string PdfContentType { get; set; } = string.Empty;
    public DateTime? PdfReceivedAtUtc { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTime? ProcessingStartedAtUtc { get; set; }
    public DateTime? WaitingPdfAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string TransactionType { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
    public string BankName { get; set; } = string.Empty;
    public decimal ProcessingFeeVnd { get; set; }
    public decimal NetAmountVnd { get; set; }
    public string SourceStatus { get; set; } = string.Empty;
    public DateTime? PublishedAtUtc { get; set; }
    public string PublishedAtDisplay => PublishedAtUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") ?? "-";
    public string CompletedAtDisplay => CompletedAtUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") ?? "-";
    public bool HasPdf => !string.IsNullOrWhiteSpace(PdfStorageKey);
    public bool CanRetry =>
        string.Equals(PublishStatus, TransactionReupStatuses.PublishFailed, StringComparison.OrdinalIgnoreCase)
        || string.Equals(PublishStatus, TransactionReupStatuses.Error, StringComparison.OrdinalIgnoreCase);
}

public sealed record TransactionReupSourceRow(
    int RowNumber,
    string CreatedAtText,
    string UpdatedAtText,
    string TransactionCode,
    string RequestInvoiceCode,
    string SourceOriginalRequestCode,
    string SourceCreatedBy,
    string TransactionType,
    string PaymentMethod,
    string BankName,
    decimal TotalAmountVnd,
    decimal ProcessingFee,
    string TransferContent,
    string FeeBearer,
    decimal NetAmountVnd,
    string SourceStatus,
    IReadOnlyDictionary<string, string> Values);

public sealed record TransactionReupImportResult(
    int BatchId,
    string Message,
    int FirstInvoiceNumber,
    int LastInvoiceNumber,
    int NextInvoiceNumber);

public sealed class TransactionReupSelectionRequest
{
    public string SelectionMode { get; set; } = "selected";
    public List<int> InvoiceIds { get; set; } = [];
    public PaymentTransactionFilterViewModel Filter { get; set; } = new();
}

public sealed record TransactionReupSelectionResult(
    int BatchId,
    string BatchCode,
    int RequestedCount,
    int AuthorizedCount,
    int CreatedCount,
    string Message);

public sealed record TransactionReupPdfCallbackResult(
    int ItemId,
    string InvoiceCode,
    string TransactionCode,
    string FileName,
    long FileSize,
    string Sha256,
    DateTime ReceivedAtUtc);

public sealed class TransactionReupItemPdfOpenResult
{
    public TransactionReupItemViewModel Item { get; set; } = new();
    public Stream Stream { get; set; } = Stream.Null;
}
