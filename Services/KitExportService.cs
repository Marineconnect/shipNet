using System.Globalization;
using Microsoft.AspNetCore.Http;
using NPOI.SS.UserModel;

namespace StarlinkDeviceManager.Services;

public class KitExportService(IDeviceService deviceService, ILogger<KitExportService> logger) : IKitExportService
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RateLimitDelay = TimeSpan.FromSeconds(10);

    public async Task<byte[]> ProcessSlkTemplateAsync(IFormFile importFile, CancellationToken cancellationToken = default)
    {
        if (importFile is null || importFile.Length == 0)
        {
            throw new InvalidOperationException("Vui lòng chọn file SLK_Template.xls.");
        }

        await using var input = importFile.OpenReadStream();
        var workbook = WorkbookFactory.Create(input);
        var sheet = workbook.GetSheetAt(0) ?? throw new InvalidOperationException("File không có sheet dữ liệu.");
        var headerRowIndex = FindHeaderRowIndex(sheet);
        if (headerRowIndex < 0)
        {
            throw new InvalidOperationException("Không tìm thấy cột Terminal ID trong file.");
        }

        var headerRow = sheet.GetRow(headerRowIndex) ?? throw new InvalidOperationException("Header row không hợp lệ.");
        var terminalColumn = FindColumn(headerRow, "Terminal ID", "TerminalID", "Terminal Id", "Terminal");
        if (terminalColumn < 0)
        {
            throw new InvalidOperationException("Không tìm thấy cột Terminal ID trong file.");
        }

        var kitColumn = EnsureColumn(headerRow, "KIT", "KIT Number", "Kit Number");
        var serviceColumn = EnsureColumn(headerRow, "Service", "Service Line", "ServiceLine");
        var statusColumn = EnsureColumn(headerRow, "Status");

        var rows = GetDataRows(sheet, headerRowIndex, terminalColumn).ToList();
        logger.LogInformation("Start SLK KIT export import. Rows={RowCount}.", rows.Count);
        if (rows.Count == 0)
        {
            throw new InvalidOperationException("File import không có dữ liệu Terminal ID để xử lý. Vui lòng nhập Terminal ID vào cột Terminal ID rồi import lại.");
        }

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var terminalId = GetCellText(row.GetCell(terminalColumn));
            await ProcessRowAsync(row, terminalId, kitColumn, serviceColumn, statusColumn, cancellationToken);
        }

        using var output = new MemoryStream();
        workbook.Write(output, leaveOpen: true);
        logger.LogInformation("Completed SLK KIT export import. Rows={RowCount}.", rows.Count);
        return output.ToArray();
    }

    private async Task ProcessRowAsync(IRow row, string terminalId, int kitColumn, int serviceColumn, int statusColumn, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(terminalId))
        {
            SetCell(row, statusColumn, "Error: Terminal ID is empty.");
            return;
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                SetCell(row, statusColumn, attempt == 1 ? "Processing" : $"Retry {attempt}/{MaxAttempts}");
                var lookup = await deviceService.LookupKitTerminalInfoAsync(terminalId, cancellationToken);
                if (lookup.Success)
                {
                    SetCell(row, kitColumn, lookup.KitNumber);
                    SetCell(row, serviceColumn, lookup.ServiceLine);
                    SetCell(row, statusColumn, BuildSuccessStatus(lookup));
                    return;
                }

                if (lookup.IsRateLimited && attempt < MaxAttempts)
                {
                    SetCell(row, statusColumn, $"Pending: rate limit, retry {attempt + 1}/{MaxAttempts}.");
                    await Task.Delay(RateLimitDelay, cancellationToken);
                    continue;
                }

                if (attempt < MaxAttempts && IsTransientError(lookup.ErrorCode, lookup.MessageEn))
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
                    continue;
                }

                SetCell(row, statusColumn, $"Error: {FirstNotEmpty(lookup.MessageEn, lookup.Message, lookup.ErrorCode, "Unknown error")}");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed to process Terminal ID {TerminalId} on attempt {Attempt}/{MaxAttempts}.", terminalId, attempt, MaxAttempts);
                if (attempt >= MaxAttempts)
                {
                    SetCell(row, statusColumn, $"Error: {exception.GetBaseException().Message}");
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
            }
        }
    }

    private static string BuildSuccessStatus(Models.KitTerminalLookupResult lookup)
    {
        if (string.IsNullOrWhiteSpace(lookup.KitNumber) || string.IsNullOrWhiteSpace(lookup.ServiceLine))
        {
            return "Warning: API succeeded but KIT or Service is empty.";
        }

        return "Success";
    }

    private static bool IsTransientError(string errorCode, string message)
    {
        var text = $"{errorCode} {message}";
        return text.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || text.Contains("tempor", StringComparison.OrdinalIgnoreCase)
            || text.Contains("503", StringComparison.OrdinalIgnoreCase)
            || text.Contains("502", StringComparison.OrdinalIgnoreCase)
            || text.Contains("500", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<IRow> GetDataRows(ISheet sheet, int headerRowIndex, int terminalColumn)
    {
        for (var rowIndex = headerRowIndex + 1; rowIndex <= sheet.LastRowNum; rowIndex++)
        {
            var row = sheet.GetRow(rowIndex);
            if (row is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(GetCellText(row.GetCell(terminalColumn))))
            {
                yield return row;
            }
        }
    }

    private static int FindHeaderRowIndex(ISheet sheet)
    {
        var max = Math.Min(sheet.LastRowNum, 30);
        for (var i = sheet.FirstRowNum; i <= max; i++)
        {
            var row = sheet.GetRow(i);
            if (row is not null && FindColumn(row, "Terminal ID", "TerminalID", "Terminal Id", "Terminal") >= 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindColumn(IRow row, params string[] names)
    {
        var normalizedNames = names.Select(NormalizeHeader).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = row.FirstCellNum; i < row.LastCellNum; i++)
        {
            if (normalizedNames.Contains(NormalizeHeader(GetCellText(row.GetCell(i)))))
            {
                return i;
            }
        }

        return -1;
    }

    private static int EnsureColumn(IRow headerRow, string preferredName, params string[] alternateNames)
    {
        var names = new[] { preferredName }.Concat(alternateNames).ToArray();
        var existing = FindColumn(headerRow, names);
        if (existing >= 0)
        {
            return existing;
        }

        var column = Math.Max(headerRow.LastCellNum, (short)0);
        SetCell(headerRow, column, preferredName);
        return column;
    }

    private static string NormalizeHeader(string value)
    {
        return new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    private static string GetCellText(ICell? cell)
    {
        if (cell is null)
        {
            return string.Empty;
        }

        return cell.CellType switch
        {
            CellType.String => cell.StringCellValue?.Trim() ?? string.Empty,
            CellType.Numeric => DateUtil.IsCellDateFormatted(cell)
                ? (cell.DateCellValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty)
                : cell.NumericCellValue.ToString("0.############", CultureInfo.InvariantCulture),
            CellType.Boolean => cell.BooleanCellValue ? "TRUE" : "FALSE",
            CellType.Formula => cell.ToString()?.Trim() ?? string.Empty,
            _ => cell.ToString()?.Trim() ?? string.Empty
        };
    }

    private static void SetCell(IRow row, int column, string value)
    {
        var cell = row.GetCell(column) ?? row.CreateCell(column);
        cell.SetCellValue(value ?? string.Empty);
    }

    private static string FirstNotEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}
