using System.Data;
using Microsoft.Data.SqlClient;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public sealed class TenantCommissionPaymentService(IConfiguration configuration) : ITenantCommissionPaymentService
{
    private const string MarginSql = "COALESCE(NULLIF(i.[MarginAmount], 0), i.[SalePrice] - i.[BuyPrice])";
    private const string ValidInvoiceSql = "LOWER(COALESCE(i.[Status], N'')) NOT IN (N'void', N'cancelled', N'canceled', N'refunded')";
    private const string PaidInvoiceSql = "(LOWER(COALESCE(i.[Status], N'')) = N'paid' OR (i.[Amount] > 0 AND i.[PaidAmount] >= i.[Amount]))";
    private const string DuplicateCycleMessage = "Một hoặc nhiều Billing Cycle vừa được ghi nhận thanh toán bởi người dùng khác. Vui lòng tải lại danh sách.";
    private const string OverRemainingMessage = "Số tiền thanh toán vượt quá hoa hồng còn phải trả của Tenant.";

    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing connection string: DefaultConnection");

    private static readonly HashSet<int> PageSizes = [10, 20, 50, 100];
    private static readonly IReadOnlyDictionary<string, string> SortColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["paymentDate"] = "p.[PaymentDate]",
        ["tenant"] = "t.[TenantName]",
        ["amount"] = "p.[Amount]",
        ["createdAt"] = "p.[CreatedAt]"
    };

    public async Task<TenantCommissionPaymentIndexViewModel> GetIndexAsync(
        TenantCommissionPaymentFilterViewModel filter,
        int page,
        int pageSize,
        int? allowedTenantId = null,
        bool canCreatePayment = false,
        CancellationToken cancellationToken = default)
    {
        NormalizeFilter(filter, allowedTenantId);
        page = Math.Max(1, page);
        pageSize = PageSizes.Contains(pageSize) ? pageSize : 20;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);

        var where = BuildPaymentWhere(filter);
        var total = await CountPaymentsAsync(connection, where, filter, cancellationToken);
        var payments = await QueryPaymentsAsync(connection, where, filter, page, pageSize, cancellationToken);
        var tenants = await GetTenantOptionsAsync(connection, allowedTenantId, cancellationToken);
        var balance = await QueryBalanceAsync(connection, filter.TenantId, allowedTenantId, cancellationToken);

        return new TenantCommissionPaymentIndexViewModel
        {
            Balance = balance,
            Filter = filter,
            Payments = payments,
            Tenants = tenants,
            IsTenantScoped = allowedTenantId.HasValue,
            CanCreatePayment = canCreatePayment,
            CurrentPage = page,
            PageSize = pageSize,
            TotalItems = total
        };
    }

    public async Task<TenantCommissionBalanceViewModel> GetBalanceAsync(int? tenantId, int? allowedTenantId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);
        return await QueryBalanceAsync(connection, tenantId, allowedTenantId, cancellationToken);
    }

    public async Task<IReadOnlyList<EligibleCommissionBillingCycleViewModel>> SearchEligibleCyclesAsync(
        int tenantId,
        DateTime? dateFrom,
        DateTime? dateTo,
        string? search,
        int? allowedTenantId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureTenantAccess(tenantId, allowedTenantId);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);
        return await QueryEligibleCyclesAsync(connection, null, tenantId, dateFrom, dateTo, search, cancellationToken);
    }

    public async Task<long> CreateManualPaymentAsync(TenantCommissionManualPaymentInput input, int? createdByUserId, string createdBy, int? allowedTenantId = null, CancellationToken cancellationToken = default)
    {
        if (input.TenantId <= 0) throw new InvalidOperationException("Tenant is required.");
        EnsureTenantAccess(input.TenantId, allowedTenantId);
        if (!input.PaymentDate.HasValue) throw new InvalidOperationException("Payment date is required.");
        if (!input.PeriodFrom.HasValue || !input.PeriodTo.HasValue) throw new InvalidOperationException("Payment period is required.");
        if (input.PeriodFrom.Value.Date > input.PeriodTo.Value.Date) throw new InvalidOperationException("Period From must be before Period To.");
        if (input.Amount <= 0) throw new InvalidOperationException("Amount must be greater than zero.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var balance = await QueryBalanceAsync(connection, input.TenantId, allowedTenantId, cancellationToken, transaction);
        if (input.Amount > balance.RemainingCommission)
        {
            throw new InvalidOperationException(OverRemainingMessage);
        }

        var paymentId = await InsertPaymentAsync(
            connection,
            transaction,
            input.TenantId,
            input.PaymentDate.Value.Date,
            input.PeriodFrom.Value.Date,
            input.PeriodTo.Value.Date,
            input.Amount,
            TenantCommissionPaymentSourceModes.Manual,
            input.Note,
            createdByUserId,
            createdBy,
            cancellationToken);

        await InsertAuditAsync(
            connection,
            transaction,
            createdByUserId,
            $"Created tenant commission payment #{paymentId} for tenant {input.TenantId}. Mode=manual. Period={input.PeriodFrom:yyyy-MM-dd}..{input.PeriodTo:yyyy-MM-dd}. Amount={input.Amount:0.##}.",
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return paymentId;
    }

    public async Task<long> CreateBillingCyclePaymentAsync(TenantCommissionCyclePaymentInput input, int? createdByUserId, string createdBy, int? allowedTenantId = null, CancellationToken cancellationToken = default)
    {
        if (input.TenantId <= 0) throw new InvalidOperationException("Tenant is required.");
        EnsureTenantAccess(input.TenantId, allowedTenantId);
        if (!input.PaymentDate.HasValue) throw new InvalidOperationException("Payment date is required.");
        var selectedIds = input.SubscriptionIds.Distinct().Where(id => id > 0).ToArray();
        if (selectedIds.Length == 0) throw new InvalidOperationException("Select at least one Billing Cycle.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var cycles = await QueryEligibleCyclesByIdsAsync(connection, transaction, input.TenantId, selectedIds, cancellationToken);
        if (cycles.Count != selectedIds.Length)
        {
            throw new InvalidOperationException("Một hoặc nhiều Billing Cycle không hợp lệ, chưa thanh toán, không thuộc Tenant, hoặc đã được ghi nhận thanh toán.");
        }

        var amount = cycles.Sum(item => item.CommissionAmount);
        if (amount <= 0) throw new InvalidOperationException("Billing Cycle không phát sinh hoa hồng hợp lệ.");

        var balance = await QueryBalanceAsync(connection, input.TenantId, allowedTenantId, cancellationToken, transaction);
        if (amount > balance.RemainingCommission)
        {
            throw new InvalidOperationException(OverRemainingMessage);
        }

        var periodFrom = cycles.Min(item => item.StartDate).Date;
        var periodTo = cycles.Max(item => item.EndDate).Date;
        var paymentId = await InsertPaymentAsync(
            connection,
            transaction,
            input.TenantId,
            input.PaymentDate.Value.Date,
            periodFrom,
            periodTo,
            amount,
            TenantCommissionPaymentSourceModes.BillingCycles,
            input.Note,
            createdByUserId,
            createdBy,
            cancellationToken);

        try
        {
            foreach (var cycle in cycles)
            {
                await InsertPaymentItemAsync(connection, transaction, paymentId, cycle.SubscriptionId, cycle.CommissionAmount, cancellationToken);
            }
        }
        catch (SqlException exception) when (IsDuplicateKey(exception))
        {
            throw new InvalidOperationException(DuplicateCycleMessage, exception);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            createdByUserId,
            $"Created tenant commission payment #{paymentId} for tenant {input.TenantId}. Mode=billing_cycles. Cycles={cycles.Count}. Amount={amount:0.##}. PaymentDate={input.PaymentDate:yyyy-MM-dd}.",
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return paymentId;
    }

    public async Task<TenantCommissionPaymentDetailViewModel?> GetDetailAsync(long id, int? allowedTenantId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);

        const string headerSql = """
            SELECT p.[ID], p.[TenantId], t.[TenantName], p.[PaymentDate], p.[PeriodFrom], p.[PeriodTo], p.[Amount],
                   p.[SourceMode], p.[Note], p.[CreatedByUserId], p.[CreatedBy], p.[CreatedAt]
            FROM [dbo].[TblTenantCommissionPayment] p
            INNER JOIN [dbo].[TblTenant] t ON t.[ID] = p.[TenantId]
            WHERE p.[ID] = @id
              AND (@allowedTenantId IS NULL OR p.[TenantId] = @allowedTenantId);
            """;

        await using var command = new SqlCommand(headerSql, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        command.Parameters.Add("@allowedTenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var model = new TenantCommissionPaymentDetailViewModel
        {
            Id = ReadLong(reader, "ID"),
            TenantId = ReadInt(reader, "TenantId"),
            TenantName = ReadText(reader, "TenantName"),
            PaymentDate = ReadDate(reader, "PaymentDate") ?? DateTime.MinValue,
            PeriodFrom = ReadDate(reader, "PeriodFrom"),
            PeriodTo = ReadDate(reader, "PeriodTo"),
            Amount = ReadDecimal(reader, "Amount"),
            SourceMode = ReadText(reader, "SourceMode"),
            Note = ReadText(reader, "Note"),
            CreatedByUserId = ReadNullableInt(reader, "CreatedByUserId"),
            CreatedBy = ReadText(reader, "CreatedBy"),
            CreatedAt = ReadDate(reader, "CreatedAt") ?? DateTime.MinValue
        };
        await reader.CloseAsync();

        model.Items = await QueryPaymentItemsAsync(connection, id, cancellationToken);
        return model;
    }

    private static async Task<TenantCommissionBalanceViewModel> QueryBalanceAsync(SqlConnection connection, int? tenantId, int? allowedTenantId, CancellationToken cancellationToken, SqlTransaction? transaction = null)
    {
        if (allowedTenantId.HasValue)
        {
            tenantId = allowedTenantId;
        }

        const string sql = """
            SELECT
                COALESCE(g.[GrossCommission], 0) AS [GrossCommission],
                COALESCE(p.[PaidCommission], 0) AS [PaidCommission],
                COALESCE(p.[PaymentCount], 0) AS [PaymentCount]
            FROM (SELECT 1 AS [Anchor]) anchor
            OUTER APPLY (
                SELECT SUM(COALESCE(NULLIF(i.[MarginAmount], 0), i.[SalePrice] - i.[BuyPrice])) AS [GrossCommission]
                FROM [dbo].[TblSubscriptionInvoice] i
                INNER JOIN [dbo].[TblMonthlySubscription] s ON s.[ID] = i.[SubscriptionId]
                WHERE (@tenantId IS NULL OR s.[TenantId] = @tenantId)
                  AND LOWER(COALESCE(i.[Status], N'')) NOT IN (N'void', N'cancelled', N'canceled', N'refunded')
                  AND (LOWER(COALESCE(i.[Status], N'')) = N'paid' OR (i.[Amount] > 0 AND i.[PaidAmount] >= i.[Amount]))
            ) g
            OUTER APPLY (
                SELECT SUM([Amount]) AS [PaidCommission], COUNT(1) AS [PaymentCount]
                FROM [dbo].[TblTenantCommissionPayment]
                WHERE (@tenantId IS NULL OR [TenantId] = @tenantId)
            ) p;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new TenantCommissionBalanceViewModel();
        }

        return new TenantCommissionBalanceViewModel
        {
            GrossCommission = ReadDecimal(reader, "GrossCommission"),
            PaidCommission = ReadDecimal(reader, "PaidCommission"),
            PaymentCount = ReadInt(reader, "PaymentCount")
        };
    }

    private static async Task<List<EligibleCommissionBillingCycleViewModel>> QueryEligibleCyclesAsync(SqlConnection connection, SqlTransaction? transaction, int tenantId, DateTime? dateFrom, DateTime? dateTo, string? search, CancellationToken cancellationToken)
    {
        var sql = EligibleCyclesSql(extraWhere: string.Empty, limit: 200);
        await using var command = new SqlCommand(sql, connection, transaction);
        AddEligibleParameters(command, tenantId, dateFrom, dateTo, search);
        return await ReadEligibleCyclesAsync(command, cancellationToken);
    }

    private static async Task<List<EligibleCommissionBillingCycleViewModel>> QueryEligibleCyclesByIdsAsync(SqlConnection connection, SqlTransaction transaction, int tenantId, IReadOnlyList<int> subscriptionIds, CancellationToken cancellationToken)
    {
        var parameters = subscriptionIds.Select((_, index) => $"@sid{index}").ToArray();
        var sql = EligibleCyclesSql($"AND s.[ID] IN ({string.Join(",", parameters)})", limit: null);
        await using var command = new SqlCommand(sql, connection, transaction);
        AddEligibleParameters(command, tenantId, null, null, null);
        for (var index = 0; index < subscriptionIds.Count; index++)
        {
            command.Parameters.Add(parameters[index], SqlDbType.Int).Value = subscriptionIds[index];
        }

        return await ReadEligibleCyclesAsync(command, cancellationToken);
    }

    private static string EligibleCyclesSql(string extraWhere, int? limit)
    {
        var topClause = limit.HasValue ? $"TOP ({limit.Value}) " : string.Empty;
        return $$"""
            SELECT {{topClause}}
                s.[ID] AS [SubscriptionId],
                s.[UsageMonth],
                s.[StartDate],
                s.[EndDate],
                COALESCE(NULLIF(s.[VesselName], N''), NULLIF(d.[VesselName], N''), N'') AS [VesselName],
                COALESCE(NULLIF(d.[DeviceName], N''), NULLIF(d.[DeviceCode], N''), N'') AS [DeviceName],
                COALESCE(NULLIF(d.[KITNumber], N''), NULLIF(s.[KitId], N''), NULLIF(d.[KITID], N''), N'') AS [KitId],
                s.[PlanName],
                COUNT(i.[ID]) AS [InvoiceCount],
                SUM(COALESCE(NULLIF(i.[MarginAmount], 0), i.[SalePrice] - i.[BuyPrice])) AS [CommissionAmount]
            FROM [dbo].[TblMonthlySubscription] s
            INNER JOIN [dbo].[TblSubscriptionInvoice] i ON i.[SubscriptionId] = s.[ID]
            LEFT JOIN [dbo].[TblDevices] d ON d.[ID] = s.[DeviceId]
            WHERE s.[TenantId] = @tenantId
              AND NOT EXISTS (
                    SELECT 1
                    FROM [dbo].[TblTenantCommissionPaymentItem] pi WITH (UPDLOCK, HOLDLOCK)
                    WHERE pi.[SubscriptionId] = s.[ID]
              )
              AND LOWER(COALESCE(i.[Status], N'')) NOT IN (N'void', N'cancelled', N'canceled', N'refunded')
              AND (@dateFrom IS NULL OR s.[StartDate] >= @dateFrom OR s.[EndDate] >= @dateFrom)
              AND (@dateTo IS NULL OR s.[StartDate] <= @dateTo OR s.[EndDate] <= @dateTo)
              AND (
                    @search IS NULL
                    OR CONVERT(nvarchar(30), s.[ID]) LIKE @search
                    OR FORMAT(s.[UsageMonth], N'MM/yyyy') LIKE @search
                    OR i.[InvoiceNumber] LIKE @search
                    OR i.[ReceiptNumber] LIKE @search
                    OR s.[VesselName] LIKE @search
                    OR d.[DeviceName] LIKE @search
                    OR d.[DeviceCode] LIKE @search
                    OR d.[KITNumber] LIKE @search
                    OR s.[KitId] LIKE @search
                    OR d.[KITID] LIKE @search
                    OR s.[PlanName] LIKE @search
                    OR EXISTS (
                        SELECT 1
                        FROM [dbo].[TblPaymentTransaction] pt
                        WHERE (pt.[SubscriptionId] = s.[ID] OR pt.[InvoiceId] = i.[ID] OR pt.[InvoiceNumber] = i.[InvoiceNumber])
                          AND (CONVERT(nvarchar(30), pt.[ID]) LIKE @search
                               OR pt.[InvoiceNumber] LIKE @search
                               OR pt.[ProviderPaymentNo] LIKE @search
                               OR pt.[ProviderStatus] LIKE @search
                               OR pt.[Method] LIKE @search)
                    )
                    OR EXISTS (
                        SELECT 1
                        FROM [dbo].[TblNinePayQrSession] qs
                        WHERE (qs.[SubscriptionId] = s.[ID]
                               OR qs.[InvoiceId] = i.[ID]
                               OR qs.[InvoiceNumber] = i.[InvoiceNumber]
                               OR EXISTS (SELECT 1 FROM [dbo].[TblNinePayQrSessionInvoice] qi WHERE qi.[QrSessionId] = qs.[ID] AND qi.[InvoiceId] = i.[ID]))
                          AND (CONVERT(nvarchar(30), qs.[ID]) LIKE @search
                               OR qs.[InvoiceNumber] LIKE @search
                               OR qs.[ProviderInvoiceNo] LIKE @search
                               OR qs.[ProviderPaymentNo] LIKE @search
                               OR qs.[BankAccountNo] LIKE @search
                               OR qs.[TransferContent] LIKE @search)
                    )
                  )
              {{extraWhere}}
            GROUP BY s.[ID], s.[UsageMonth], s.[StartDate], s.[EndDate], s.[VesselName], d.[VesselName], d.[DeviceName], d.[DeviceCode], d.[KITNumber], s.[KitId], d.[KITID], s.[PlanName]
            HAVING COUNT(i.[ID]) > 0
               AND SUM(CASE WHEN (LOWER(COALESCE(i.[Status], N'')) = N'paid' OR (i.[Amount] > 0 AND i.[PaidAmount] >= i.[Amount])) THEN 0 ELSE 1 END) = 0
               AND SUM(COALESCE(NULLIF(i.[MarginAmount], 0), i.[SalePrice] - i.[BuyPrice])) > 0
            ORDER BY s.[UsageMonth] DESC, s.[ID] DESC;
            """;
    }

    private static async Task<long> InsertPaymentAsync(SqlConnection connection, SqlTransaction transaction, int tenantId, DateTime paymentDate, DateTime? periodFrom, DateTime? periodTo, decimal amount, string sourceMode, string? note, int? createdByUserId, string createdBy, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [dbo].[TblTenantCommissionPayment]
                ([TenantId], [PaymentDate], [PeriodFrom], [PeriodTo], [Amount], [SourceMode], [Note], [CreatedAt], [CreatedByUserId], [CreatedBy])
            OUTPUT INSERTED.[ID]
            VALUES
                (@tenantId, @paymentDate, @periodFrom, @periodTo, @amount, @sourceMode, @note, SYSUTCDATETIME(), @createdByUserId, @createdBy);
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@paymentDate", SqlDbType.Date).Value = paymentDate;
        command.Parameters.Add("@periodFrom", SqlDbType.Date).Value = (object?)periodFrom ?? DBNull.Value;
        command.Parameters.Add("@periodTo", SqlDbType.Date).Value = (object?)periodTo ?? DBNull.Value;
        AddDecimal(command, "@amount", amount);
        command.Parameters.Add("@sourceMode", SqlDbType.NVarChar, 30).Value = sourceMode;
        command.Parameters.Add("@note", SqlDbType.NVarChar, 1000).Value = EmptyToDbNull(note);
        command.Parameters.Add("@createdByUserId", SqlDbType.Int).Value = (object?)createdByUserId ?? DBNull.Value;
        command.Parameters.Add("@createdBy", SqlDbType.NVarChar, 250).Value = EmptyToDbNull(createdBy);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task InsertPaymentItemAsync(SqlConnection connection, SqlTransaction transaction, long paymentId, int subscriptionId, decimal commissionAmount, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [dbo].[TblTenantCommissionPaymentItem]
                ([PaymentId], [SubscriptionId], [CommissionAmount], [CreatedAt])
            VALUES
                (@paymentId, @subscriptionId, @commissionAmount, SYSUTCDATETIME());
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@paymentId", SqlDbType.BigInt).Value = paymentId;
        command.Parameters.Add("@subscriptionId", SqlDbType.Int).Value = subscriptionId;
        AddDecimal(command, "@commissionAmount", commissionAmount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAuditAsync(SqlConnection connection, SqlTransaction transaction, int? userId, string detail, CancellationToken cancellationToken)
    {
        const string sql = """
            IF OBJECT_ID(N'[dbo].[TblAudit]', N'U') IS NOT NULL
            BEGIN
                INSERT INTO [dbo].[TblAudit] ([IDUser], [LogDate], [LogAction], [LogDetail], [IDDevice])
                VALUES (@userId, GETDATE(), N'tenant_commission_payment_created', @detail, NULL);
            END
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@userId", SqlDbType.Int).Value = (object?)userId ?? DBNull.Value;
        command.Parameters.Add("@detail", SqlDbType.NVarChar, -1).Value = detail;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureSchemaAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            IF OBJECT_ID(N'[dbo].[TblTenantCommissionPayment]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[TblTenantCommissionPayment](
                    [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblTenantCommissionPayment] PRIMARY KEY,
                    [TenantId] int NOT NULL,
                    [PaymentDate] date NOT NULL,
                    [PeriodFrom] date NULL,
                    [PeriodTo] date NULL,
                    [Amount] decimal(18,2) NOT NULL,
                    [SourceMode] nvarchar(30) NOT NULL,
                    [Note] nvarchar(1000) NULL,
                    [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_TblTenantCommissionPayment_CreatedAt] DEFAULT(SYSUTCDATETIME()),
                    [CreatedByUserId] int NULL,
                    [CreatedBy] nvarchar(250) NULL,
                    CONSTRAINT [CK_TblTenantCommissionPayment_Amount] CHECK ([Amount] > 0),
                    CONSTRAINT [CK_TblTenantCommissionPayment_Period] CHECK ([PeriodFrom] IS NULL OR [PeriodTo] IS NULL OR [PeriodFrom] <= [PeriodTo]),
                    CONSTRAINT [CK_TblTenantCommissionPayment_SourceMode] CHECK ([SourceMode] IN (N'manual', N'billing_cycles'))
                );
            END;
            IF OBJECT_ID(N'[dbo].[TblTenantCommissionPaymentItem]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[TblTenantCommissionPaymentItem](
                    [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblTenantCommissionPaymentItem] PRIMARY KEY,
                    [PaymentId] bigint NOT NULL,
                    [SubscriptionId] int NOT NULL,
                    [CommissionAmount] decimal(18,2) NOT NULL,
                    [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_TblTenantCommissionPaymentItem_CreatedAt] DEFAULT(SYSUTCDATETIME()),
                    CONSTRAINT [CK_TblTenantCommissionPaymentItem_CommissionAmount] CHECK ([CommissionAmount] > 0)
                );
            END;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TenantCommissionPaymentItem_SubscriptionId' AND [object_id] = OBJECT_ID(N'[dbo].[TblTenantCommissionPaymentItem]'))
                CREATE UNIQUE INDEX [UX_TenantCommissionPaymentItem_SubscriptionId] ON [dbo].[TblTenantCommissionPaymentItem]([SubscriptionId]);
            IF OBJECT_ID(N'[dbo].[TblTenant]', N'U') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = N'FK_TenantCommissionPayment_Tenant')
                ALTER TABLE [dbo].[TblTenantCommissionPayment] WITH NOCHECK ADD CONSTRAINT [FK_TenantCommissionPayment_Tenant] FOREIGN KEY ([TenantId]) REFERENCES [dbo].[TblTenant]([ID]);
            IF OBJECT_ID(N'[dbo].[TblMonthlySubscription]', N'U') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = N'FK_TenantCommissionPaymentItem_Subscription')
                ALTER TABLE [dbo].[TblTenantCommissionPaymentItem] WITH NOCHECK ADD CONSTRAINT [FK_TenantCommissionPaymentItem_Subscription] FOREIGN KEY ([SubscriptionId]) REFERENCES [dbo].[TblMonthlySubscription]([ID]);
            IF EXISTS (
                SELECT 1
                FROM sys.foreign_keys
                WHERE [name] = N'FK_TenantCommissionPaymentItem_Payment'
                  AND [parent_object_id] = OBJECT_ID(N'[dbo].[TblTenantCommissionPaymentItem]')
                  AND [delete_referential_action_desc] = N'CASCADE'
            )
                ALTER TABLE [dbo].[TblTenantCommissionPaymentItem] DROP CONSTRAINT [FK_TenantCommissionPaymentItem_Payment];
            IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = N'FK_TenantCommissionPaymentItem_Payment')
                ALTER TABLE [dbo].[TblTenantCommissionPaymentItem] WITH NOCHECK ADD CONSTRAINT [FK_TenantCommissionPaymentItem_Payment] FOREIGN KEY ([PaymentId]) REFERENCES [dbo].[TblTenantCommissionPayment]([ID]);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TenantCommissionPayment_Tenant_PaymentDate' AND [object_id] = OBJECT_ID(N'[dbo].[TblTenantCommissionPayment]'))
                CREATE INDEX [IX_TenantCommissionPayment_Tenant_PaymentDate] ON [dbo].[TblTenantCommissionPayment]([TenantId], [PaymentDate] DESC, [ID] DESC);
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> CountPaymentsAsync(SqlConnection connection, string where, TenantCommissionPaymentFilterViewModel filter, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand($"SELECT COUNT(1) FROM [dbo].[TblTenantCommissionPayment] p INNER JOIN [dbo].[TblTenant] t ON t.[ID] = p.[TenantId] WHERE {where};", connection);
        AddPaymentFilterParameters(command, filter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<List<TenantCommissionPaymentListItemViewModel>> QueryPaymentsAsync(SqlConnection connection, string where, TenantCommissionPaymentFilterViewModel filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var sortColumn = SortColumns.GetValueOrDefault(filter.SortBy, SortColumns["paymentDate"]);
        var sortDirection = string.Equals(filter.SortDirection, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        var sql = $"""
            SELECT p.[ID], p.[TenantId], t.[TenantName], p.[PaymentDate], p.[PeriodFrom], p.[PeriodTo], p.[Amount],
                   p.[SourceMode], p.[Note], p.[CreatedBy], p.[CreatedAt],
                   COUNT(pi.[ID]) AS [BillingCycleCount]
            FROM [dbo].[TblTenantCommissionPayment] p
            INNER JOIN [dbo].[TblTenant] t ON t.[ID] = p.[TenantId]
            LEFT JOIN [dbo].[TblTenantCommissionPaymentItem] pi ON pi.[PaymentId] = p.[ID]
            WHERE {where}
            GROUP BY p.[ID], p.[TenantId], t.[TenantName], p.[PaymentDate], p.[PeriodFrom], p.[PeriodTo], p.[Amount], p.[SourceMode], p.[Note], p.[CreatedBy], p.[CreatedAt]
            ORDER BY {sortColumn} {sortDirection}, p.[ID] DESC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;
            """;
        await using var command = new SqlCommand(sql, connection);
        AddPaymentFilterParameters(command, filter);
        command.Parameters.Add("@offset", SqlDbType.Int).Value = (page - 1) * pageSize;
        command.Parameters.Add("@pageSize", SqlDbType.Int).Value = pageSize;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var payments = new List<TenantCommissionPaymentListItemViewModel>();
        while (await reader.ReadAsync(cancellationToken))
        {
            payments.Add(new TenantCommissionPaymentListItemViewModel
            {
                Id = ReadLong(reader, "ID"),
                TenantId = ReadInt(reader, "TenantId"),
                TenantName = ReadText(reader, "TenantName"),
                PaymentDate = ReadDate(reader, "PaymentDate") ?? DateTime.MinValue,
                PeriodFrom = ReadDate(reader, "PeriodFrom"),
                PeriodTo = ReadDate(reader, "PeriodTo"),
                Amount = ReadDecimal(reader, "Amount"),
                SourceMode = ReadText(reader, "SourceMode"),
                BillingCycleCount = ReadInt(reader, "BillingCycleCount"),
                Note = ReadText(reader, "Note"),
                CreatedBy = ReadText(reader, "CreatedBy"),
                CreatedAt = ReadDate(reader, "CreatedAt") ?? DateTime.MinValue
            });
        }

        return payments;
    }

    private static async Task<List<TenantCommissionPaymentItemViewModel>> QueryPaymentItemsAsync(SqlConnection connection, long paymentId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT pi.[ID], pi.[SubscriptionId], s.[UsageMonth], s.[StartDate], s.[EndDate],
                   COALESCE(NULLIF(s.[VesselName], N''), NULLIF(d.[VesselName], N''), N'') AS [VesselName],
                   COALESCE(NULLIF(d.[DeviceName], N''), NULLIF(d.[DeviceCode], N''), N'') AS [DeviceName],
                   COALESCE(NULLIF(d.[KITNumber], N''), NULLIF(s.[KitId], N''), NULLIF(d.[KITID], N''), N'') AS [KitId],
                   s.[PlanName],
                   COALESCE(inv.[InvoiceNumbers], N'') AS [InvoiceNumbers],
                   COALESCE(tx.[TransactionReferences], N'') AS [TransactionReferences],
                   pi.[CommissionAmount]
            FROM [dbo].[TblTenantCommissionPaymentItem] pi
            INNER JOIN [dbo].[TblMonthlySubscription] s ON s.[ID] = pi.[SubscriptionId]
            LEFT JOIN [dbo].[TblDevices] d ON d.[ID] = s.[DeviceId]
            OUTER APPLY (
                SELECT STUFF((
                    SELECT DISTINCT N', ' + i2.[InvoiceNumber]
                    FROM [dbo].[TblSubscriptionInvoice] i2
                    WHERE i2.[SubscriptionId] = s.[ID]
                    FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N'') AS [InvoiceNumbers]
            ) inv
            OUTER APPLY (
                SELECT STUFF((
                    SELECT DISTINCT N', ' + refs.[Reference]
                    FROM (
                        SELECT NULLIF(pt.[ProviderPaymentNo], N'') AS [Reference]
                        FROM [dbo].[TblPaymentTransaction] pt
                        WHERE pt.[SubscriptionId] = s.[ID]
                           OR pt.[InvoiceId] IN (SELECT i3.[ID] FROM [dbo].[TblSubscriptionInvoice] i3 WHERE i3.[SubscriptionId] = s.[ID])
                           OR pt.[InvoiceNumber] IN (SELECT i4.[InvoiceNumber] FROM [dbo].[TblSubscriptionInvoice] i4 WHERE i4.[SubscriptionId] = s.[ID])
                        UNION
                        SELECT NULLIF(qs.[ProviderPaymentNo], N'')
                        FROM [dbo].[TblNinePayQrSession] qs
                        WHERE qs.[SubscriptionId] = s.[ID]
                           OR qs.[InvoiceId] IN (SELECT i5.[ID] FROM [dbo].[TblSubscriptionInvoice] i5 WHERE i5.[SubscriptionId] = s.[ID])
                           OR qs.[InvoiceNumber] IN (SELECT i6.[InvoiceNumber] FROM [dbo].[TblSubscriptionInvoice] i6 WHERE i6.[SubscriptionId] = s.[ID])
                           OR EXISTS (
                                SELECT 1
                                FROM [dbo].[TblNinePayQrSessionInvoice] qi
                                WHERE qi.[QrSessionId] = qs.[ID]
                                  AND qi.[SubscriptionId] = s.[ID]
                           )
                        UNION
                        SELECT NULLIF(qs.[ProviderInvoiceNo], N'')
                        FROM [dbo].[TblNinePayQrSession] qs
                        WHERE qs.[SubscriptionId] = s.[ID]
                           OR qs.[InvoiceId] IN (SELECT i7.[ID] FROM [dbo].[TblSubscriptionInvoice] i7 WHERE i7.[SubscriptionId] = s.[ID])
                           OR qs.[InvoiceNumber] IN (SELECT i8.[InvoiceNumber] FROM [dbo].[TblSubscriptionInvoice] i8 WHERE i8.[SubscriptionId] = s.[ID])
                    ) refs
                    WHERE refs.[Reference] IS NOT NULL
                    FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N'') AS [TransactionReferences]
            ) tx
            WHERE pi.[PaymentId] = @paymentId
            ORDER BY s.[UsageMonth], s.[ID];
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@paymentId", SqlDbType.BigInt).Value = paymentId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<TenantCommissionPaymentItemViewModel>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var usageMonth = ReadDate(reader, "UsageMonth") ?? DateTime.MinValue;
            items.Add(new TenantCommissionPaymentItemViewModel
            {
                Id = ReadLong(reader, "ID"),
                SubscriptionId = ReadInt(reader, "SubscriptionId"),
                BillingCycle = usageMonth == DateTime.MinValue ? "-" : usageMonth.ToString("MM/yyyy"),
                UsageMonth = usageMonth,
                StartDate = ReadDate(reader, "StartDate") ?? DateTime.MinValue,
                EndDate = ReadDate(reader, "EndDate") ?? DateTime.MinValue,
                VesselName = ReadText(reader, "VesselName"),
                DeviceName = ReadText(reader, "DeviceName"),
                KitId = ReadText(reader, "KitId"),
                PlanName = ReadText(reader, "PlanName"),
                InvoiceNumbers = ReadText(reader, "InvoiceNumbers"),
                TransactionReferences = ReadText(reader, "TransactionReferences"),
                CommissionAmount = ReadDecimal(reader, "CommissionAmount")
            });
        }

        return items;
    }

    private static async Task<List<DeviceTenantOptionViewModel>> GetTenantOptionsAsync(SqlConnection connection, int? allowedTenantId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT [ID], [TenantName]
            FROM [dbo].[TblTenant]
            WHERE (@allowedTenantId IS NULL OR [ID] = @allowedTenantId)
            ORDER BY [TenantName], [ID];
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@allowedTenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tenants = new List<DeviceTenantOptionViewModel>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tenants.Add(new DeviceTenantOptionViewModel
            {
                Id = ReadInt(reader, "ID"),
                TenantName = ReadText(reader, "TenantName")
            });
        }

        return tenants;
    }

    private static async Task<List<EligibleCommissionBillingCycleViewModel>> ReadEligibleCyclesAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var cycles = new List<EligibleCommissionBillingCycleViewModel>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var usageMonth = ReadDate(reader, "UsageMonth") ?? DateTime.MinValue;
            cycles.Add(new EligibleCommissionBillingCycleViewModel
            {
                SubscriptionId = ReadInt(reader, "SubscriptionId"),
                BillingCycle = usageMonth == DateTime.MinValue ? "-" : usageMonth.ToString("MM/yyyy"),
                UsageMonth = usageMonth,
                StartDate = ReadDate(reader, "StartDate") ?? DateTime.MinValue,
                EndDate = ReadDate(reader, "EndDate") ?? DateTime.MinValue,
                VesselName = ReadText(reader, "VesselName"),
                DeviceName = ReadText(reader, "DeviceName"),
                KitId = ReadText(reader, "KitId"),
                PlanName = ReadText(reader, "PlanName"),
                InvoiceCount = ReadInt(reader, "InvoiceCount"),
                CommissionAmount = ReadDecimal(reader, "CommissionAmount")
            });
        }

        return cycles;
    }

    private static string BuildPaymentWhere(TenantCommissionPaymentFilterViewModel filter)
    {
        var clauses = new List<string>
        {
            "(@allowedTenantId IS NULL OR p.[TenantId] = @allowedTenantId)",
            "(@tenantId IS NULL OR p.[TenantId] = @tenantId)",
            "(@paymentDateFrom IS NULL OR p.[PaymentDate] >= @paymentDateFrom)",
            "(@paymentDateTo IS NULL OR p.[PaymentDate] <= @paymentDateTo)",
            "(@periodFrom IS NULL OR p.[PeriodTo] IS NULL OR p.[PeriodTo] >= @periodFrom)",
            "(@periodTo IS NULL OR p.[PeriodFrom] IS NULL OR p.[PeriodFrom] <= @periodTo)",
            "(@sourceMode IS NULL OR p.[SourceMode] = @sourceMode)"
        };

        if (!string.IsNullOrWhiteSpace(filter.Keyword))
        {
            clauses.Add("""
                (
                    t.[TenantName] LIKE @keyword
                    OR CONVERT(nvarchar(30), t.[ID]) LIKE @keyword
                    OR p.[Note] LIKE @keyword
                    OR p.[CreatedBy] LIKE @keyword
                    OR p.[SourceMode] LIKE @keyword
                    OR CONVERT(nvarchar(30), p.[ID]) LIKE @keyword
                    OR CONVERT(nvarchar(30), p.[Amount]) LIKE @keyword
                    OR CONVERT(nvarchar(10), p.[PaymentDate], 120) LIKE @keyword
                    OR CONVERT(nvarchar(10), p.[PeriodFrom], 120) LIKE @keyword
                    OR CONVERT(nvarchar(10), p.[PeriodTo], 120) LIKE @keyword
                    OR EXISTS (
                        SELECT 1
                        FROM [dbo].[TblTenantCommissionPaymentItem] pi
                        INNER JOIN [dbo].[TblMonthlySubscription] s ON s.[ID] = pi.[SubscriptionId]
                        LEFT JOIN [dbo].[TblDevices] d ON d.[ID] = s.[DeviceId]
                        LEFT JOIN [dbo].[TblSubscriptionInvoice] i ON i.[SubscriptionId] = s.[ID]
                        WHERE pi.[PaymentId] = p.[ID]
                          AND (
                            CONVERT(nvarchar(30), s.[ID]) LIKE @keyword
                            OR FORMAT(s.[UsageMonth], N'MM/yyyy') LIKE @keyword
                            OR CONVERT(nvarchar(10), s.[StartDate], 120) LIKE @keyword
                            OR CONVERT(nvarchar(10), s.[EndDate], 120) LIKE @keyword
                            OR s.[PlanName] LIKE @keyword
                            OR s.[VesselName] LIKE @keyword
                            OR s.[KitId] LIKE @keyword
                            OR d.[VesselName] LIKE @keyword
                            OR d.[DeviceName] LIKE @keyword
                            OR d.[DeviceCode] LIKE @keyword
                            OR d.[KITNumber] LIKE @keyword
                            OR d.[KITID] LIKE @keyword
                            OR CONVERT(nvarchar(30), i.[ID]) LIKE @keyword
                            OR i.[InvoiceNumber] LIKE @keyword
                            OR i.[ReceiptNumber] LIKE @keyword
                            OR EXISTS (
                                SELECT 1
                                FROM [dbo].[TblPaymentTransaction] pt
                                WHERE (pt.[SubscriptionId] = s.[ID] OR pt.[InvoiceId] = i.[ID] OR pt.[InvoiceNumber] = i.[InvoiceNumber])
                                  AND (CONVERT(nvarchar(30), pt.[ID]) LIKE @keyword
                                       OR pt.[InvoiceNumber] LIKE @keyword
                                       OR pt.[ProviderPaymentNo] LIKE @keyword
                                       OR pt.[ProviderStatus] LIKE @keyword
                                       OR pt.[Method] LIKE @keyword)
                            )
                            OR EXISTS (
                                SELECT 1
                                FROM [dbo].[TblNinePayQrSession] qs
                                WHERE (qs.[SubscriptionId] = s.[ID]
                                       OR qs.[InvoiceId] = i.[ID]
                                       OR qs.[InvoiceNumber] = i.[InvoiceNumber]
                                       OR EXISTS (SELECT 1 FROM [dbo].[TblNinePayQrSessionInvoice] qi WHERE qi.[QrSessionId] = qs.[ID] AND qi.[InvoiceId] = i.[ID]))
                                  AND (CONVERT(nvarchar(30), qs.[ID]) LIKE @keyword
                                       OR qs.[InvoiceNumber] LIKE @keyword
                                       OR qs.[ProviderInvoiceNo] LIKE @keyword
                                       OR qs.[ProviderPaymentNo] LIKE @keyword
                                       OR qs.[BankAccountNo] LIKE @keyword
                                       OR qs.[TransferContent] LIKE @keyword)
                            )
                          )
                    )
                )
                """);
        }

        return string.Join(" AND ", clauses);
    }

    private static void AddPaymentFilterParameters(SqlCommand command, TenantCommissionPaymentFilterViewModel filter)
    {
        command.Parameters.Add("@allowedTenantId", SqlDbType.Int).Value = (object?)filter.TenantIdScope ?? DBNull.Value;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)filter.TenantId ?? DBNull.Value;
        command.Parameters.Add("@paymentDateFrom", SqlDbType.Date).Value = (object?)filter.PaymentDateFrom?.Date ?? DBNull.Value;
        command.Parameters.Add("@paymentDateTo", SqlDbType.Date).Value = (object?)filter.PaymentDateTo?.Date ?? DBNull.Value;
        command.Parameters.Add("@periodFrom", SqlDbType.Date).Value = (object?)filter.PeriodFrom?.Date ?? DBNull.Value;
        command.Parameters.Add("@periodTo", SqlDbType.Date).Value = (object?)filter.PeriodTo?.Date ?? DBNull.Value;
        command.Parameters.Add("@sourceMode", SqlDbType.NVarChar, 30).Value = string.IsNullOrWhiteSpace(filter.SourceMode) ? DBNull.Value : filter.SourceMode;
        if (!string.IsNullOrWhiteSpace(filter.Keyword))
        {
            command.Parameters.Add("@keyword", SqlDbType.NVarChar, 260).Value = $"%{filter.Keyword.Trim()}%";
        }
    }

    private static void AddEligibleParameters(SqlCommand command, int tenantId, DateTime? dateFrom, DateTime? dateTo, string? search)
    {
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@dateFrom", SqlDbType.Date).Value = (object?)dateFrom?.Date ?? DBNull.Value;
        command.Parameters.Add("@dateTo", SqlDbType.Date).Value = (object?)dateTo?.Date ?? DBNull.Value;
        command.Parameters.Add("@search", SqlDbType.NVarChar, 260).Value = string.IsNullOrWhiteSpace(search) ? DBNull.Value : $"%{search.Trim()}%";
    }

    private static void NormalizeFilter(TenantCommissionPaymentFilterViewModel filter, int? allowedTenantId)
    {
        filter.TenantIdScope = allowedTenantId;
        if (allowedTenantId.HasValue)
        {
            filter.TenantId = allowedTenantId.Value;
        }

        if (!string.Equals(filter.SourceMode, TenantCommissionPaymentSourceModes.Manual, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(filter.SourceMode, TenantCommissionPaymentSourceModes.BillingCycles, StringComparison.OrdinalIgnoreCase))
        {
            filter.SourceMode = null;
        }

        filter.SortBy = SortColumns.ContainsKey(filter.SortBy) ? filter.SortBy : "paymentDate";
        filter.SortDirection = string.Equals(filter.SortDirection, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
    }

    private static void EnsureTenantAccess(int tenantId, int? allowedTenantId)
    {
        if (allowedTenantId.HasValue && tenantId != allowedTenantId.Value)
        {
            throw new UnauthorizedAccessException("Tenant scope mismatch.");
        }
    }

    private static void AddDecimal(SqlCommand command, string name, decimal value)
    {
        command.Parameters.Add(name, SqlDbType.Decimal).Value = value;
        command.Parameters[name].Precision = 18;
        command.Parameters[name].Scale = 2;
    }

    private static object EmptyToDbNull(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    private static bool IsDuplicateKey(SqlException exception) => exception.Number is 2601 or 2627;
    private static string ReadText(SqlDataReader reader, string column) => reader[column] == DBNull.Value ? string.Empty : reader[column].ToString() ?? string.Empty;
    private static int ReadInt(SqlDataReader reader, string column) => reader[column] == DBNull.Value ? 0 : Convert.ToInt32(reader[column]);
    private static int? ReadNullableInt(SqlDataReader reader, string column) => reader[column] == DBNull.Value ? null : Convert.ToInt32(reader[column]);
    private static long ReadLong(SqlDataReader reader, string column) => reader[column] == DBNull.Value ? 0 : Convert.ToInt64(reader[column]);
    private static decimal ReadDecimal(SqlDataReader reader, string column) => reader[column] == DBNull.Value ? 0 : Convert.ToDecimal(reader[column]);
    private static DateTime? ReadDate(SqlDataReader reader, string column) => reader[column] == DBNull.Value ? null : Convert.ToDateTime(reader[column]);
}
