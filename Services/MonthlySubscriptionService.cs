using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public class MonthlySubscriptionService(IConfiguration configuration) : IMonthlySubscriptionService
{
    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing connection string: DefaultConnection");

    public async Task<MonthlySubscriptionPageResult> GetSubscriptionsAsync(MonthlySubscriptionFilterViewModel filter, int page, int pageSize, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);
        await EnsureNinePayQrHistorySchemaAsync(connection, cancellationToken);

        var normalizedPage = page <= 0 ? 1 : page;
        var normalizedPageSize = pageSize <= 0 ? 10 : pageSize;
        var offset = (normalizedPage - 1) * normalizedPageSize;
        var where = BuildFilterWhereClause(filter, tenantId, deviceId);

        var countQuery = $"SELECT COUNT(1) FROM [dbo].[TblMonthlySubscription] s {where}";
        var listQuery = $"""
            SELECT
                s.[ID], s.[TenantId], s.[DeviceId], s.[PricingPlanId], s.[TenantName], s.[VesselName], s.[KitId],
                s.[PlanName], s.[SubscriptionType], s.[DataLimitGb], s.[BasePlanPrice], s.[SubscriptionDays],
                s.[SubscriptionPrice], s.[OverChargePrice], s.[TotalTopUpGb], s.[Status],
                s.[StartDate], s.[EndDate], s.[NextBillingDate], s.[TotalInvoiceAmount], s.[TotalPaid],
                COALESCE((
                    SELECT TOP 1 i.[Status]
                    FROM [dbo].[TblSubscriptionInvoice] i
                    WHERE i.[SubscriptionId] = s.[ID]
                    ORDER BY CASE WHEN i.[InvoiceType] = N'SUBSCRIPTION' THEN 0 ELSE 1 END, i.[ID]
                ), N'pending') AS [InvoiceStatus]
            FROM [dbo].[TblMonthlySubscription] s
            {where}
            ORDER BY s.[StartDate] DESC, s.[ID] DESC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            """;
        var summaryQuery = $"""
            SELECT
                COUNT(1) AS [TotalSubscriptions],
                COALESCE(SUM(s.[TotalTopUpGb]), 0) AS [TotalTopUpAmount],
                COALESCE(SUM(s.[TotalInvoiceAmount]), 0) AS [TotalInvoiceAmount],
                COALESCE(SUM(s.[TotalPaid]), 0) AS [TotalPaid]
            FROM [dbo].[TblMonthlySubscription] s
            {where}
            """;

        int totalItems;
        await using (var countCommand = new SqlCommand(countQuery, connection))
        {
            AddFilterParameters(countCommand, filter, tenantId, deviceId);
            totalItems = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        }

        var totalPages = totalItems == 0 ? 1 : (int)Math.Ceiling(totalItems / (double)normalizedPageSize);
        var clampedPage = Math.Min(normalizedPage, totalPages);
        offset = (clampedPage - 1) * normalizedPageSize;

        var subscriptions = new List<MonthlySubscriptionListItemViewModel>();
        await using (var listCommand = new SqlCommand(listQuery, connection))
        {
            AddFilterParameters(listCommand, filter, tenantId, deviceId);
            listCommand.Parameters.Add("@offset", SqlDbType.Int).Value = offset;
            listCommand.Parameters.Add("@pageSize", SqlDbType.Int).Value = normalizedPageSize;
            await using var reader = await listCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                subscriptions.Add(MapSubscriptionListItem(reader));
            }
        }

        var summary = new MonthlySubscriptionSummaryViewModel();
        await using (var summaryCommand = new SqlCommand(summaryQuery, connection))
        {
            AddFilterParameters(summaryCommand, filter, tenantId, deviceId);
            await using var reader = await summaryCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                summary.TotalSubscriptions = Convert.ToInt32(reader["TotalSubscriptions"]);
                summary.TotalTopUpAmount = ReadDecimal(reader, "TotalTopUpAmount");
                summary.TotalInvoiceAmount = ReadDecimal(reader, "TotalInvoiceAmount");
                summary.TotalPaid = ReadDecimal(reader, "TotalPaid");
            }
        }

        return new MonthlySubscriptionPageResult
        {
            Subscriptions = subscriptions,
            Summary = summary,
            CurrentPage = clampedPage,
            PageSize = normalizedPageSize,
            TotalItems = totalItems
        };
    }

    public async Task<MonthlySubscriptionDetailViewModel?> GetSubscriptionDetailAsync(int id, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);

        var query = """
            SELECT
                s.[ID], s.[TenantId], s.[DeviceId], s.[PricingPlanId], s.[TenantName], s.[VesselName], s.[KitId],
                s.[PlanName], s.[SubscriptionType], s.[DataLimitGb], s.[BasePlanPrice], s.[SubscriptionDays],
                s.[SubscriptionPrice], s.[OverChargePrice], s.[TotalTopUpGb], s.[Status],
                s.[StartDate], s.[EndDate], s.[NextBillingDate], s.[TotalInvoiceAmount], s.[TotalPaid],
                COALESCE((
                    SELECT TOP 1 i.[Status]
                    FROM [dbo].[TblSubscriptionInvoice] i
                    WHERE i.[SubscriptionId] = s.[ID]
                    ORDER BY CASE WHEN i.[InvoiceType] = N'SUBSCRIPTION' THEN 0 ELSE 1 END, i.[ID]
                ), N'pending') AS [InvoiceStatus]
            FROM [dbo].[TblMonthlySubscription] s
            WHERE s.[ID] = @id
              AND (@tenantId IS NULL OR s.[TenantId] = @tenantId)
              AND (@deviceId IS NULL OR s.[DeviceId] = @deviceId)
            """;
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;

        MonthlySubscriptionListItemViewModel? subscription = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                subscription = MapSubscriptionListItem(reader);
            }
        }

        if (subscription is null)
        {
            return null;
        }

        var invoices = await GetInvoicesAsync(connection, id, cancellationToken);
        var qrSessions = await GetNinePayQrSessionsAsync(connection, invoices.Select(invoice => invoice.Id).ToList(), cancellationToken);
        var summary = new MonthlySubscriptionInvoiceSummaryViewModel
        {
            Type = "SUBSCRIPTION",
            TotalAmount = invoices.Sum(invoice => invoice.Amount),
            TotalPaid = invoices.Sum(invoice => invoice.PaidAmount),
            TotalRefund = invoices.Sum(invoice => invoice.RefundAmount),
            Status = invoices.Any(invoice => !string.Equals(invoice.Status, "paid", StringComparison.OrdinalIgnoreCase))
                ? "pending"
                : "paid"
        };
        var canEditBilling = CanEditBilling(invoices, qrSessions, out var billingEditBlockedReason);

        return new MonthlySubscriptionDetailViewModel
        {
            Subscription = subscription,
            Invoices = invoices,
            QrSessions = qrSessions,
            InvoiceSummary = summary,
            CreateInvoiceForm = new CreateSubscriptionInvoiceViewModel { SubscriptionId = id },
            UpdateInvoiceForm = new UpdateSubscriptionInvoiceViewModel { SubscriptionId = id },
            UpdateBillingForm = new UpdateMonthlySubscriptionBillingViewModel
            {
                SubscriptionId = id,
                UsageMonth = new DateTime(subscription.StartDate.Year, subscription.StartDate.Month, 1),
                StartDate = subscription.StartDate,
                EndDate = subscription.EndDate,
                NextBillingDate = subscription.NextBillingDate ?? subscription.EndDate.AddDays(1),
                BasePlanPrice = subscription.BasePlanPrice,
                OverChargePrice = subscription.OverChargePrice
            },
            CanEditBilling = canEditBilling,
            BillingEditBlockedReason = billingEditBlockedReason
        };
    }

    public async Task<List<SubscriptionDeviceOptionViewModel>> GetDeviceOptionsAsync(int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        const string query = """
            DECLARE @currentMonth date = DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1);

            SELECT
                d.[ID],
                d.[TenantID],
                d.[DeviceName],
                d.[VesselName],
                d.[KitId],
                cms.[Status] AS [CurrentMonthSubscriptionStatus]
            FROM [dbo].[TblDevices] d
            OUTER APPLY (
                SELECT TOP 1 s.[Status]
                FROM [dbo].[TblMonthlySubscription] s
                WHERE s.[DeviceId] = d.[ID]
                  AND s.[UsageMonth] = @currentMonth
                ORDER BY s.[ID] DESC
            ) cms
            WHERE (@tenantId IS NULL OR d.[TenantID] = @tenantId)
              AND (@deviceId IS NULL OR d.[ID] = @deviceId)
            ORDER BY d.[VesselName], d.[DeviceName], d.[ID]
            """;

        var devices = new List<SubscriptionDeviceOptionViewModel>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            devices.Add(new SubscriptionDeviceOptionViewModel
            {
                Id = ReadInt(reader, "ID"),
                TenantId = ReadInt(reader, "TenantID"),
                DeviceName = reader["DeviceName"]?.ToString() ?? string.Empty,
                VesselName = reader["VesselName"]?.ToString() ?? string.Empty,
                KitId = reader["KitId"]?.ToString() ?? string.Empty,
                CurrentMonthSubscriptionStatus = reader["CurrentMonthSubscriptionStatus"]?.ToString() ?? string.Empty,
                HasCurrentMonthSubscription = reader["CurrentMonthSubscriptionStatus"] != DBNull.Value
            });
        }

        return devices;
    }

    public async Task<List<SubscriptionPlanOptionViewModel>> GetPlanOptionsAsync(int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT
                dp.[DeviceId],
                dp.[PricingPlanId],
                pp.[PlanName],
                pp.[PlanCode],
                pp.[BaseData],
                dp.[ResellerPrice],
                dp.[FinalPrice],
                dp.[ResellerOverChargePrice],
                dp.[FinalOverChargePrice]
            FROM [dbo].[TblDevicePricing] dp
            INNER JOIN [dbo].[TblDevices] d ON d.[ID] = dp.[DeviceId]
            INNER JOIN [dbo].[TblPricingPlan] pp ON pp.[ID] = dp.[PricingPlanId]
            WHERE (@tenantId IS NULL OR d.[TenantID] = @tenantId)
              AND (@deviceId IS NULL OR d.[ID] = @deviceId)
            ORDER BY pp.[PlanName], pp.[PlanCode]
            """;

        var plans = new List<SubscriptionPlanOptionViewModel>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            plans.Add(new SubscriptionPlanOptionViewModel
            {
                DeviceId = ReadInt(reader, "DeviceId"),
                PricingPlanId = ReadInt(reader, "PricingPlanId"),
                PlanName = reader["PlanName"]?.ToString() ?? string.Empty,
                PlanCode = reader["PlanCode"]?.ToString() ?? string.Empty,
                DataLimitGb = ReadDecimal(reader, "BaseData"),
                ResellerPrice = ReadDecimal(reader, "ResellerPrice"),
                FinalPrice = ReadDecimal(reader, "FinalPrice"),
                ResellerOverChargePrice = ReadDecimal(reader, "ResellerOverChargePrice"),
                FinalOverChargePrice = ReadDecimal(reader, "FinalOverChargePrice")
            });
        }

        return plans;
    }

    public async Task<int> CreateSubscriptionAsync(CreateMonthlySubscriptionViewModel model, int? userId, string username, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        if (tenantId.HasValue && model.TenantId != tenantId.Value)
        {
            throw new InvalidOperationException("Tenant is outside the current user scope.");
        }

        if (deviceId.HasValue && model.DeviceId != deviceId.Value)
        {
            throw new InvalidOperationException("Device is outside the current user scope.");
        }

        if (model.StartDate.Date > model.EndDate.Date || model.StartDate.Month != model.EndDate.Month || model.StartDate.Year != model.EndDate.Year)
        {
            throw new InvalidOperationException("Start date and end date must be in the same month.");
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        var context = await GetCreateContextAsync(connection, transaction, model, cancellationToken);
        if (context is null)
        {
            throw new InvalidOperationException("The selected vessel and plan do not match an active device plan.");
        }

        var usageMonth = new DateTime(model.UsageMonth.Year, model.UsageMonth.Month, 1);
        var days = (model.EndDate.Date - model.StartDate.Date).Days + 1;
        var subscriptionPrice = Math.Round(context.FinalPrice * days / 30m, 2, MidpointRounding.AwayFromZero);
        var buyPrice = Math.Round(context.ResellerPrice * days / 30m, 2, MidpointRounding.AwayFromZero);
        var invoiceNumber = await BuildInvoiceNumberAsync(connection, transaction, cancellationToken);

        const string insertSubscriptionQuery = """
            INSERT INTO [dbo].[TblMonthlySubscription]
                ([TenantId], [DeviceId], [PricingPlanId], [TenantName], [VesselName], [KitId], [PlanName], [PlanCode],
                 [SubscriptionType], [UsageMonth], [PurchasedDate], [StartDate], [EndDate], [NextBillingDate],
                 [DataLimitGb], [BasePlanPrice], [SubscriptionDays], [SubscriptionPrice], [OverChargePrice],
                 [TotalTopUpGb], [TotalInvoiceAmount], [TotalPaid], [Status], [Created_Date], [Created_By], [Updated_Date], [Updated_By])
            OUTPUT INSERTED.[ID]
            VALUES
                (@tenantId, @deviceId, @pricingPlanId, @tenantName, @vesselName, @kitId, @planName, @planCode,
                 @subscriptionType, @usageMonth, GETDATE(), @startDate, @endDate, @nextBillingDate,
                 @dataLimitGb, @basePlanPrice, @subscriptionDays, @subscriptionPrice, @overChargePrice,
                 0, @subscriptionPrice, 0, N'pending_payment', GETDATE(), @createdBy, GETDATE(), @updatedBy)
            """;

        int subscriptionId;
        await using (var command = new SqlCommand(insertSubscriptionQuery, connection, transaction))
        {
            command.Parameters.Add("@tenantId", SqlDbType.Int).Value = model.TenantId;
            command.Parameters.Add("@deviceId", SqlDbType.Int).Value = model.DeviceId;
            command.Parameters.Add("@pricingPlanId", SqlDbType.Int).Value = model.PricingPlanId;
            command.Parameters.Add("@tenantName", SqlDbType.NVarChar, 250).Value = context.TenantName;
            command.Parameters.Add("@vesselName", SqlDbType.NVarChar, 250).Value = context.VesselName;
            command.Parameters.Add("@kitId", SqlDbType.NVarChar, 250).Value = (object?)context.KitId ?? DBNull.Value;
            command.Parameters.Add("@planName", SqlDbType.NVarChar, 250).Value = context.PlanName;
            command.Parameters.Add("@planCode", SqlDbType.NVarChar, 100).Value = context.PlanCode;
            command.Parameters.Add("@subscriptionType", SqlDbType.NVarChar, 50).Value = model.SubscriptionType.Trim();
            command.Parameters.Add("@usageMonth", SqlDbType.Date).Value = usageMonth;
            command.Parameters.Add("@startDate", SqlDbType.Date).Value = model.StartDate.Date;
            command.Parameters.Add("@endDate", SqlDbType.Date).Value = model.EndDate.Date;
            command.Parameters.Add("@nextBillingDate", SqlDbType.Date).Value = model.NextBillingDate.Date;
            AddDecimal(command, "@dataLimitGb", context.DataLimitGb);
            AddDecimal(command, "@basePlanPrice", context.FinalPrice);
            command.Parameters.Add("@subscriptionDays", SqlDbType.Int).Value = days;
            AddDecimal(command, "@subscriptionPrice", subscriptionPrice);
            AddDecimal(command, "@overChargePrice", context.FinalOverChargePrice);
            command.Parameters.Add("@createdBy", SqlDbType.NVarChar, 50).Value = username;
            command.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 50).Value = username;
            subscriptionId = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }

        await InsertInvoiceAsync(connection, transaction, subscriptionId, invoiceNumber, "SUBSCRIPTION", "Monthly subscription", context.DataLimitGb, buyPrice, subscriptionPrice, subscriptionPrice, username, cancellationToken);
        await InsertAuditAsync(connection, transaction, userId, subscriptionId, $"Created monthly subscription #{subscriptionId} by '{username}'.", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return subscriptionId;
    }

    public async Task<int> CreateInvoiceAsync(CreateSubscriptionInvoiceViewModel model, int? userId, string username, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        var subscription = await GetSubscriptionContextAsync(connection, transaction, model.SubscriptionId, tenantId, deviceId, cancellationToken)
            ?? throw new InvalidOperationException("Subscription was not found.");

        var invoiceType = string.IsNullOrWhiteSpace(model.InvoiceType) ? "OVERCHARGE" : model.InvoiceType.Trim().ToUpperInvariant();
        var dataGb = Math.Max(0, model.DataGb);
        var amount = invoiceType == "OVERCHARGE"
            ? Math.Round(dataGb * subscription.FinalOverChargePrice, 2, MidpointRounding.AwayFromZero)
            : Math.Round(model.Amount, 2, MidpointRounding.AwayFromZero);
        var buyPrice = invoiceType == "OVERCHARGE"
            ? Math.Round(dataGb * subscription.ResellerOverChargePrice, 2, MidpointRounding.AwayFromZero)
            : amount;
        var invoiceNumber = await BuildInvoiceNumberAsync(connection, transaction, cancellationToken);
        var invoiceId = await InsertInvoiceAsync(connection, transaction, model.SubscriptionId, invoiceNumber, invoiceType, model.Description, dataGb, buyPrice, amount, amount, username, cancellationToken);
        await RecalculateSubscriptionTotalsAsync(connection, transaction, model.SubscriptionId, cancellationToken);
        await InsertAuditAsync(connection, transaction, userId, model.SubscriptionId, $"Created invoice #{invoiceId} for subscription #{model.SubscriptionId} by '{username}'.", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return invoiceId;
    }

    public async Task UpdateInvoiceAsync(UpdateSubscriptionInvoiceViewModel model, int? userId, string username, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        var invoiceNumber = model.InvoiceNumber?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(invoiceNumber))
        {
            throw new InvalidOperationException("Invoice number is required.");
        }

        var status = NormalizeInvoiceStatus(model.Status);
        var amount = Math.Round(Math.Max(0, model.Amount), 2, MidpointRounding.AwayFromZero);
        var refundAmount = Math.Round(Math.Max(0, model.RefundAmount), 2, MidpointRounding.AwayFromZero);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        _ = await GetSubscriptionContextAsync(connection, transaction, model.SubscriptionId, tenantId, deviceId, cancellationToken)
            ?? throw new InvalidOperationException("Subscription was not found.");

        if (await IsInvoicePaidByBankTransferAsync(connection, transaction, model.InvoiceId, cancellationToken))
        {
            throw new InvalidOperationException("Invoice was paid by bank transfer and cannot be updated.");
        }

        const string query = """
            IF EXISTS (
                SELECT 1
                FROM [dbo].[TblSubscriptionInvoice]
                WHERE [InvoiceNumber] = @invoiceNumber
                  AND [ID] <> @invoiceId
            )
            BEGIN
                THROW 50001, 'Invoice number already exists.', 1;
            END;

            UPDATE [dbo].[TblSubscriptionInvoice]
            SET
                [InvoiceNumber] = @invoiceNumber,
                [Amount] = @amount,
                [SalePrice] = @amount,
                [MarginAmount] = @amount - [BuyPrice],
                [RefundAmount] = @refundAmount,
                [CompletedAt] = @completedAt,
                [Status] = @status,
                [Updated_Date] = GETDATE(),
                [Updated_By] = @updatedBy
            WHERE [ID] = @invoiceId
              AND [SubscriptionId] = @subscriptionId
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@invoiceId", SqlDbType.Int).Value = model.InvoiceId;
        command.Parameters.Add("@subscriptionId", SqlDbType.Int).Value = model.SubscriptionId;
        command.Parameters.Add("@invoiceNumber", SqlDbType.NVarChar, 100).Value = invoiceNumber;
        AddDecimal(command, "@amount", amount);
        AddDecimal(command, "@refundAmount", refundAmount);
        command.Parameters.Add("@completedAt", SqlDbType.DateTime).Value = model.CompletedAt.HasValue ? model.CompletedAt.Value : (object)DBNull.Value;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 50).Value = status;
        command.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 50).Value = username;

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new InvalidOperationException("Invoice was not found.");
        }

        await RecalculateSubscriptionTotalsAsync(connection, transaction, model.SubscriptionId, cancellationToken);
        await InsertAuditAsync(connection, transaction, userId, model.SubscriptionId, $"Updated invoice #{model.InvoiceId} by '{username}'.", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateSubscriptionBillingAsync(UpdateMonthlySubscriptionBillingViewModel model, int? userId, string username, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        if (model.StartDate.Date > model.EndDate.Date || model.StartDate.Month != model.EndDate.Month || model.StartDate.Year != model.EndDate.Year)
        {
            throw new InvalidOperationException("Start date and end date must be in the same month.");
        }

        var usageMonth = new DateTime(model.UsageMonth.Year, model.UsageMonth.Month, 1);
        var startDate = model.StartDate.Date;
        var endDate = model.EndDate.Date;
        var nextBillingDate = model.NextBillingDate.Date;
        var basePlanPrice = Math.Round(Math.Max(0, model.BasePlanPrice), 2, MidpointRounding.AwayFromZero);
        var overChargePrice = Math.Round(Math.Max(0, model.OverChargePrice), 2, MidpointRounding.AwayFromZero);
        var subscriptionDays = (endDate - startDate).Days + 1;
        var subscriptionPrice = Math.Round(basePlanPrice * subscriptionDays / 30m, 2, MidpointRounding.AwayFromZero);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureNinePayQrHistorySchemaAsync(connection, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        var invoice = await GetEditableSubscriptionInvoiceAsync(connection, transaction, model.SubscriptionId, tenantId, deviceId, cancellationToken)
            ?? throw new InvalidOperationException("Subscription invoice was not found.");

        if (!string.Equals(invoice.Status, "pending", StringComparison.OrdinalIgnoreCase) || invoice.PaidAmount > 0)
        {
            throw new InvalidOperationException("Subscription invoice must be unpaid and pending before billing can be updated.");
        }

        if (await HasActiveQrSessionAsync(connection, transaction, invoice.Id, cancellationToken))
        {
            throw new InvalidOperationException("Subscription invoice has an active QR session. Please wait until the QR expires before updating billing.");
        }

        const string updateSubscriptionQuery = """
            UPDATE [dbo].[TblMonthlySubscription]
            SET
                [UsageMonth] = @usageMonth,
                [StartDate] = @startDate,
                [EndDate] = @endDate,
                [NextBillingDate] = @nextBillingDate,
                [BasePlanPrice] = @basePlanPrice,
                [SubscriptionDays] = @subscriptionDays,
                [SubscriptionPrice] = @subscriptionPrice,
                [OverChargePrice] = @overChargePrice,
                [Updated_Date] = GETDATE(),
                [Updated_By] = @updatedBy
            WHERE [ID] = @subscriptionId
              AND (@tenantId IS NULL OR [TenantId] = @tenantId)
              AND (@deviceId IS NULL OR [DeviceId] = @deviceId)
            """;

        await using (var command = new SqlCommand(updateSubscriptionQuery, connection, transaction))
        {
            command.Parameters.Add("@subscriptionId", SqlDbType.Int).Value = model.SubscriptionId;
            command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
            command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;
            command.Parameters.Add("@usageMonth", SqlDbType.Date).Value = usageMonth;
            command.Parameters.Add("@startDate", SqlDbType.Date).Value = startDate;
            command.Parameters.Add("@endDate", SqlDbType.Date).Value = endDate;
            command.Parameters.Add("@nextBillingDate", SqlDbType.Date).Value = nextBillingDate;
            AddDecimal(command, "@basePlanPrice", basePlanPrice);
            command.Parameters.Add("@subscriptionDays", SqlDbType.Int).Value = subscriptionDays;
            AddDecimal(command, "@subscriptionPrice", subscriptionPrice);
            AddDecimal(command, "@overChargePrice", overChargePrice);
            command.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 50).Value = username;

            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new InvalidOperationException("Subscription was not found.");
            }
        }

        const string updateInvoiceQuery = """
            UPDATE [dbo].[TblSubscriptionInvoice]
            SET
                [SalePrice] = @subscriptionPrice,
                [Amount] = @subscriptionPrice,
                [MarginAmount] = @subscriptionPrice - [BuyPrice],
                [Updated_Date] = GETDATE(),
                [Updated_By] = @updatedBy
            WHERE [ID] = @invoiceId
              AND [SubscriptionId] = @subscriptionId
            """;

        await using (var command = new SqlCommand(updateInvoiceQuery, connection, transaction))
        {
            command.Parameters.Add("@invoiceId", SqlDbType.Int).Value = invoice.Id;
            command.Parameters.Add("@subscriptionId", SqlDbType.Int).Value = model.SubscriptionId;
            AddDecimal(command, "@subscriptionPrice", subscriptionPrice);
            command.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 50).Value = username;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await RecalculateSubscriptionTotalsAsync(connection, transaction, model.SubscriptionId, cancellationToken);
        await InsertAuditAsync(connection, transaction, userId, model.SubscriptionId, $"Updated subscription #{model.SubscriptionId} billing by '{username}'.", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> IsInvoicePaidByBankTransferAsync(SqlConnection connection, SqlTransaction transaction, int invoiceId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT TOP 1 1
            FROM [dbo].[TblSubscriptionInvoice] i
            WHERE i.[ID] = @invoiceId
              AND EXISTS (
                  SELECT 1
                  FROM [dbo].[TblPaymentTransaction] t
                  WHERE t.[Provider] = N'9Pay'
                    AND LOWER(t.[Status]) = N'paid'
                    AND (
                        t.[InvoiceId] = i.[ID]
                        OR t.[InvoiceNumber] = i.[InvoiceNumber]
                        OR (
                            NULLIF(i.[ReceiptNumber], N'') IS NOT NULL
                            AND t.[ProviderPaymentNo] = i.[ReceiptNumber]
                        )
                    )
              )
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@invoiceId", SqlDbType.Int).Value = invoiceId;
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<EditableSubscriptionInvoice?> GetEditableSubscriptionInvoiceAsync(SqlConnection connection, SqlTransaction transaction, int subscriptionId, int? tenantId, int? deviceId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT TOP 1
                i.[ID],
                i.[Status],
                i.[PaidAmount]
            FROM [dbo].[TblSubscriptionInvoice] i
            INNER JOIN [dbo].[TblMonthlySubscription] s ON s.[ID] = i.[SubscriptionId]
            WHERE i.[SubscriptionId] = @subscriptionId
              AND i.[InvoiceType] = N'SUBSCRIPTION'
              AND (@tenantId IS NULL OR s.[TenantId] = @tenantId)
              AND (@deviceId IS NULL OR s.[DeviceId] = @deviceId)
            ORDER BY i.[ID]
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@subscriptionId", SqlDbType.Int).Value = subscriptionId;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new EditableSubscriptionInvoice(ReadInt(reader, "ID"), reader["Status"]?.ToString() ?? string.Empty, ReadDecimal(reader, "PaidAmount"))
            : null;
    }

    private static async Task<bool> HasActiveQrSessionAsync(SqlConnection connection, SqlTransaction transaction, int invoiceId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT TOP 1 1
            FROM [dbo].[TblNinePayQrSessionInvoice] qi
            INNER JOIN [dbo].[TblNinePayQrSession] q ON q.[ID] = qi.[QrSessionId]
            WHERE qi.[InvoiceId] = @invoiceId
              AND q.[QrExpiresAt] > GETUTCDATE()
              AND LOWER(ISNULL(q.[Status], N'')) NOT IN (N'paid', N'expired', N'cancelled', N'canceled', N'void')
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@invoiceId", SqlDbType.Int).Value = invoiceId;
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static bool CanEditBilling(IReadOnlyCollection<SubscriptionInvoiceViewModel> invoices, IReadOnlyCollection<NinePayQrSessionHistoryViewModel> qrSessions, out string blockedReason)
    {
        var subscriptionInvoice = invoices
            .OrderBy(invoice => invoice.Id)
            .FirstOrDefault(invoice => string.Equals(invoice.InvoiceType, "SUBSCRIPTION", StringComparison.OrdinalIgnoreCase));

        if (subscriptionInvoice is null)
        {
            blockedReason = "Không tìm thấy invoice subscription.";
            return false;
        }

        if (!string.Equals(subscriptionInvoice.Status, "pending", StringComparison.OrdinalIgnoreCase) || subscriptionInvoice.PaidAmount > 0)
        {
            blockedReason = "Invoice subscription đã thanh toán hoặc không còn ở trạng thái pending.";
            return false;
        }

        var hasActiveQr = qrSessions.Any(qr =>
            qr.InvoiceId == subscriptionInvoice.Id
            && qr.ExpiresAt > DateTime.UtcNow
            && !string.Equals(qr.Status, "paid", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(qr.Status, "expired", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(qr.Status, "cancelled", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(qr.Status, "canceled", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(qr.Status, "void", StringComparison.OrdinalIgnoreCase));

        if (hasActiveQr)
        {
            blockedReason = "Invoice subscription đang có QR session còn hiệu lực.";
            return false;
        }

        blockedReason = string.Empty;
        return true;
    }

    public async Task UpdateSubscriptionStatusAsync(UpdateMonthlySubscriptionStatusViewModel model, int? userId, string username, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        var status = NormalizeSubscriptionStatus(model.Status);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        const string query = """
            UPDATE [dbo].[TblMonthlySubscription]
            SET
                [Status] = @status,
                [Updated_Date] = GETDATE(),
                [Updated_By] = @updatedBy
            WHERE [ID] = @subscriptionId
              AND (@tenantId IS NULL OR [TenantId] = @tenantId)
              AND (@deviceId IS NULL OR [DeviceId] = @deviceId)
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@subscriptionId", SqlDbType.Int).Value = model.SubscriptionId;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 50).Value = status;
        command.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 50).Value = username;

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new InvalidOperationException("Subscription was not found.");
        }

        await InsertAuditAsync(connection, transaction, userId, model.SubscriptionId, $"Updated subscription #{model.SubscriptionId} status to '{status}' by '{username}'.", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string BuildFilterWhereClause(MonthlySubscriptionFilterViewModel filter, int? tenantId, int? deviceId)
    {
        return """
            WHERE (@scopeTenantId IS NULL OR s.[TenantId] = @scopeTenantId)
              AND (@scopeDeviceId IS NULL OR s.[DeviceId] = @scopeDeviceId)
              AND (@tenantFilter IS NULL OR s.[TenantId] = @tenantFilter)
              AND (@deviceFilter IS NULL OR s.[DeviceId] = @deviceFilter)
              AND (@planFilter IS NULL OR s.[PricingPlanId] = @planFilter)
              AND (@kitId IS NULL OR s.[KitId] LIKE @kitPattern)
              AND (@status IS NULL OR s.[Status] = @status)
              AND (@monthFrom IS NULL OR s.[StartDate] >= @monthFrom)
              AND (@monthTo IS NULL OR s.[StartDate] < DATEADD(day, 1, @monthTo))
              AND (@nextBillingFrom IS NULL OR s.[NextBillingDate] >= @nextBillingFrom)
              AND (@nextBillingTo IS NULL OR s.[NextBillingDate] <= @nextBillingTo)
              AND (
                    (@invoicePaidFrom IS NULL AND @invoicePaidTo IS NULL)
                    OR EXISTS (
                        SELECT 1 FROM [dbo].[TblSubscriptionInvoice] paidInv
                        WHERE paidInv.[SubscriptionId] = s.[ID]
                          AND paidInv.[CompletedAt] IS NOT NULL
                          AND (@invoicePaidFrom IS NULL OR paidInv.[CompletedAt] >= @invoicePaidFrom)
                          AND (@invoicePaidTo IS NULL OR paidInv.[CompletedAt] < DATEADD(day, 1, @invoicePaidTo))
                    )
              )
              AND (
                    @invoiceStatus IS NULL
                    OR EXISTS (
                        SELECT 1 FROM [dbo].[TblSubscriptionInvoice] inv
                        WHERE inv.[SubscriptionId] = s.[ID]
                          AND inv.[Status] = @invoiceStatus
                    )
              )
            """;
    }

    private static void AddFilterParameters(SqlCommand command, MonthlySubscriptionFilterViewModel filter, int? tenantId, int? deviceId)
    {
        command.Parameters.Add("@scopeTenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@scopeDeviceId", SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;
        command.Parameters.Add("@tenantFilter", SqlDbType.Int).Value = (object?)filter.TenantId ?? DBNull.Value;
        command.Parameters.Add("@deviceFilter", SqlDbType.Int).Value = (object?)filter.DeviceId ?? DBNull.Value;
        command.Parameters.Add("@planFilter", SqlDbType.Int).Value = (object?)filter.PricingPlanId ?? DBNull.Value;
        command.Parameters.Add("@kitId", SqlDbType.NVarChar, 250).Value = string.IsNullOrWhiteSpace(filter.KitId) ? DBNull.Value : (object)filter.KitId.Trim();
        command.Parameters.Add("@kitPattern", SqlDbType.NVarChar, 260).Value = string.IsNullOrWhiteSpace(filter.KitId) ? DBNull.Value : (object)$"%{filter.KitId.Trim()}%";
        command.Parameters.Add("@status", SqlDbType.NVarChar, 50).Value = string.IsNullOrWhiteSpace(filter.Status) ? DBNull.Value : (object)filter.Status.Trim();
        command.Parameters.Add("@invoiceStatus", SqlDbType.NVarChar, 50).Value = string.IsNullOrWhiteSpace(filter.InvoiceStatus) ? DBNull.Value : (object)filter.InvoiceStatus.Trim();
        command.Parameters.Add("@monthFrom", SqlDbType.Date).Value = (object?)filter.MonthFrom?.Date ?? DBNull.Value;
        command.Parameters.Add("@monthTo", SqlDbType.Date).Value = (object?)filter.MonthTo?.Date ?? DBNull.Value;
        command.Parameters.Add("@nextBillingFrom", SqlDbType.Date).Value = (object?)filter.NextBillingFrom?.Date ?? DBNull.Value;
        command.Parameters.Add("@nextBillingTo", SqlDbType.Date).Value = (object?)filter.NextBillingTo?.Date ?? DBNull.Value;
        command.Parameters.Add("@invoicePaidFrom", SqlDbType.Date).Value = (object?)filter.InvoicePaidFrom?.Date ?? DBNull.Value;
        command.Parameters.Add("@invoicePaidTo", SqlDbType.Date).Value = (object?)filter.InvoicePaidTo?.Date ?? DBNull.Value;
    }

    private async Task<List<SubscriptionInvoiceViewModel>> GetInvoicesAsync(SqlConnection connection, int subscriptionId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT [ID], [SubscriptionId], [InvoiceNumber], [ReceiptNumber], [PoNumber], [InvoiceType], [Description],
                   [DataGb], [BuyPrice], [SalePrice], [MarginAmount], [Amount], [PaidAmount], [RefundAmount],
                   [Status], [CreatedAt], [CompletedAt],
                   CASE WHEN EXISTS (
                       SELECT 1
                       FROM [dbo].[TblPaymentTransaction] t
                       WHERE t.[Provider] = N'9Pay'
                         AND LOWER(t.[Status]) = N'paid'
                         AND (
                             t.[InvoiceId] = [dbo].[TblSubscriptionInvoice].[ID]
                             OR t.[InvoiceNumber] = [dbo].[TblSubscriptionInvoice].[InvoiceNumber]
                             OR (
                                 NULLIF([dbo].[TblSubscriptionInvoice].[ReceiptNumber], N'') IS NOT NULL
                                 AND t.[ProviderPaymentNo] = [dbo].[TblSubscriptionInvoice].[ReceiptNumber]
                             )
                         )
                   ) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS [IsPaidByBankTransfer]
            FROM [dbo].[TblSubscriptionInvoice]
            WHERE [SubscriptionId] = @subscriptionId
            ORDER BY CASE WHEN LOWER([Status]) = N'pending' THEN 0 ELSE 1 END,
                     [CreatedAt] ASC,
                     [ID] ASC
            """;
        var invoices = new List<SubscriptionInvoiceViewModel>();
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@subscriptionId", SqlDbType.Int).Value = subscriptionId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            invoices.Add(new SubscriptionInvoiceViewModel
            {
                Id = ReadInt(reader, "ID"),
                SubscriptionId = ReadInt(reader, "SubscriptionId"),
                InvoiceNumber = reader["InvoiceNumber"]?.ToString() ?? string.Empty,
                ReceiptNumber = reader["ReceiptNumber"]?.ToString() ?? string.Empty,
                PoNumber = reader["PoNumber"]?.ToString() ?? string.Empty,
                InvoiceType = reader["InvoiceType"]?.ToString() ?? string.Empty,
                Description = reader["Description"]?.ToString() ?? string.Empty,
                DataGb = ReadDecimal(reader, "DataGb"),
                BuyPrice = ReadDecimal(reader, "BuyPrice"),
                SalePrice = ReadDecimal(reader, "SalePrice"),
                MarginAmount = ReadDecimal(reader, "MarginAmount"),
                Amount = ReadDecimal(reader, "Amount"),
                PaidAmount = ReadDecimal(reader, "PaidAmount"),
                RefundAmount = ReadDecimal(reader, "RefundAmount"),
                Status = reader["Status"]?.ToString() ?? string.Empty,
                CreatedAt = ReadDate(reader, "CreatedAt") ?? DateTime.MinValue,
                CompletedAt = ReadDate(reader, "CompletedAt"),
                IsPaidByBankTransfer = reader["IsPaidByBankTransfer"] != DBNull.Value && Convert.ToBoolean(reader["IsPaidByBankTransfer"], CultureInfo.InvariantCulture)
            });
        }

        return invoices;
    }

    private async Task<List<NinePayQrSessionHistoryViewModel>> GetNinePayQrSessionsAsync(SqlConnection connection, IReadOnlyList<int> invoiceIds, CancellationToken cancellationToken)
    {
        var distinctInvoiceIds = invoiceIds.Where(id => id > 0).Distinct().ToList();
        if (distinctInvoiceIds.Count == 0)
        {
            return [];
        }

        var parameterNames = distinctInvoiceIds.Select((_, index) => $"@invoiceId{index}").ToList();
        var query = $"""
            SELECT
                qi.[InvoiceId],
                q.[ID],
                q.[Created_Date],
                q.[Created_By],
                COALESCE(q.[Channel], N'9pay') AS [Channel],
                q.[QrExpiresAt],
                CASE WHEN q.[QrExpiresAt] <= GETUTCDATE() THEN 0 ELSE DATEDIFF(MINUTE, GETUTCDATE(), q.[QrExpiresAt]) / 60.0 END AS [HoursRemaining],
                qi.[AmountVnd] AS [InvoiceAmountVnd],
                q.[BankAccountNo],
                q.[TransferContent],
                q.[TransferFeeVnd],
                q.[ProviderInvoiceNo],
                q.[ProviderPaymentNo],
                COALESCE(NULLIF(q.[IpnPaymentNo], N''), latestLog.[PaymentNo], q.[ProviderPaymentNo]) AS [IpnPaymentNo],
                COALESCE(q.[IpnReceivedAt], latestLog.[ReceivedAt]) AS [IpnReceivedAt],
                COALESCE(NULLIF(q.[IpnProcessStatus], N''), latestLog.[ProcessStatus]) AS [IpnProcessStatus],
                q.[Status],
                q.[AmountVnd] AS [SessionTotalVnd],
                CASE WHEN linked.[InvoiceCount] > 1 THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS [IsGrouped]
            FROM [dbo].[TblNinePayQrSessionInvoice] qi
            INNER JOIN [dbo].[TblNinePayQrSession] q ON q.[ID] = qi.[QrSessionId]
            OUTER APPLY (
                SELECT TOP 1
                    [ReceivedAt],
                    [PaymentNo],
                    [ProcessStatus]
                FROM [dbo].[TblNinePayIpnLog]
                WHERE [ProviderInvoiceNo] = q.[ProviderInvoiceNo]
                ORDER BY [ID] DESC
            ) latestLog
            OUTER APPLY (
                SELECT COUNT(1) AS [InvoiceCount]
                FROM [dbo].[TblNinePayQrSessionInvoice] linked
                WHERE linked.[QrSessionId] = q.[ID]
            ) linked
            WHERE qi.[InvoiceId] IN ({string.Join(",", parameterNames)})
            ORDER BY q.[Created_Date] DESC, q.[ID] DESC
            """;

        var sessions = new List<NinePayQrSessionHistoryViewModel>();
        await using var command = new SqlCommand(query, connection);
        for (var index = 0; index < distinctInvoiceIds.Count; index++)
        {
            command.Parameters.Add(parameterNames[index], SqlDbType.Int).Value = distinctInvoiceIds[index];
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(new NinePayQrSessionHistoryViewModel
            {
                Id = ReadInt(reader, "ID"),
                InvoiceId = ReadInt(reader, "InvoiceId"),
                CreatedAt = ReadDate(reader, "Created_Date") ?? DateTime.MinValue,
                CreatedBy = reader["Created_By"]?.ToString() ?? string.Empty,
                Channel = reader["Channel"]?.ToString() ?? "9pay",
                ExpiresAt = ReadDate(reader, "QrExpiresAt") ?? DateTime.MinValue,
                HoursRemaining = ReadDecimal(reader, "HoursRemaining"),
                InvoiceAmountVnd = ReadDecimal(reader, "InvoiceAmountVnd"),
                BankAccountNo = reader["BankAccountNo"]?.ToString() ?? string.Empty,
                TransferContent = reader["TransferContent"]?.ToString() ?? string.Empty,
                TransferFeeVnd = ReadDecimal(reader, "TransferFeeVnd"),
                ProviderRef = reader["ProviderInvoiceNo"]?.ToString() ?? string.Empty,
                Status = reader["Status"]?.ToString() ?? string.Empty,
                IpnPaymentNo = reader["IpnPaymentNo"]?.ToString() ?? reader["ProviderPaymentNo"]?.ToString() ?? string.Empty,
                IpnReceivedAt = ReadDate(reader, "IpnReceivedAt"),
                IpnProcessStatus = reader["IpnProcessStatus"]?.ToString() ?? string.Empty,
                SessionTotalVnd = ReadDecimal(reader, "SessionTotalVnd"),
                IsGrouped = reader["IsGrouped"] != DBNull.Value && Convert.ToBoolean(reader["IsGrouped"], CultureInfo.InvariantCulture)
            });
        }

        return sessions;
    }

    private async Task<CreateSubscriptionContext?> GetCreateContextAsync(SqlConnection connection, SqlTransaction transaction, CreateMonthlySubscriptionViewModel model, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT TOP 1
                t.[TenantName],
                d.[VesselName],
                d.[KitId],
                pp.[PlanName],
                pp.[PlanCode],
                pp.[BaseData],
                dp.[ResellerPrice],
                dp.[FinalPrice],
                dp.[ResellerOverChargePrice],
                dp.[FinalOverChargePrice]
            FROM [dbo].[TblDevices] d
            INNER JOIN [dbo].[TblTenant] t ON t.[ID] = d.[TenantID]
            INNER JOIN [dbo].[TblDevicePricing] dp ON dp.[DeviceId] = d.[ID] AND dp.[PricingPlanId] = @pricingPlanId
            INNER JOIN [dbo].[TblPricingPlan] pp ON pp.[ID] = dp.[PricingPlanId]
            WHERE d.[ID] = @deviceId
              AND d.[TenantID] = @tenantId
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = model.TenantId;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = model.DeviceId;
        command.Parameters.Add("@pricingPlanId", SqlDbType.Int).Value = model.PricingPlanId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CreateSubscriptionContext(
            reader["TenantName"]?.ToString() ?? string.Empty,
            reader["VesselName"]?.ToString() ?? string.Empty,
            reader["KitId"]?.ToString() ?? string.Empty,
            reader["PlanName"]?.ToString() ?? string.Empty,
            reader["PlanCode"]?.ToString() ?? string.Empty,
            ReadDecimal(reader, "BaseData"),
            ReadDecimal(reader, "ResellerPrice"),
            ReadDecimal(reader, "FinalPrice"),
            ReadDecimal(reader, "ResellerOverChargePrice"),
            ReadDecimal(reader, "FinalOverChargePrice"));
    }

    private async Task<SubscriptionPriceContext?> GetSubscriptionContextAsync(SqlConnection connection, SqlTransaction transaction, int subscriptionId, int? tenantId, int? deviceId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT TOP 1
                s.[ID],
                dp.[ResellerOverChargePrice],
                dp.[FinalOverChargePrice]
            FROM [dbo].[TblMonthlySubscription] s
            INNER JOIN [dbo].[TblDevicePricing] dp ON dp.[DeviceId] = s.[DeviceId] AND dp.[PricingPlanId] = s.[PricingPlanId]
            WHERE s.[ID] = @subscriptionId
              AND (@tenantId IS NULL OR s.[TenantId] = @tenantId)
              AND (@deviceId IS NULL OR s.[DeviceId] = @deviceId)
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@subscriptionId", SqlDbType.Int).Value = subscriptionId;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SubscriptionPriceContext(ReadDecimal(reader, "ResellerOverChargePrice"), ReadDecimal(reader, "FinalOverChargePrice"))
            : null;
    }

    private async Task<int> InsertInvoiceAsync(SqlConnection connection, SqlTransaction transaction, int subscriptionId, string invoiceNumber, string invoiceType, string? description, decimal dataGb, decimal buyPrice, decimal salePrice, decimal amount, string username, CancellationToken cancellationToken)
    {
        const string query = """
            INSERT INTO [dbo].[TblSubscriptionInvoice]
                ([SubscriptionId], [InvoiceNumber], [InvoiceType], [Description], [DataGb], [BuyPrice], [SalePrice], [MarginAmount], [Amount], [PaidAmount], [RefundAmount], [Status], [CreatedAt], [Created_Date], [Created_By], [Updated_Date], [Updated_By])
            OUTPUT INSERTED.[ID]
            VALUES
                (@subscriptionId, @invoiceNumber, @invoiceType, @description, @dataGb, @buyPrice, @salePrice, @marginAmount, @amount, 0, 0, N'pending', GETDATE(), GETDATE(), @createdBy, GETDATE(), @updatedBy)
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@subscriptionId", SqlDbType.Int).Value = subscriptionId;
        command.Parameters.Add("@invoiceNumber", SqlDbType.NVarChar, 100).Value = invoiceNumber;
        command.Parameters.Add("@invoiceType", SqlDbType.NVarChar, 50).Value = invoiceType;
        command.Parameters.Add("@description", SqlDbType.NVarChar, 500).Value = string.IsNullOrWhiteSpace(description) ? DBNull.Value : (object)description.Trim();
        AddDecimal(command, "@dataGb", dataGb);
        AddDecimal(command, "@buyPrice", buyPrice);
        AddDecimal(command, "@salePrice", salePrice);
        AddDecimal(command, "@marginAmount", salePrice - buyPrice);
        AddDecimal(command, "@amount", amount);
        command.Parameters.Add("@createdBy", SqlDbType.NVarChar, 50).Value = username;
        command.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 50).Value = username;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task RecalculateSubscriptionTotalsAsync(SqlConnection connection, SqlTransaction transaction, int subscriptionId, CancellationToken cancellationToken)
    {
        const string query = """
            UPDATE s
            SET
                [TotalTopUpGb] = COALESCE(inv.[TotalTopUpGb], 0),
                [TotalInvoiceAmount] = COALESCE(inv.[TotalInvoiceAmount], 0),
                [TotalPaid] = COALESCE(inv.[TotalPaid], 0),
                [Updated_Date] = GETDATE()
            FROM [dbo].[TblMonthlySubscription] s
            OUTER APPLY (
                SELECT
                    SUM(CASE WHEN i.[InvoiceType] = N'OVERCHARGE' THEN i.[DataGb] ELSE 0 END) AS [TotalTopUpGb],
                    SUM(i.[Amount]) AS [TotalInvoiceAmount],
                    SUM(i.[PaidAmount]) AS [TotalPaid]
                FROM [dbo].[TblSubscriptionInvoice] i
                WHERE i.[SubscriptionId] = s.[ID]
            ) inv
            WHERE s.[ID] = @subscriptionId
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@subscriptionId", SqlDbType.Int).Value = subscriptionId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<string> BuildInvoiceNumberAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var now = DateTime.Today;
        var dayPart = now.ToString("yyMMdd", CultureInfo.InvariantCulture);
        var monthPrefix = now.ToString("yyMM", CultureInfo.InvariantCulture);
        const string query = """
            SELECT ISNULL(MAX(TRY_CONVERT(int, RIGHT([InvoiceNumber], 4))), 0)
            FROM [dbo].[TblSubscriptionInvoice]
            WHERE [InvoiceNumber] LIKE @prefix ESCAPE '\'
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@prefix", SqlDbType.NVarChar, 100).Value = $@"SNINV\_{monthPrefix}__-____";
        var nextNumber = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) + 1;
        if (nextNumber > 9999)
        {
            throw new InvalidOperationException("Monthly invoice sequence limit reached. Maximum is 9999 invoices per month.");
        }

        return $"SNINV_{dayPart}-{nextNumber:0000}";
    }

    private async Task InsertAuditAsync(SqlConnection connection, SqlTransaction transaction, int? userId, int subscriptionId, string detail, CancellationToken cancellationToken)
    {
        const string query = """
            IF OBJECT_ID(N'[dbo].[TblAuditLog]', N'U') IS NOT NULL
            BEGIN
                INSERT INTO [dbo].[TblAuditLog] ([UserId], [DeviceId], [LogAction], [LogDetail], [Created_Date])
                VALUES (@userId, @subscriptionId, N'monthly_subscription', @detail, GETDATE())
            END
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@userId", SqlDbType.Int).Value = (object?)userId ?? DBNull.Value;
        command.Parameters.Add("@subscriptionId", SqlDbType.Int).Value = subscriptionId;
        command.Parameters.Add("@detail", SqlDbType.NVarChar, 1000).Value = detail;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureNinePayQrHistorySchemaAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string query = """
            IF OBJECT_ID(N'[dbo].[TblNinePayQrSession]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[TblNinePayQrSession](
                    [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblNinePayQrSession] PRIMARY KEY,
                    [InvoiceId] int NOT NULL,
                    [SubscriptionId] int NOT NULL,
                    [InvoiceNumber] nvarchar(100) NOT NULL,
                    [ProviderInvoiceNo] nvarchar(100) NOT NULL,
                    [ProviderPaymentNo] nvarchar(100) NULL,
                    [ProviderStatus] nvarchar(50) NULL,
                    [Status] nvarchar(50) NOT NULL CONSTRAINT [DF_TblNinePayQrSession_Status] DEFAULT(N'Pending'),
                    [AmountVnd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblNinePayQrSession_AmountVnd] DEFAULT(0),
                    [Currency] nvarchar(10) NULL,
                    [Method] nvarchar(50) NULL,
                    [Description] nvarchar(500) NULL,
                    [Channel] nvarchar(50) NULL,
                    [Created_By] nvarchar(100) NULL,
                    [TransferFeeVnd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblNinePayQrSession_TransferFeeVnd] DEFAULT(0),
                    [BankAccountNo] nvarchar(100) NULL,
                    [TransferContent] nvarchar(500) NULL,
                    [IpnPaymentNo] nvarchar(100) NULL,
                    [IpnReceivedAt] datetime NULL,
                    [IpnProcessStatus] nvarchar(50) NULL,
                    [IpnProcessMessage] nvarchar(500) NULL,
                    [IpnChecksum] nvarchar(200) NULL,
                    [IpnResultBase64] nvarchar(max) NULL,
                    [IpnRawJson] nvarchar(max) NULL,
                    [PaidAt] datetime NULL,
                    [QrStartedAt] datetime NOT NULL,
                    [QrExpiresAt] datetime NOT NULL,
                    [ProviderResponseJson] nvarchar(max) NULL,
                    [DebugLog] nvarchar(max) NULL,
                    [Created_Date] datetime NOT NULL CONSTRAINT [DF_TblNinePayQrSession_Created_Date] DEFAULT(GETDATE()),
                    [Updated_Date] datetime NOT NULL CONSTRAINT [DF_TblNinePayQrSession_Updated_Date] DEFAULT(GETDATE())
                );
            END;

            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'Channel') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [Channel] nvarchar(50) NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'Created_By') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [Created_By] nvarchar(100) NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'TransferFeeVnd') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [TransferFeeVnd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblNinePayQrSession_TransferFeeVnd_Read] DEFAULT(0);
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'BankAccountNo') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [BankAccountNo] nvarchar(100) NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'TransferContent') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [TransferContent] nvarchar(500) NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'IpnPaymentNo') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [IpnPaymentNo] nvarchar(100) NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'IpnReceivedAt') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [IpnReceivedAt] datetime NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'IpnProcessStatus') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [IpnProcessStatus] nvarchar(50) NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'IpnProcessMessage') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [IpnProcessMessage] nvarchar(500) NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'IpnChecksum') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [IpnChecksum] nvarchar(200) NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'IpnResultBase64') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [IpnResultBase64] nvarchar(max) NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'IpnRawJson') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [IpnRawJson] nvarchar(max) NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'PaidAt') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [PaidAt] datetime NULL;

            IF OBJECT_ID(N'[dbo].[TblNinePayQrSessionInvoice]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[TblNinePayQrSessionInvoice](
                    [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblNinePayQrSessionInvoice] PRIMARY KEY,
                    [QrSessionId] int NOT NULL,
                    [InvoiceId] int NOT NULL,
                    [SubscriptionId] int NOT NULL,
                    [InvoiceNumber] nvarchar(100) NOT NULL,
                    [AmountVnd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblNinePayQrSessionInvoice_AmountVnd] DEFAULT(0),
                    [Status] nvarchar(50) NOT NULL CONSTRAINT [DF_TblNinePayQrSessionInvoice_Status] DEFAULT(N'Pending'),
                    [Created_Date] datetime NOT NULL CONSTRAINT [DF_TblNinePayQrSessionInvoice_Created_Date] DEFAULT(GETDATE()),
                    [Updated_Date] datetime NOT NULL CONSTRAINT [DF_TblNinePayQrSessionInvoice_Updated_Date] DEFAULT(GETDATE())
                );
            END;
            """;

        await using var command = new SqlCommand(query, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static MonthlySubscriptionListItemViewModel MapSubscriptionListItem(SqlDataReader reader)
    {
        return new MonthlySubscriptionListItemViewModel
        {
            Id = ReadInt(reader, "ID"),
            TenantId = ReadInt(reader, "TenantId"),
            DeviceId = ReadInt(reader, "DeviceId"),
            PricingPlanId = ReadInt(reader, "PricingPlanId"),
            TenantName = reader["TenantName"]?.ToString() ?? string.Empty,
            VesselName = reader["VesselName"]?.ToString() ?? string.Empty,
            KitId = reader["KitId"]?.ToString() ?? string.Empty,
            PlanName = reader["PlanName"]?.ToString() ?? string.Empty,
            SubscriptionType = reader["SubscriptionType"]?.ToString() ?? string.Empty,
            DataLimitGb = ReadDecimal(reader, "DataLimitGb"),
            BasePlanPrice = ReadDecimal(reader, "BasePlanPrice"),
            SubscriptionDays = ReadInt(reader, "SubscriptionDays"),
            SubscriptionPrice = ReadDecimal(reader, "SubscriptionPrice"),
            OverChargePrice = ReadDecimal(reader, "OverChargePrice"),
            TotalTopUpGb = ReadDecimal(reader, "TotalTopUpGb"),
            Status = reader["Status"]?.ToString() ?? string.Empty,
            StartDate = ReadDate(reader, "StartDate") ?? DateTime.MinValue,
            EndDate = ReadDate(reader, "EndDate") ?? DateTime.MinValue,
            NextBillingDate = ReadDate(reader, "NextBillingDate"),
            TotalInvoiceAmount = ReadDecimal(reader, "TotalInvoiceAmount"),
            TotalPaid = ReadDecimal(reader, "TotalPaid"),
            InvoiceStatus = reader["InvoiceStatus"]?.ToString() ?? string.Empty
        };
    }

    private static int ReadInt(SqlDataReader reader, string columnName)
    {
        return reader[columnName] is int value ? value : 0;
    }

    private static decimal ReadDecimal(SqlDataReader reader, string columnName)
    {
        return reader[columnName] == DBNull.Value ? 0m : Convert.ToDecimal(reader[columnName], CultureInfo.InvariantCulture);
    }

    private static DateTime? ReadDate(SqlDataReader reader, string columnName)
    {
        return reader[columnName] is DateTime value ? value : null;
    }

    private static void AddDecimal(SqlCommand command, string name, decimal value)
    {
        command.Parameters.Add(name, SqlDbType.Decimal).Value = value;
        command.Parameters[name].Precision = 18;
        command.Parameters[name].Scale = 2;
    }

    private static string NormalizeSubscriptionStatus(string? status)
    {
        var normalized = string.IsNullOrWhiteSpace(status) ? "active" : status.Trim().ToLowerInvariant();
        return normalized switch
        {
            "active" => "active",
            "debit" => "debit",
            "paid" => "paid",
            "terminated" => "terminated",
            _ => throw new InvalidOperationException("Subscription status is invalid.")
        };
    }

    private static string NormalizeInvoiceStatus(string? status)
    {
        var normalized = string.IsNullOrWhiteSpace(status) ? "pending" : status.Trim().ToLowerInvariant();
        return normalized switch
        {
            "pending" => "pending",
            "paid" => "paid",
            "refunded" => "refunded",
            "refund" => "refunded",
            "void" => "void",
            _ => throw new InvalidOperationException("Invoice status is invalid.")
        };
    }

    private static async Task EnsureSchemaAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        const string query = """
            IF OBJECT_ID(N'[dbo].[TblMonthlySubscription]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[TblMonthlySubscription](
                    [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblMonthlySubscription] PRIMARY KEY,
                    [TenantId] int NOT NULL,
                    [DeviceId] int NOT NULL,
                    [PricingPlanId] int NOT NULL,
                    [TenantName] nvarchar(250) NULL,
                    [VesselName] nvarchar(250) NOT NULL,
                    [KitId] nvarchar(250) NULL,
                    [PlanName] nvarchar(250) NOT NULL,
                    [PlanCode] nvarchar(100) NULL,
                    [SubscriptionType] nvarchar(50) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_SubscriptionType] DEFAULT(N'SUBSCRIPTION'),
                    [UsageMonth] date NOT NULL,
                    [PurchasedDate] datetime NOT NULL CONSTRAINT [DF_TblMonthlySubscription_PurchasedDate] DEFAULT(GETDATE()),
                    [StartDate] date NOT NULL,
                    [EndDate] date NOT NULL,
                    [NextBillingDate] date NULL,
                    [DataLimitGb] decimal(18,2) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_DataLimitGb] DEFAULT(0),
                    [BasePlanPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_BasePlanPrice] DEFAULT(0),
                    [SubscriptionDays] int NOT NULL CONSTRAINT [DF_TblMonthlySubscription_SubscriptionDays] DEFAULT(0),
                    [SubscriptionPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_SubscriptionPrice] DEFAULT(0),
                    [OverChargePrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_OverChargePrice] DEFAULT(0),
                    [TotalTopUpGb] decimal(18,2) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_TotalTopUpGb] DEFAULT(0),
                    [TotalInvoiceAmount] decimal(18,2) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_TotalInvoiceAmount] DEFAULT(0),
                    [TotalPaid] decimal(18,2) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_TotalPaid] DEFAULT(0),
                    [Status] nvarchar(50) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_Status] DEFAULT(N'pending_payment'),
                    [Created_Date] datetime NULL,
                    [Created_By] nvarchar(50) NULL,
                    [Updated_Date] datetime NULL,
                    [Updated_By] nvarchar(50) NULL
                );
            END;

            IF OBJECT_ID(N'[dbo].[TblSubscriptionInvoice]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[TblSubscriptionInvoice](
                    [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblSubscriptionInvoice] PRIMARY KEY,
                    [SubscriptionId] int NOT NULL,
                    [InvoiceNumber] nvarchar(100) NOT NULL,
                    [ReceiptNumber] nvarchar(100) NULL,
                    [PoNumber] nvarchar(100) NULL,
                    [InvoiceType] nvarchar(50) NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_InvoiceType] DEFAULT(N'SUBSCRIPTION'),
                    [Description] nvarchar(500) NULL,
                    [DataGb] decimal(18,2) NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_DataGb] DEFAULT(0),
                    [BuyPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_BuyPrice] DEFAULT(0),
                    [SalePrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_SalePrice] DEFAULT(0),
                    [MarginAmount] decimal(18,2) NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_MarginAmount] DEFAULT(0),
                    [Amount] decimal(18,2) NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_Amount] DEFAULT(0),
                    [PaidAmount] decimal(18,2) NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_PaidAmount] DEFAULT(0),
                    [RefundAmount] decimal(18,2) NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_RefundAmount] DEFAULT(0),
                    [Status] nvarchar(50) NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_Status] DEFAULT(N'pending'),
                    [CreatedAt] datetime NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_CreatedAt] DEFAULT(GETDATE()),
                    [CompletedAt] datetime NULL,
                    [Created_Date] datetime NULL,
                    [Created_By] nvarchar(50) NULL,
                    [Updated_Date] datetime NULL,
                    [Updated_By] nvarchar(50) NULL
                );
            END;

            IF COL_LENGTH(N'[dbo].[TblMonthlySubscription]', N'BasePlanPrice') IS NULL
                ALTER TABLE [dbo].[TblMonthlySubscription] ADD [BasePlanPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_BasePlanPrice_Existing] DEFAULT(0);
            IF COL_LENGTH(N'[dbo].[TblMonthlySubscription]', N'SubscriptionDays') IS NULL
                ALTER TABLE [dbo].[TblMonthlySubscription] ADD [SubscriptionDays] int NOT NULL CONSTRAINT [DF_TblMonthlySubscription_SubscriptionDays_Existing] DEFAULT(0);
            IF COL_LENGTH(N'[dbo].[TblMonthlySubscription]', N'SubscriptionPrice') IS NULL
                ALTER TABLE [dbo].[TblMonthlySubscription] ADD [SubscriptionPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_SubscriptionPrice_Existing] DEFAULT(0);
            IF COL_LENGTH(N'[dbo].[TblMonthlySubscription]', N'OverChargePrice') IS NULL
                ALTER TABLE [dbo].[TblMonthlySubscription] ADD [OverChargePrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_OverChargePrice_Existing] DEFAULT(0);
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record CreateSubscriptionContext(
        string TenantName,
        string VesselName,
        string KitId,
        string PlanName,
        string PlanCode,
        decimal DataLimitGb,
        decimal ResellerPrice,
        decimal FinalPrice,
        decimal ResellerOverChargePrice,
        decimal FinalOverChargePrice);

    private sealed record SubscriptionPriceContext(decimal ResellerOverChargePrice, decimal FinalOverChargePrice);

    private sealed record EditableSubscriptionInvoice(int Id, string Status, decimal PaidAmount);
}
