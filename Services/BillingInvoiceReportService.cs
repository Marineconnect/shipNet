using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public sealed class BillingInvoiceReportService(IConfiguration configuration) : IBillingInvoiceReportService
{
    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing connection string: DefaultConnection");

    private static readonly HashSet<int> PageSizes = [10, 20, 50, 100];
    private static readonly IReadOnlyDictionary<string, string> SortColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["createdAt"] = "i.[CreatedAt]",
        ["invoiceNumber"] = "i.[InvoiceNumber]",
        ["billingCycle"] = "s.[UsageMonth]",
        ["tenant"] = "s.[TenantName]",
        ["vessel"] = "s.[VesselName]",
        ["invoiceAmount"] = "i.[Amount]",
        ["paidAmount"] = "i.[PaidAmount]",
        ["margin"] = "COALESCE(NULLIF(i.[MarginAmount], 0), i.[SalePrice] - i.[BuyPrice])",
        ["status"] = "i.[Status]"
    };

    public async Task<BillingInvoicePageResult> GetInvoicesAsync(BillingInvoiceFilterViewModel filter, int page, int pageSize, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default)
    {
        NormalizeFilter(filter, allowedTenantId, allowedDeviceId);
        pageSize = PageSizes.Contains(pageSize) ? pageSize : 20;
        page = Math.Max(1, page);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var where = BuildWhere(filter);
        var orderBy = $"{SortColumns.GetValueOrDefault(filter.SortBy, SortColumns["createdAt"])} {(filter.SortDirection == "asc" ? "ASC" : "DESC")}, i.[ID] DESC";
        var totalItems = await ScalarIntAsync(connection, $"SELECT COUNT(1) {BaseFromSql()} WHERE {where};", filter, cancellationToken);
        var summary = await QuerySummaryAsync(connection, SummarySql(where), filter, cancellationToken);
        var items = await QueryItemsAsync(connection, ListSql(where, orderBy), filter, page, pageSize, cancellationToken);

        return new BillingInvoicePageResult { Items = items, Summary = summary, CurrentPage = page, PageSize = pageSize, TotalItems = totalItems };
    }

    public async Task<BillingInvoiceIndexViewModel> GetIndexOptionsAsync(BillingInvoiceFilterViewModel filter, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default)
    {
        NormalizeFilter(filter, allowedTenantId, allowedDeviceId);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        return new BillingInvoiceIndexViewModel
        {
            Filter = filter,
            Tenants = await GetTenantOptionsAsync(connection, allowedTenantId, allowedDeviceId, cancellationToken),
            Devices = await GetDeviceOptionsAsync(connection, allowedTenantId, allowedDeviceId, cancellationToken),
            Plans = await GetPlanOptionsAsync(connection, allowedTenantId, allowedDeviceId, cancellationToken),
            InvoiceTypes = await GetDistinctTextOptionsAsync(connection, "i.[InvoiceType]", allowedTenantId, allowedDeviceId, cancellationToken),
            InvoiceStatuses = await GetDistinctTextOptionsAsync(connection, "i.[Status]", allowedTenantId, allowedDeviceId, cancellationToken),
            IsTenantScoped = allowedTenantId.HasValue,
            IsDeviceScoped = allowedDeviceId.HasValue
        };
    }

    public async Task<byte[]> ExportCsvAsync(BillingInvoiceFilterViewModel filter, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default)
    {
        NormalizeFilter(filter, allowedTenantId, allowedDeviceId);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var where = BuildWhere(filter);
        var orderBy = $"{SortColumns.GetValueOrDefault(filter.SortBy, SortColumns["createdAt"])} {(filter.SortDirection == "asc" ? "ASC" : "DESC")}, i.[ID] DESC";
        await using var command = new SqlCommand(ExportSql(where, orderBy), connection);
        AddFilterParameters(command, filter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var csv = new StringBuilder();
        csv.AppendLine("Invoice Number,Invoice Type,Tenant,Vessel,Device,KIT,Plan,Billing Cycle,Period Start,Period End,Buy Price,Sale Price,Margin,Margin %,Invoice Amount,Paid Amount,Outstanding Amount,Status,Payment Method,Payment Time");
        while (await reader.ReadAsync(cancellationToken))
        {
            csv.AppendLine(string.Join(",", new[]
            {
                Csv(ReadText(reader, "InvoiceNumber")),
                Csv(ReadText(reader, "InvoiceType")),
                Csv(ReadText(reader, "TenantName")),
                Csv(ReadText(reader, "VesselName")),
                Csv(ReadText(reader, "DeviceName")),
                Csv(ReadText(reader, "KitId")),
                Csv(ReadText(reader, "PlanName")),
                Csv(ReadDate(reader, "UsageMonth")?.ToString("MM/yyyy") ?? string.Empty),
                Csv(ReadDate(reader, "StartDate")?.ToString("yyyy-MM-dd") ?? string.Empty),
                Csv(ReadDate(reader, "EndDate")?.ToString("yyyy-MM-dd") ?? string.Empty),
                Csv(ReadDecimal(reader, "BuyPrice").ToString("0.##", CultureInfo.InvariantCulture)),
                Csv(ReadDecimal(reader, "SalePrice").ToString("0.##", CultureInfo.InvariantCulture)),
                Csv(ReadDecimal(reader, "MarginAmount").ToString("0.##", CultureInfo.InvariantCulture)),
                Csv(ReadDecimal(reader, "MarginPercent").ToString("0.##", CultureInfo.InvariantCulture)),
                Csv(ReadDecimal(reader, "InvoiceAmount").ToString("0.##", CultureInfo.InvariantCulture)),
                Csv(ReadDecimal(reader, "PaidAmount").ToString("0.##", CultureInfo.InvariantCulture)),
                Csv(ReadDecimal(reader, "OutstandingAmount").ToString("0.##", CultureInfo.InvariantCulture)),
                Csv(ReadText(reader, "Status")),
                Csv(ReadText(reader, "PaymentMethod")),
                Csv(ReadDate(reader, "PaymentTime")?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty)
            }));
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv.ToString());
    }

    private static string BaseFromSql() => """
        FROM [dbo].[TblSubscriptionInvoice] i
        INNER JOIN [dbo].[TblMonthlySubscription] s ON s.[ID] = i.[SubscriptionId]
        LEFT JOIN [dbo].[TblDevices] d ON d.[ID] = s.[DeviceId]
        OUTER APPLY (
            SELECT TOP 1 *
            FROM [dbo].[TblPaymentTransaction] pt
            WHERE pt.[InvoiceId] = i.[ID] OR pt.[InvoiceNumber] = i.[InvoiceNumber] OR pt.[ProviderPaymentNo] = i.[ReceiptNumber]
            ORDER BY pt.[Updated_Date] DESC, pt.[ID] DESC
        ) tx
        OUTER APPLY (
            SELECT TOP 1 *
            FROM [dbo].[TblNinePayQrSession] qs
            WHERE qs.[InvoiceId] = i.[ID]
               OR EXISTS (SELECT 1 FROM [dbo].[TblNinePayQrSessionInvoice] qi WHERE qi.[QrSessionId] = qs.[ID] AND qi.[InvoiceId] = i.[ID])
            ORDER BY qs.[Created_Date] DESC, qs.[ID] DESC
        ) qr
        """;

    private static string CommonSelectSql() => """
        i.[ID] AS [InvoiceId], i.[InvoiceNumber], i.[ReceiptNumber], i.[PoNumber], i.[InvoiceType],
        i.[SubscriptionId], s.[TenantId], s.[TenantName], s.[DeviceId],
        COALESCE(NULLIF(d.[DeviceName], N''), d.[DeviceCode], N'') AS [DeviceName],
        d.[DeviceCode],
        COALESCE(NULLIF(s.[VesselName], N''), NULLIF(d.[VesselName], N''), N'') AS [VesselName],
        COALESCE(NULLIF(d.[KITNumber], N''), NULLIF(s.[KitId], N''), NULLIF(d.[KITID], N''), N'') AS [KitId],
        s.[PricingPlanId], s.[PlanName], s.[UsageMonth], s.[StartDate], s.[EndDate],
        i.[DataGb], i.[BuyPrice], i.[SalePrice],
        COALESCE(NULLIF(i.[MarginAmount], 0), i.[SalePrice] - i.[BuyPrice]) AS [MarginAmount],
        CASE WHEN i.[SalePrice] = 0 THEN 0 ELSE COALESCE(NULLIF(i.[MarginAmount], 0), i.[SalePrice] - i.[BuyPrice]) / i.[SalePrice] * 100 END AS [MarginPercent],
        i.[Amount] AS [InvoiceAmount], i.[PaidAmount], i.[RefundAmount],
        CASE WHEN i.[Amount] - i.[PaidAmount] - i.[RefundAmount] > 0 THEN i.[Amount] - i.[PaidAmount] - i.[RefundAmount] ELSE 0 END AS [OutstandingAmount],
        COALESCE(NULLIF(tx.[Method], N''), NULLIF(qr.[Method], N''), N'') AS [PaymentMethod],
        COALESCE(tx.[Updated_Date], qr.[PaidAt], i.[CompletedAt]) AS [PaymentTime],
        COALESCE(NULLIF(tx.[ProviderPaymentNo], N''), NULLIF(qr.[IpnPaymentNo], N''), NULLIF(qr.[ProviderPaymentNo], N''), NULLIF(i.[ReceiptNumber], N''), N'') AS [TransactionCode],
        COALESCE(NULLIF(qr.[TransferContent], N''), NULLIF(tx.[ProviderStatus], N''), NULLIF(qr.[ProviderStatus], N''), N'') AS [PaymentDescription],
        i.[Status], i.[CreatedAt], i.[CompletedAt]
        """;

    private static string SummarySql(string where) => $"""
        SELECT
            COALESCE(SUM(i.[Amount]), 0) AS [TotalInvoiceAmount],
            COALESCE(SUM(i.[PaidAmount]), 0) AS [PaidAmount],
            COALESCE(SUM(CASE WHEN i.[Amount] - i.[PaidAmount] - i.[RefundAmount] > 0 THEN i.[Amount] - i.[PaidAmount] - i.[RefundAmount] ELSE 0 END), 0) AS [PendingAmount],
            COALESCE(SUM(COALESCE(NULLIF(i.[MarginAmount], 0), i.[SalePrice] - i.[BuyPrice])), 0) AS [TotalMargin],
            SUM(CASE WHEN LOWER(i.[Status]) = N'paid' OR (i.[PaidAmount] >= i.[Amount] AND i.[Amount] > 0) THEN 1 ELSE 0 END) AS [PaidInvoiceCount],
            SUM(CASE WHEN LOWER(i.[Status]) NOT IN (N'paid', N'void', N'cancelled', N'canceled', N'refunded')
                      AND (i.[Amount] - i.[PaidAmount] - i.[RefundAmount]) > 0 THEN 1 ELSE 0 END) AS [PendingInvoiceCount]
        {BaseFromSql()}
        WHERE {where};
        """;

    private static string ListSql(string where, string orderBy) => $"""
        SELECT {CommonSelectSql()}
        {BaseFromSql()}
        WHERE {where}
        ORDER BY {orderBy}
        OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;
        """;

    private static string ExportSql(string where, string orderBy) => $"""
        SELECT {CommonSelectSql()}
        {BaseFromSql()}
        WHERE {where}
        ORDER BY {orderBy};
        """;

    private static string BuildWhere(BillingInvoiceFilterViewModel filter)
    {
        var clauses = new List<string>
        {
            "(@allowedTenantId IS NULL OR s.[TenantId] = @allowedTenantId)",
            "(@allowedDeviceId IS NULL OR s.[DeviceId] = @allowedDeviceId)",
            "(@tenantId IS NULL OR s.[TenantId] = @tenantId)",
            "(@deviceId IS NULL OR s.[DeviceId] = @deviceId)",
            "(@pricingPlanId IS NULL OR s.[PricingPlanId] = @pricingPlanId)",
            "(@invoiceType IS NULL OR i.[InvoiceType] = @invoiceType)",
            "(@invoiceStatus IS NULL OR i.[Status] = @invoiceStatus)",
            "(@invoiceNumber IS NULL OR i.[InvoiceNumber] LIKE @invoiceNumber)",
            "(@dateFrom IS NULL OR i.[CreatedAt] >= @dateFrom)",
            "(@dateTo IS NULL OR i.[CreatedAt] < DATEADD(day, 1, @dateTo))",
            "(@billingCycle IS NULL OR s.[UsageMonth] = @billingCycle)",
            "(@billingYearStart IS NULL OR @billingCycle IS NOT NULL OR (s.[UsageMonth] >= @billingYearStart AND s.[UsageMonth] < @billingYearEnd))",
            "(@kitId IS NULL OR COALESCE(NULLIF(d.[KITNumber], N''), NULLIF(s.[KitId], N''), NULLIF(d.[KITID], N''), N'') LIKE @kitId)",
            "(@vessel IS NULL OR COALESCE(NULLIF(s.[VesselName], N''), NULLIF(d.[VesselName], N''), N'') LIKE @vessel)"
        };

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            clauses.Add("(i.[InvoiceNumber] LIKE @search OR COALESCE(NULLIF(d.[KITNumber], N''), NULLIF(s.[KitId], N''), NULLIF(d.[KITID], N''), N'') LIKE @search OR s.[TenantName] LIKE @search OR s.[VesselName] LIKE @search OR d.[DeviceName] LIKE @search OR d.[DeviceCode] LIKE @search OR s.[PlanName] LIKE @search)");
        }

        if (string.Equals(filter.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase))
        {
            clauses.Add("(LOWER(i.[Status]) = N'paid' OR (i.[PaidAmount] >= i.[Amount] AND i.[Amount] > 0))");
        }
        else if (string.Equals(filter.PaymentStatus, "partial", StringComparison.OrdinalIgnoreCase))
        {
            clauses.Add("i.[PaidAmount] > 0 AND i.[PaidAmount] < i.[Amount] AND LOWER(i.[Status]) NOT IN (N'paid', N'void', N'cancelled', N'canceled')");
        }
        else if (string.Equals(filter.PaymentStatus, "pending", StringComparison.OrdinalIgnoreCase))
        {
            clauses.Add("i.[PaidAmount] <= 0 AND LOWER(i.[Status]) NOT IN (N'paid', N'void', N'cancelled', N'canceled', N'refunded')");
        }
        else if (string.Equals(filter.PaymentStatus, "refunded", StringComparison.OrdinalIgnoreCase))
        {
            clauses.Add("LOWER(i.[Status]) = N'refunded' OR i.[RefundAmount] >= i.[Amount]");
        }
        else if (string.Equals(filter.PaymentStatus, "void", StringComparison.OrdinalIgnoreCase))
        {
            clauses.Add("LOWER(i.[Status]) IN (N'void', N'cancelled', N'canceled')");
        }

        if (string.Equals(filter.MetricFilter, "margin", StringComparison.OrdinalIgnoreCase))
        {
            clauses.Add("COALESCE(NULLIF(i.[MarginAmount], 0), i.[SalePrice] - i.[BuyPrice]) <> 0");
        }

        if (IsDashboardDrillDown(filter.Source))
        {
            clauses.Add("LOWER(COALESCE(i.[Status], N'')) NOT IN (N'void', N'cancelled', N'canceled')");
        }

        return string.Join(" AND ", clauses);
    }

    private static void AddFilterParameters(SqlCommand command, BillingInvoiceFilterViewModel filter)
    {
        command.Parameters.Add("@allowedTenantId", SqlDbType.Int).Value = (object?)filter.TenantIdScope ?? DBNull.Value;
        command.Parameters.Add("@allowedDeviceId", SqlDbType.Int).Value = (object?)filter.DeviceIdScope ?? DBNull.Value;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)filter.TenantId ?? DBNull.Value;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)filter.DeviceId ?? DBNull.Value;
        command.Parameters.Add("@pricingPlanId", SqlDbType.Int).Value = (object?)filter.PricingPlanId ?? DBNull.Value;
        command.Parameters.Add("@invoiceType", SqlDbType.NVarChar, 50).Value = (object?)filter.InvoiceType ?? DBNull.Value;
        command.Parameters.Add("@invoiceStatus", SqlDbType.NVarChar, 50).Value = (object?)filter.InvoiceStatus ?? DBNull.Value;
        command.Parameters.Add("@invoiceNumber", SqlDbType.NVarChar, 120).Value = string.IsNullOrWhiteSpace(filter.InvoiceNumber) ? DBNull.Value : $"%{filter.InvoiceNumber}%";
        command.Parameters.Add("@dateFrom", SqlDbType.DateTime).Value = (object?)filter.DateFrom ?? DBNull.Value;
        command.Parameters.Add("@dateTo", SqlDbType.DateTime).Value = (object?)filter.DateTo ?? DBNull.Value;
        command.Parameters.Add("@billingCycle", SqlDbType.Date).Value = (object?)ParseBillingCycle(filter.BillingCycle) ?? DBNull.Value;
        var billingYearStart = filter.BillingYear is >= 2000 and <= 2100 ? new DateTime(filter.BillingYear.Value, 1, 1) : (DateTime?)null;
        command.Parameters.Add("@billingYearStart", SqlDbType.Date).Value = (object?)billingYearStart ?? DBNull.Value;
        command.Parameters.Add("@billingYearEnd", SqlDbType.Date).Value = (object?)billingYearStart?.AddYears(1) ?? DBNull.Value;
        command.Parameters.Add("@kitId", SqlDbType.NVarChar, 120).Value = string.IsNullOrWhiteSpace(filter.KitId) ? DBNull.Value : $"%{filter.KitId}%";
        command.Parameters.Add("@vessel", SqlDbType.NVarChar, 250).Value = string.IsNullOrWhiteSpace(filter.Vessel) ? DBNull.Value : $"%{filter.Vessel}%";
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            command.Parameters.Add("@search", SqlDbType.NVarChar, 260).Value = $"%{filter.Search}%";
        }
    }

    private static void NormalizeFilter(BillingInvoiceFilterViewModel filter, int? allowedTenantId, int? allowedDeviceId)
    {
        filter.DateFrom = filter.DateFrom?.Date;
        filter.DateTo = filter.DateTo?.Date;
        filter.BillingCycle = NormalizeNullable(filter.BillingCycle);
        filter.Vessel = NormalizeNullable(filter.Vessel);
        filter.KitId = NormalizeNullable(filter.KitId);
        filter.InvoiceType = NormalizeNullable(filter.InvoiceType);
        filter.InvoiceStatus = NormalizeNullable(filter.InvoiceStatus);
        filter.PaymentStatus = NormalizeNullable(filter.PaymentStatus);
        filter.MetricFilter = NormalizeMetricFilter(filter.MetricFilter);
        filter.Source = NormalizeSource(filter.Source);
        if (filter.MetricFilter == "paid")
        {
            filter.PaymentStatus = "paid";
        }
        else if (filter.MetricFilter == "pending")
        {
            filter.PaymentStatus = "pending";
        }
        else if (filter.MetricFilter == "total" || filter.MetricFilter == "margin")
        {
            filter.PaymentStatus = null;
        }
        filter.InvoiceNumber = NormalizeNullable(filter.InvoiceNumber);
        filter.Search = NormalizeNullable(filter.Search);
        filter.SortBy = SortColumns.ContainsKey(filter.SortBy) ? filter.SortBy : "createdAt";
        filter.SortDirection = string.Equals(filter.SortDirection, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
        filter.BillingYear = filter.BillingYear is >= 2000 and <= 2100 ? filter.BillingYear : null;
        if (!string.IsNullOrWhiteSpace(filter.BillingCycle))
        {
            filter.BillingYear = null;
        }
        filter.TenantIdScope = allowedTenantId;
        filter.DeviceIdScope = allowedDeviceId;
        if (allowedTenantId.HasValue) filter.TenantId = allowedTenantId.Value;
        if (allowedDeviceId.HasValue) filter.DeviceId = allowedDeviceId.Value;
    }

    private static string? NormalizeNullable(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string? NormalizeMetricFilter(string? value)
    {
        var normalized = NormalizeNullable(value)?.ToLowerInvariant();
        return normalized is "total" or "paid" or "pending" or "margin" ? normalized : null;
    }

    private static string? NormalizeSource(string? value)
    {
        var normalized = NormalizeNullable(value)?.ToLowerInvariant();
        return normalized is "dashboard-revenue" or "dashboard-commission" ? normalized : null;
    }

    private static bool IsDashboardDrillDown(string? source) =>
        string.Equals(source, "dashboard-revenue", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, "dashboard-commission", StringComparison.OrdinalIgnoreCase);

    private static DateTime? ParseBillingCycle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParseExact(value.Trim(), ["yyyy-MM", "yyyy-MM-dd", "MM/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? new DateTime(parsed.Year, parsed.Month, 1)
            : null;
    }

    private static async Task<int> ScalarIntAsync(SqlConnection connection, string query, BillingInvoiceFilterViewModel filter, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(query, connection);
        AddFilterParameters(command, filter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private static async Task<BillingInvoiceSummaryViewModel> QuerySummaryAsync(SqlConnection connection, string query, BillingInvoiceFilterViewModel filter, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(query, connection);
        AddFilterParameters(command, filter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new BillingInvoiceSummaryViewModel();
        return new BillingInvoiceSummaryViewModel
        {
            TotalInvoiceAmount = ReadDecimal(reader, "TotalInvoiceAmount"),
            PaidAmount = ReadDecimal(reader, "PaidAmount"),
            PendingAmount = ReadDecimal(reader, "PendingAmount"),
            TotalMargin = ReadDecimal(reader, "TotalMargin"),
            PaidInvoiceCount = ReadInt(reader, "PaidInvoiceCount"),
            PendingInvoiceCount = ReadInt(reader, "PendingInvoiceCount")
        };
    }

    private static async Task<List<BillingInvoiceListItemViewModel>> QueryItemsAsync(SqlConnection connection, string query, BillingInvoiceFilterViewModel filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(query, connection);
        AddFilterParameters(command, filter);
        command.Parameters.Add("@offset", SqlDbType.Int).Value = (page - 1) * pageSize;
        command.Parameters.Add("@pageSize", SqlDbType.Int).Value = pageSize;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<BillingInvoiceListItemViewModel>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new BillingInvoiceListItemViewModel
            {
                InvoiceId = ReadInt(reader, "InvoiceId"),
                InvoiceNumber = ReadText(reader, "InvoiceNumber"),
                ReceiptNumber = ReadText(reader, "ReceiptNumber"),
                PoNumber = ReadText(reader, "PoNumber"),
                InvoiceType = ReadText(reader, "InvoiceType"),
                SubscriptionId = ReadInt(reader, "SubscriptionId"),
                TenantId = ReadInt(reader, "TenantId"),
                TenantName = ReadText(reader, "TenantName"),
                DeviceId = ReadInt(reader, "DeviceId"),
                DeviceName = ReadText(reader, "DeviceName"),
                DeviceCode = ReadText(reader, "DeviceCode"),
                VesselName = ReadText(reader, "VesselName"),
                KitId = ReadText(reader, "KitId"),
                PricingPlanId = ReadInt(reader, "PricingPlanId"),
                PlanName = ReadText(reader, "PlanName"),
                UsageMonth = ReadDate(reader, "UsageMonth") ?? default,
                StartDate = ReadDate(reader, "StartDate") ?? default,
                EndDate = ReadDate(reader, "EndDate") ?? default,
                DataGb = ReadDecimal(reader, "DataGb"),
                BuyPrice = ReadDecimal(reader, "BuyPrice"),
                SalePrice = ReadDecimal(reader, "SalePrice"),
                MarginAmount = ReadDecimal(reader, "MarginAmount"),
                MarginPercent = ReadDecimal(reader, "MarginPercent"),
                InvoiceAmount = ReadDecimal(reader, "InvoiceAmount"),
                PaidAmount = ReadDecimal(reader, "PaidAmount"),
                RefundAmount = ReadDecimal(reader, "RefundAmount"),
                OutstandingAmount = ReadDecimal(reader, "OutstandingAmount"),
                PaymentMethod = ReadText(reader, "PaymentMethod"),
                PaymentTime = ReadDate(reader, "PaymentTime"),
                TransactionCode = ReadText(reader, "TransactionCode"),
                PaymentDescription = ReadText(reader, "PaymentDescription"),
                Status = ReadText(reader, "Status"),
                CreatedAt = ReadDate(reader, "CreatedAt") ?? default,
                CompletedAt = ReadDate(reader, "CompletedAt")
            });
        }
        return items;
    }

    private static async Task<List<DeviceTenantOptionViewModel>> GetTenantOptionsAsync(SqlConnection connection, int? allowedTenantId, int? allowedDeviceId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT DISTINCT s.[TenantId] AS [ID], s.[TenantName]
            FROM [dbo].[TblMonthlySubscription] s
            WHERE (@allowedTenantId IS NULL OR s.[TenantId] = @allowedTenantId)
              AND (@allowedDeviceId IS NULL OR s.[DeviceId] = @allowedDeviceId)
            ORDER BY s.[TenantName]
            """;
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@allowedTenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        command.Parameters.Add("@allowedDeviceId", SqlDbType.Int).Value = (object?)allowedDeviceId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tenants = new List<DeviceTenantOptionViewModel>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tenants.Add(new DeviceTenantOptionViewModel { Id = ReadInt(reader, "ID"), TenantName = ReadText(reader, "TenantName") });
        }
        return tenants;
    }

    private static async Task<List<BillingInvoiceDeviceOptionViewModel>> GetDeviceOptionsAsync(SqlConnection connection, int? allowedTenantId, int? allowedDeviceId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT DISTINCT s.[DeviceId] AS [ID], s.[TenantId],
                   COALESCE(NULLIF(d.[DeviceName], N''), d.[DeviceCode], N'') AS [DeviceName],
                   COALESCE(NULLIF(s.[VesselName], N''), NULLIF(d.[VesselName], N''), N'') AS [VesselName],
                   COALESCE(NULLIF(d.[KITNumber], N''), NULLIF(s.[KitId], N''), NULLIF(d.[KITID], N''), N'') AS [KitId]
            FROM [dbo].[TblMonthlySubscription] s
            LEFT JOIN [dbo].[TblDevices] d ON d.[ID] = s.[DeviceId]
            WHERE (@allowedTenantId IS NULL OR s.[TenantId] = @allowedTenantId)
              AND (@allowedDeviceId IS NULL OR s.[DeviceId] = @allowedDeviceId)
            ORDER BY [VesselName], [DeviceName]
            """;
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@allowedTenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        command.Parameters.Add("@allowedDeviceId", SqlDbType.Int).Value = (object?)allowedDeviceId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var devices = new List<BillingInvoiceDeviceOptionViewModel>();
        while (await reader.ReadAsync(cancellationToken))
        {
            devices.Add(new BillingInvoiceDeviceOptionViewModel
            {
                Id = ReadInt(reader, "ID"),
                TenantId = ReadInt(reader, "TenantId"),
                DeviceName = ReadText(reader, "DeviceName"),
                VesselName = ReadText(reader, "VesselName"),
                KitId = ReadText(reader, "KitId")
            });
        }
        return devices;
    }

    private static async Task<List<BillingInvoicePlanOptionViewModel>> GetPlanOptionsAsync(SqlConnection connection, int? allowedTenantId, int? allowedDeviceId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT DISTINCT s.[PricingPlanId] AS [ID], s.[PlanName], COALESCE(NULLIF(pp.[PlanCode], N''), N'') AS [PlanCode]
            FROM [dbo].[TblMonthlySubscription] s
            LEFT JOIN [dbo].[TblPricingPlan] pp ON pp.[ID] = s.[PricingPlanId]
            WHERE (@allowedTenantId IS NULL OR s.[TenantId] = @allowedTenantId)
              AND (@allowedDeviceId IS NULL OR s.[DeviceId] = @allowedDeviceId)
            ORDER BY s.[PlanName]
            """;
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@allowedTenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        command.Parameters.Add("@allowedDeviceId", SqlDbType.Int).Value = (object?)allowedDeviceId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var plans = new List<BillingInvoicePlanOptionViewModel>();
        while (await reader.ReadAsync(cancellationToken))
        {
            plans.Add(new BillingInvoicePlanOptionViewModel { Id = ReadInt(reader, "ID"), PlanName = ReadText(reader, "PlanName"), PlanCode = ReadText(reader, "PlanCode") });
        }
        return plans;
    }

    private static async Task<List<string>> GetDistinctTextOptionsAsync(SqlConnection connection, string expression, int? allowedTenantId, int? allowedDeviceId, CancellationToken cancellationToken)
    {
        var query = $"""
            SELECT DISTINCT {expression} AS [Value]
            FROM [dbo].[TblSubscriptionInvoice] i
            INNER JOIN [dbo].[TblMonthlySubscription] s ON s.[ID] = i.[SubscriptionId]
            WHERE (@allowedTenantId IS NULL OR s.[TenantId] = @allowedTenantId)
              AND (@allowedDeviceId IS NULL OR s.[DeviceId] = @allowedDeviceId)
              AND NULLIF({expression}, N'') IS NOT NULL
            ORDER BY [Value]
            """;
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@allowedTenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        command.Parameters.Add("@allowedDeviceId", SqlDbType.Int).Value = (object?)allowedDeviceId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = ReadText(reader, "Value");
            if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
        }
        return values;
    }

    private static string Csv(string value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
    private static int ReadInt(SqlDataReader reader, string columnName) => reader[columnName] == DBNull.Value ? 0 : Convert.ToInt32(reader[columnName]);
    private static decimal ReadDecimal(SqlDataReader reader, string columnName) => reader[columnName] == DBNull.Value ? 0 : Convert.ToDecimal(reader[columnName]);
    private static DateTime? ReadDate(SqlDataReader reader, string columnName) => reader[columnName] == DBNull.Value ? null : Convert.ToDateTime(reader[columnName]);
    private static string ReadText(SqlDataReader reader, string columnName) => reader[columnName] == DBNull.Value ? string.Empty : reader[columnName]?.ToString() ?? string.Empty;
}
