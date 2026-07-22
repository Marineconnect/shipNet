using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public sealed class InvoicePdfService(
    IConfiguration configuration,
    IOptions<InvoicePdfIntegrationOptions> integrationOptions,
    IOptions<InvoicePdfStorageOptions> storageOptions,
    IInvoicePdfStorage storage,
    ILogger<InvoicePdfService> logger) : IInvoicePdfService
{
    private readonly string connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is missing.");
    private readonly InvoicePdfIntegrationOptions integration = integrationOptions.Value;
    private readonly InvoicePdfStorageOptions storageSettings = storageOptions.Value;

    public string BuildUploadUrl(string invoiceCode)
    {
        if (string.IsNullOrWhiteSpace(invoiceCode))
        {
            throw new InvalidOperationException("Invoice code is required to build InvoiceURL.");
        }

        var rawBaseUrl = integration.PublicBaseUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawBaseUrl) ||
            rawBaseUrl.Contains("example", StringComparison.OrdinalIgnoreCase) ||
            rawBaseUrl.Contains("placeholder", StringComparison.OrdinalIgnoreCase) ||
            rawBaseUrl.Contains("DOMAIN-", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("InvoicePdfIntegration:PublicBaseUrl is required and must be a real public base URL.");
        }

        if (!Uri.TryCreate(rawBaseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("InvoicePdfIntegration:PublicBaseUrl must be an absolute URL.");
        }

        if (!string.IsNullOrWhiteSpace(baseUri.Query) || !string.IsNullOrWhiteSpace(baseUri.Fragment))
        {
            throw new InvalidOperationException("InvoicePdfIntegration:PublicBaseUrl must not contain query string or fragment.");
        }

        if (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !baseUri.IsLoopback)
        {
            throw new InvalidOperationException("InvoicePdfIntegration:PublicBaseUrl must use HTTPS except for localhost development.");
        }

        var normalizedBaseUrl = rawBaseUrl.TrimEnd('/') + "/";
        var normalizedBaseUri = new Uri(normalizedBaseUrl, UriKind.Absolute);
        return new Uri(normalizedBaseUri, $"api/invoices/{Uri.EscapeDataString(invoiceCode.Trim())}/pdf").AbsoluteUri;
    }

    public async Task<InvoicePdfUploadResult> UploadAsync(InvoicePdfUploadRequest request, CancellationToken cancellationToken = default)
    {
        var invoiceCode = NormalizeInvoiceCode(request.InvoiceCode);
        ValidateUploadRequest(invoiceCode, request.File);

        var tempPath = Path.GetTempFileName();
        string sha256;
        try
        {
            await using (var input = request.File!.OpenReadStream())
            await using (var temp = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[81920];
                int read;
                long total = 0;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    total += read;
                    if (total > MaxFileSizeBytes())
                    {
                        throw PdfError("file_too_large", "File PDF vượt quá dung lượng cho phép.", "The PDF file exceeds the allowed size.", StatusCodes.Status413PayloadTooLarge);
                    }

                    await temp.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                }

                sha.TransformFinalBlock([], 0, 0);
                sha256 = Convert.ToHexString(sha.Hash ?? []).ToLowerInvariant();
                await temp.FlushAsync(cancellationToken);
            }

            await ValidatePdfMagicBytesAsync(tempPath, cancellationToken);

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await EnsureSchemaAsync(connection, transaction, cancellationToken);

            var invoice = await ResolveInvoiceAsync(connection, transaction, invoiceCode, request.AllowedTenantId, request.AllowedDeviceId, cancellationToken);
            var current = await GetCurrentRecordByInvoiceIdAsync(connection, transaction, invoice.InvoiceId, cancellationToken);
            if (current is not null && string.Equals(current.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            {
                await transaction.CommitAsync(cancellationToken);
                return BuildUploadResult(invoiceCode, current, replaced: false, unchanged: true);
            }

            var nextVersion = await GetNextVersionAsync(connection, transaction, invoice.InvoiceId, cancellationToken);
            var safeFileName = BuildSafeFileName(invoiceCode, nextVersion);
            var storageKey = BuildStorageKey(invoice.InvoiceId, nextVersion, safeFileName, DateTime.UtcNow);
            await using (var source = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await storage.SaveAsync(source, storageKey, cancellationToken);
            }

            InvoicePdfRecord? savedRecord = null;
            try
            {
                await MarkOldVersionsNotCurrentAsync(connection, transaction, invoice.InvoiceId, cancellationToken);
                savedRecord = await InsertRecordAsync(connection, transaction, invoice, request, invoiceCode, safeFileName, storageKey, sha256, nextVersion, cancellationToken);
                await InsertAuditAsync(connection, transaction, request.UploadedByUserId, invoice.SubscriptionId, current is null ? "InvoicePdfUploaded" : "InvoicePdfReplaced", invoiceCode, request.UploadedBy, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await storage.DeleteAsync(storageKey, cancellationToken);
                throw;
            }

            return BuildUploadResult(invoiceCode, savedRecord, replaced: current is not null, unchanged: false);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public async Task<InvoicePdfOpenResult?> OpenReadAsync(string invoiceCode, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);
        var invoice = await ResolveInvoiceAsync(connection, transaction, NormalizeInvoiceCode(invoiceCode), tenantId, deviceId, cancellationToken);
        var record = await GetCurrentRecordByInvoiceIdAsync(connection, transaction, invoice.InvoiceId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (record is null)
        {
            return null;
        }

        var stream = await storage.OpenReadAsync(record.StorageKey, cancellationToken);
        if (stream is null)
        {
            logger.LogWarning("Invoice PDF metadata exists but physical file is missing. InvoiceId={InvoiceId}; InvoiceCode={InvoiceCode}; Version={Version}.", record.InvoiceId, record.InvoiceCode, record.Version);
            return null;
        }

        return new InvoicePdfOpenResult { Record = record, Stream = stream };
    }

    public async Task<InvoicePdfFileViewModel> GetCurrentFileViewModelAsync(string invoiceCode, bool canReplace, bool canDelete, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);
        var invoice = await ResolveInvoiceAsync(connection, transaction, NormalizeInvoiceCode(invoiceCode), tenantId, deviceId, cancellationToken);
        var record = await GetCurrentRecordByInvoiceIdAsync(connection, transaction, invoice.InvoiceId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record is null ? EmptyFile(canReplace) : ToViewModel(record, canReplace, canDelete);
    }

    public async Task<Dictionary<int, InvoicePdfFileViewModel>> GetCurrentFilesForSubscriptionAsync(int subscriptionId, bool canReplace, bool canDelete, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        const string query = """
            SELECT p.[ID], p.[InvoiceId], p.[InvoiceCode], p.[FileName], p.[OriginalFileName], p.[StorageKey],
                   p.[ContentType], p.[FileSize], p.[Sha256], p.[Version], p.[SourceSystem], p.[ExternalReference],
                   p.[UploadedByUserId], p.[UploadedBy], p.[UploadedAtUtc], p.[UpdatedAtUtc],
                   i.[SubscriptionId], s.[TenantId], s.[DeviceId]
            FROM [dbo].[TblInvoicePdf] p
            INNER JOIN [dbo].[TblSubscriptionInvoice] i ON i.[ID] = p.[InvoiceId]
            INNER JOIN [dbo].[TblMonthlySubscription] s ON s.[ID] = i.[SubscriptionId]
            WHERE i.[SubscriptionId] = @subscriptionId
              AND p.[IsCurrent] = 1
              AND p.[IsDeleted] = 0;
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@subscriptionId", SqlDbType.Int).Value = subscriptionId;
        var result = new Dictionary<int, InvoicePdfFileViewModel>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var record = MapRecord(reader);
            result[record.InvoiceId] = ToViewModel(record, canReplace, canDelete);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task DeleteAsync(string invoiceCode, int? userId, string username, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);
        var invoice = await ResolveInvoiceAsync(connection, transaction, NormalizeInvoiceCode(invoiceCode), tenantId, deviceId, cancellationToken);
        var record = await GetCurrentRecordByInvoiceIdAsync(connection, transaction, invoice.InvoiceId, cancellationToken);
        if (record is null)
        {
            throw PdfError("pdf_not_found", "Invoice chưa có file PDF.", "No PDF file has been uploaded for this invoice.", StatusCodes.Status404NotFound);
        }

        const string update = """
            UPDATE [dbo].[TblInvoicePdf]
            SET [IsCurrent] = 0, [IsDeleted] = 1, [DeletedAtUtc] = SYSUTCDATETIME(), [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [ID] = @id;
            """;
        await using (var command = new SqlCommand(update, connection, transaction))
        {
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = record.Id;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(connection, transaction, userId, invoice.SubscriptionId, "InvoicePdfDeleted", invoiceCode, username, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await storage.DeleteAsync(record.StorageKey, cancellationToken);
    }

    private void ValidateUploadRequest(string invoiceCode, IFormFile? file)
    {
        if (string.IsNullOrWhiteSpace(invoiceCode))
        {
            throw PdfError("invalid_invoice_code", "Mã invoice không hợp lệ.", "Invoice code is invalid.", StatusCodes.Status400BadRequest);
        }

        if (file is null)
        {
            throw PdfError("missing_file", "Thiếu file PDF.", "PDF file is required.", StatusCodes.Status400BadRequest);
        }

        if (file.Length <= 0)
        {
            throw PdfError("empty_file", "File PDF rỗng.", "PDF file is empty.", StatusCodes.Status400BadRequest);
        }

        if (file.Length > MaxFileSizeBytes())
        {
            throw PdfError("file_too_large", "File PDF vượt quá dung lượng cho phép.", "The PDF file exceeds the allowed size.", StatusCodes.Status413PayloadTooLarge);
        }

        var extension = Path.GetExtension(file.FileName);
        if (!string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw PdfError("invalid_pdf", "File không phải định dạng PDF hợp lệ.", "The uploaded file is not a valid PDF.", StatusCodes.Status415UnsupportedMediaType);
        }

        var contentType = file.ContentType?.Trim() ?? string.Empty;
        if (!string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            throw PdfError("invalid_pdf", "Content-Type của file không hợp lệ.", "The uploaded file content type is not valid for PDF.", StatusCodes.Status415UnsupportedMediaType);
        }
    }

    private static async Task ValidatePdfMagicBytesAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[5];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 5, FileOptions.Asynchronous);
        var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        if (read < 5 || Encoding.ASCII.GetString(buffer) != "%PDF-")
        {
            throw PdfError("invalid_pdf", "File không phải định dạng PDF hợp lệ.", "The uploaded file is not a valid PDF.", StatusCodes.Status415UnsupportedMediaType);
        }
    }

    private long MaxFileSizeBytes()
    {
        var maxMb = Math.Clamp(storageSettings.MaxFileSizeMb <= 0 ? 20 : storageSettings.MaxFileSizeMb, 1, 200);
        return maxMb * 1024L * 1024L;
    }

    private static string NormalizeInvoiceCode(string value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string BuildSafeFileName(string invoiceCode, int version)
    {
        var safe = new string(invoiceCode.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray()).Trim('-');
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "invoice";
        }

        return version <= 1 ? $"{safe}.pdf" : $"{safe}-v{version}.pdf";
    }

    private static string BuildStorageKey(int invoiceId, int version, string fileName, DateTime utcNow)
    {
        return string.Join('/',
            utcNow.Year.ToString(CultureInfo.InvariantCulture),
            utcNow.Month.ToString("00", CultureInfo.InvariantCulture),
            invoiceId.ToString(CultureInfo.InvariantCulture),
            version.ToString(CultureInfo.InvariantCulture),
            fileName);
    }

    private static InvoicePdfUploadResult BuildUploadResult(string invoiceCode, InvoicePdfRecord record, bool replaced, bool unchanged)
    {
        return new InvoicePdfUploadResult
        {
            InvoiceCode = invoiceCode,
            FileName = record.FileName,
            FileSize = record.FileSize,
            ContentType = record.ContentType,
            UploadedAt = record.UploadedAtUtc,
            Version = record.Version,
            Replaced = replaced,
            Unchanged = unchanged,
            ViewUrl = $"/api/invoices/{Uri.EscapeDataString(invoiceCode)}/pdf/file",
            DownloadUrl = $"/api/invoices/{Uri.EscapeDataString(invoiceCode)}/pdf/file?download=true"
        };
    }

    private static InvoicePdfFileViewModel EmptyFile(bool canReplace)
    {
        return new InvoicePdfFileViewModel
        {
            Available = false,
            CanReplace = canReplace,
            CanDelete = false
        };
    }

    private static InvoicePdfFileViewModel ToViewModel(InvoicePdfRecord record, bool canReplace, bool canDelete)
    {
        var code = Uri.EscapeDataString(record.InvoiceCode);
        return new InvoicePdfFileViewModel
        {
            Available = true,
            FileName = record.FileName,
            ContentType = record.ContentType,
            Size = record.FileSize,
            SizeDisplay = FormatBytes(record.FileSize),
            Version = record.Version,
            SourceSystem = string.IsNullOrWhiteSpace(record.SourceSystem) ? "-" : record.SourceSystem,
            UploadedAt = record.UploadedAtUtc,
            UpdatedAt = record.UpdatedAtUtc,
            UploadedBy = string.IsNullOrWhiteSpace(record.UploadedBy) ? "-" : record.UploadedBy,
            ViewUrl = $"/api/invoices/{code}/pdf/file",
            DownloadUrl = $"/api/invoices/{code}/pdf/file?download=true",
            CanReplace = canReplace,
            CanDelete = canDelete
        };
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:#,##0.##} {units[unit]}";
    }

    private async Task<InvoiceIdentity> ResolveInvoiceAsync(SqlConnection connection, SqlTransaction transaction, string invoiceCode, int? tenantId, int? deviceId, CancellationToken cancellationToken)
    {
        const string query = """
            WITH invoice_codes AS (
                SELECT
                    i.[ID] AS [InvoiceId],
                    i.[SubscriptionId],
                    i.[InvoiceNumber],
                    s.[TenantId],
                    s.[DeviceId],
                    CONCAT(N'SHIPNET-INV-', YEAR(i.[CreatedAt]), N'-', RIGHT(N'00000' + CONVERT(nvarchar(20), ROW_NUMBER() OVER (PARTITION BY YEAR(i.[CreatedAt]) ORDER BY i.[CreatedAt], i.[ID])), 5)) AS [GeneratedInvoiceCode]
                FROM [dbo].[TblSubscriptionInvoice] i
                INNER JOIN [dbo].[TblMonthlySubscription] s ON s.[ID] = i.[SubscriptionId]
            )
            SELECT [InvoiceId], [SubscriptionId], [InvoiceNumber], [GeneratedInvoiceCode], [TenantId], [DeviceId]
            FROM invoice_codes
            WHERE ([InvoiceNumber] = @invoiceCode OR [GeneratedInvoiceCode] = @invoiceCode)
              AND (@tenantId IS NULL OR [TenantId] = @tenantId)
              AND (@deviceId IS NULL OR [DeviceId] = @deviceId);
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@invoiceCode", SqlDbType.NVarChar, 100).Value = invoiceCode;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;
        var matches = new List<InvoiceIdentity>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            matches.Add(new InvoiceIdentity(
                ReadInt(reader, "InvoiceId"),
                ReadInt(reader, "SubscriptionId"),
                ReadText(reader, "InvoiceNumber"),
                ReadText(reader, "GeneratedInvoiceCode"),
                ReadInt(reader, "TenantId"),
                ReadInt(reader, "DeviceId")));
        }

        if (matches.Count == 0)
        {
            throw PdfError("invoice_not_found", "Invoice không tồn tại.", "Invoice was not found.", StatusCodes.Status404NotFound);
        }

        if (matches.Count > 1)
        {
            throw PdfError("invoice_not_unique", "Mã invoice không duy nhất.", "Invoice code is not unique.", StatusCodes.Status409Conflict);
        }

        return matches[0];
    }

    private static async Task<InvoicePdfRecord?> GetCurrentRecordByInvoiceIdAsync(SqlConnection connection, SqlTransaction transaction, int invoiceId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT TOP 1 p.[ID], p.[InvoiceId], p.[InvoiceCode], p.[FileName], p.[OriginalFileName], p.[StorageKey],
                   p.[ContentType], p.[FileSize], p.[Sha256], p.[Version], p.[SourceSystem], p.[ExternalReference],
                   p.[UploadedByUserId], p.[UploadedBy], p.[UploadedAtUtc], p.[UpdatedAtUtc],
                   i.[SubscriptionId], s.[TenantId], s.[DeviceId]
            FROM [dbo].[TblInvoicePdf] p
            INNER JOIN [dbo].[TblSubscriptionInvoice] i ON i.[ID] = p.[InvoiceId]
            INNER JOIN [dbo].[TblMonthlySubscription] s ON s.[ID] = i.[SubscriptionId]
            WHERE p.[InvoiceId] = @invoiceId
              AND p.[IsCurrent] = 1
              AND p.[IsDeleted] = 0
            ORDER BY p.[Version] DESC, p.[ID] DESC;
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@invoiceId", SqlDbType.Int).Value = invoiceId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapRecord(reader) : null;
    }

    private static async Task<int> GetNextVersionAsync(SqlConnection connection, SqlTransaction transaction, int invoiceId, CancellationToken cancellationToken)
    {
        const string query = "SELECT ISNULL(MAX([Version]), 0) + 1 FROM [dbo].[TblInvoicePdf] WITH (UPDLOCK, HOLDLOCK) WHERE [InvoiceId] = @invoiceId;";
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@invoiceId", SqlDbType.Int).Value = invoiceId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task MarkOldVersionsNotCurrentAsync(SqlConnection connection, SqlTransaction transaction, int invoiceId, CancellationToken cancellationToken)
    {
        const string query = "UPDATE [dbo].[TblInvoicePdf] SET [IsCurrent] = 0, [UpdatedAtUtc] = SYSUTCDATETIME() WHERE [InvoiceId] = @invoiceId AND [IsCurrent] = 1;";
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@invoiceId", SqlDbType.Int).Value = invoiceId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<InvoicePdfRecord> InsertRecordAsync(SqlConnection connection, SqlTransaction transaction, InvoiceIdentity invoice, InvoicePdfUploadRequest request, string invoiceCode, string fileName, string storageKey, string sha256, int version, CancellationToken cancellationToken)
    {
        const string query = """
            INSERT INTO [dbo].[TblInvoicePdf]
                ([InvoiceId], [InvoiceCode], [FileName], [OriginalFileName], [StorageKey], [ContentType], [FileSize], [Sha256], [Version], [IsCurrent],
                 [SourceSystem], [ExternalReference], [UploadedByUserId], [UploadedBy], [UploadedAtUtc], [UpdatedAtUtc], [IsDeleted])
            OUTPUT INSERTED.[ID], INSERTED.[UploadedAtUtc], INSERTED.[UpdatedAtUtc]
            VALUES
                (@invoiceId, @invoiceCode, @fileName, @originalFileName, @storageKey, N'application/pdf', @fileSize, @sha256, @version, 1,
                 @sourceSystem, @externalReference, @uploadedByUserId, @uploadedBy, SYSUTCDATETIME(), SYSUTCDATETIME(), 0);
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@invoiceId", SqlDbType.Int).Value = invoice.InvoiceId;
        command.Parameters.Add("@invoiceCode", SqlDbType.NVarChar, 100).Value = invoiceCode;
        command.Parameters.Add("@fileName", SqlDbType.NVarChar, 255).Value = fileName;
        command.Parameters.Add("@originalFileName", SqlDbType.NVarChar, 255).Value = Path.GetFileName(request.File?.FileName ?? fileName);
        command.Parameters.Add("@storageKey", SqlDbType.NVarChar, 500).Value = storageKey;
        command.Parameters.Add("@fileSize", SqlDbType.BigInt).Value = request.File?.Length ?? 0;
        command.Parameters.Add("@sha256", SqlDbType.Char, 64).Value = sha256;
        command.Parameters.Add("@version", SqlDbType.Int).Value = version;
        command.Parameters.Add("@sourceSystem", SqlDbType.NVarChar, 100).Value = string.IsNullOrWhiteSpace(request.SourceSystem) ? "ShipNet" : request.SourceSystem.Trim();
        command.Parameters.Add("@externalReference", SqlDbType.NVarChar, 200).Value = string.IsNullOrWhiteSpace(request.ExternalReference) ? DBNull.Value : request.ExternalReference.Trim();
        command.Parameters.Add("@uploadedByUserId", SqlDbType.Int).Value = (object?)request.UploadedByUserId ?? DBNull.Value;
        command.Parameters.Add("@uploadedBy", SqlDbType.NVarChar, 100).Value = string.IsNullOrWhiteSpace(request.UploadedBy) ? "InvoiceGenerator" : request.UploadedBy.Trim();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new InvoicePdfRecord
        {
            Id = Convert.ToInt64(reader["ID"], CultureInfo.InvariantCulture),
            InvoiceId = invoice.InvoiceId,
            SubscriptionId = invoice.SubscriptionId,
            TenantId = invoice.TenantId,
            DeviceId = invoice.DeviceId,
            InvoiceCode = invoiceCode,
            FileName = fileName,
            OriginalFileName = Path.GetFileName(request.File?.FileName ?? fileName),
            StorageKey = storageKey,
            ContentType = "application/pdf",
            FileSize = request.File?.Length ?? 0,
            Sha256 = sha256,
            Version = version,
            SourceSystem = string.IsNullOrWhiteSpace(request.SourceSystem) ? "ShipNet" : request.SourceSystem.Trim(),
            ExternalReference = request.ExternalReference,
            UploadedByUserId = request.UploadedByUserId,
            UploadedBy = string.IsNullOrWhiteSpace(request.UploadedBy) ? "InvoiceGenerator" : request.UploadedBy.Trim(),
            UploadedAtUtc = ReadDate(reader, "UploadedAtUtc") ?? DateTime.UtcNow,
            UpdatedAtUtc = ReadDate(reader, "UpdatedAtUtc") ?? DateTime.UtcNow
        };
    }

    private static async Task InsertAuditAsync(SqlConnection connection, SqlTransaction transaction, int? userId, int subscriptionId, string action, string invoiceCode, string username, CancellationToken cancellationToken)
    {
        const string query = """
            IF OBJECT_ID(N'[dbo].[TblAuditLog]', N'U') IS NOT NULL
            BEGIN
                INSERT INTO [dbo].[TblAuditLog] ([UserId], [DeviceId], [LogAction], [LogDetail], [Created_Date])
                VALUES (@userId, @subscriptionId, N'monthly_subscription', @action, GETDATE());
            END
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@subscriptionId", SqlDbType.Int).Value = subscriptionId;
        command.Parameters.Add("@userId", SqlDbType.Int).Value = (object?)userId ?? DBNull.Value;
        command.Parameters.Add("@action", SqlDbType.NVarChar, 1000).Value = $"{action}: {invoiceCode} by {username}";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static InvoicePdfRecord MapRecord(SqlDataReader reader)
    {
        return new InvoicePdfRecord
        {
            Id = ReadLong(reader, "ID"),
            InvoiceId = ReadInt(reader, "InvoiceId"),
            SubscriptionId = ReadInt(reader, "SubscriptionId"),
            TenantId = ReadInt(reader, "TenantId"),
            DeviceId = ReadInt(reader, "DeviceId"),
            InvoiceCode = ReadText(reader, "InvoiceCode"),
            FileName = ReadText(reader, "FileName"),
            OriginalFileName = ReadText(reader, "OriginalFileName"),
            StorageKey = ReadText(reader, "StorageKey"),
            ContentType = ReadText(reader, "ContentType"),
            FileSize = ReadLong(reader, "FileSize"),
            Sha256 = ReadText(reader, "Sha256"),
            Version = ReadInt(reader, "Version"),
            SourceSystem = ReadText(reader, "SourceSystem"),
            ExternalReference = ReadText(reader, "ExternalReference"),
            UploadedByUserId = reader["UploadedByUserId"] == DBNull.Value ? null : ReadInt(reader, "UploadedByUserId"),
            UploadedBy = ReadText(reader, "UploadedBy"),
            UploadedAtUtc = ReadDate(reader, "UploadedAtUtc") ?? DateTime.MinValue,
            UpdatedAtUtc = ReadDate(reader, "UpdatedAtUtc") ?? DateTime.MinValue
        };
    }

    private static async Task EnsureSchemaAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        const string query = """
            IF OBJECT_ID(N'[dbo].[TblInvoicePdf]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[TblInvoicePdf](
                    [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblInvoicePdf] PRIMARY KEY,
                    [InvoiceId] int NOT NULL,
                    [InvoiceCode] nvarchar(100) NOT NULL,
                    [FileName] nvarchar(255) NOT NULL,
                    [OriginalFileName] nvarchar(255) NULL,
                    [StorageKey] nvarchar(500) NOT NULL,
                    [ContentType] nvarchar(100) NOT NULL,
                    [FileSize] bigint NOT NULL,
                    [Sha256] char(64) NOT NULL,
                    [Version] int NOT NULL,
                    [IsCurrent] bit NOT NULL CONSTRAINT [DF_TblInvoicePdf_IsCurrent] DEFAULT(1),
                    [SourceSystem] nvarchar(100) NULL,
                    [ExternalReference] nvarchar(200) NULL,
                    [UploadedByUserId] int NULL,
                    [UploadedBy] nvarchar(100) NULL,
                    [UploadedAtUtc] datetime2(0) NOT NULL CONSTRAINT [DF_TblInvoicePdf_UploadedAtUtc] DEFAULT(SYSUTCDATETIME()),
                    [UpdatedAtUtc] datetime2(0) NOT NULL CONSTRAINT [DF_TblInvoicePdf_UpdatedAtUtc] DEFAULT(SYSUTCDATETIME()),
                    [DeletedAtUtc] datetime2(0) NULL,
                    [IsDeleted] bit NOT NULL CONSTRAINT [DF_TblInvoicePdf_IsDeleted] DEFAULT(0)
                );
            END;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblInvoicePdf_InvoiceId' AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoicePdf]'))
                CREATE INDEX [IX_TblInvoicePdf_InvoiceId] ON [dbo].[TblInvoicePdf]([InvoiceId]);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblInvoicePdf_InvoiceCode' AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoicePdf]'))
                CREATE INDEX [IX_TblInvoicePdf_InvoiceCode] ON [dbo].[TblInvoicePdf]([InvoiceCode]);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblInvoicePdf_Sha256' AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoicePdf]'))
                CREATE INDEX [IX_TblInvoicePdf_Sha256] ON [dbo].[TblInvoicePdf]([Sha256]);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TblInvoicePdf_Current' AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoicePdf]'))
                CREATE UNIQUE INDEX [UX_TblInvoicePdf_Current] ON [dbo].[TblInvoicePdf]([InvoiceId]) WHERE [IsCurrent] = 1 AND [IsDeleted] = 0;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TblInvoicePdf_Version' AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoicePdf]'))
                CREATE UNIQUE INDEX [UX_TblInvoicePdf_Version] ON [dbo].[TblInvoicePdf]([InvoiceId], [Version]);
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static InvoicePdfError PdfError(string errorCode, string message, string messageEn, int statusCode)
    {
        return new InvoicePdfError(errorCode, message, messageEn, statusCode);
    }

    private static int ReadInt(SqlDataReader reader, string name) => reader[name] == DBNull.Value ? 0 : Convert.ToInt32(reader[name], CultureInfo.InvariantCulture);
    private static long ReadLong(SqlDataReader reader, string name) => reader[name] == DBNull.Value ? 0 : Convert.ToInt64(reader[name], CultureInfo.InvariantCulture);
    private static string ReadText(SqlDataReader reader, string name) => reader[name]?.ToString() ?? string.Empty;
    private static DateTime? ReadDate(SqlDataReader reader, string name) => reader[name] == DBNull.Value ? null : Convert.ToDateTime(reader[name], CultureInfo.InvariantCulture);

    private sealed record InvoiceIdentity(int InvoiceId, int SubscriptionId, string InvoiceNumber, string GeneratedInvoiceCode, int TenantId, int DeviceId);
}
