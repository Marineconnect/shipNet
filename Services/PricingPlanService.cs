using System.Data;
using Microsoft.Data.SqlClient;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public class PricingPlanService(IConfiguration configuration) : IPricingPlanService
{
    private const string CreateAuditAction = "created_pricing_plan";
    private const string UpdateAuditAction = "updated_pricing_plan";
    private const string DeleteAuditAction = "deleted_pricing_plan";
    private const string ImportAuditAction = "imported_pricing_plans";
    private const string CreateTenantPriceAuditAction = "created_tenant_pricing";
    private const string UpdateTenantPriceAuditAction = "updated_tenant_pricing";
    private const string DeleteTenantPriceAuditAction = "deleted_tenant_pricing";
    private const string ImportTenantPriceAuditAction = "imported_tenant_pricing";

    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing connection string: DefaultConnection");

    private bool _schemaEnsured;

    public async Task<PricingPlanPageResult> GetPlansAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        const string countQuery = "SELECT COUNT(1) FROM [TblPricingPlan]";
        const string listQuery = """
            SELECT
                [ID],
                [PlanName],
                [PlanCode],
                [ResellerPrice],
                [FinalPrice],
                [BaseData],
                [ResellerOverChargePrice],
                [FinalOverChargePrice],
                [Status],
                [Updated_Date],
                [Updated_By]
            FROM [TblPricingPlan]
            ORDER BY [ID] DESC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            """;

        var normalizedPageSize = pageSize <= 0 ? 10 : pageSize;
        var normalizedPage = page <= 0 ? 1 : page;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);

        int totalPlans;
        await using (var countCommand = new SqlCommand(countQuery, connection))
        {
            totalPlans = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        }

        var totalPages = totalPlans == 0 ? 1 : (int)Math.Ceiling(totalPlans / (double)normalizedPageSize);
        var clampedPage = Math.Min(normalizedPage, totalPages);
        var offset = (clampedPage - 1) * normalizedPageSize;

        var plans = new List<PricingPlanListItemViewModel>();
        await using (var listCommand = new SqlCommand(listQuery, connection))
        {
            listCommand.Parameters.Add("@offset", SqlDbType.Int).Value = offset;
            listCommand.Parameters.Add("@pageSize", SqlDbType.Int).Value = normalizedPageSize;

            await using var reader = await listCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                plans.Add(MapListItem(reader));
            }
        }

        return new PricingPlanPageResult
        {
            Plans = plans,
            CurrentPage = clampedPage,
            PageSize = normalizedPageSize,
            TotalPlans = totalPlans
        };
    }

    public async Task<List<PricingPlanFormViewModel>> GetPlansForExportAsync(CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT
                [ID],
                [PlanName],
                [PlanCode],
                [ResellerPrice],
                [FinalPrice],
                [BaseData],
                [ResellerOverChargePrice],
                [FinalOverChargePrice],
                [Status]
            FROM [TblPricingPlan]
            ORDER BY [ID] ASC
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);

        var plans = new List<PricingPlanFormViewModel>();
        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            plans.Add(MapForm(reader));
        }

        return plans;
    }

    public async Task<List<PricingPlanOptionViewModel>> GetPlanOptionsAsync(CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT
                [ID],
                [PlanName],
                [PlanCode],
                [ResellerPrice],
                [FinalPrice],
                [BaseData],
                [ResellerOverChargePrice],
                [FinalOverChargePrice]
            FROM [TblPricingPlan]
            WHERE LOWER([Status]) = 'active'
            ORDER BY [PlanName] ASC, [ID] ASC
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);

        var plans = new List<PricingPlanOptionViewModel>();
        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            plans.Add(new PricingPlanOptionViewModel
            {
                Id = reader["ID"] is int id ? id : 0,
                PlanName = reader["PlanName"]?.ToString() ?? string.Empty,
                PlanCode = reader["PlanCode"]?.ToString() ?? string.Empty,
                ResellerPrice = ReadDecimal(reader, "ResellerPrice"),
                FinalPrice = ReadDecimal(reader, "FinalPrice"),
                BaseData = ReadDecimal(reader, "BaseData"),
                ResellerOverChargePrice = ReadDecimal(reader, "ResellerOverChargePrice"),
                FinalOverChargePrice = ReadDecimal(reader, "FinalOverChargePrice")
            });
        }

        return plans;
    }

    public async Task<PricingPlanFormViewModel?> GetPlanByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT TOP 1
                [ID],
                [PlanName],
                [PlanCode],
                [ResellerPrice],
                [FinalPrice],
                [BaseData],
                [ResellerOverChargePrice],
                [FinalOverChargePrice],
                [Status]
            FROM [TblPricingPlan]
            WHERE [ID] = @id
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapForm(reader);
    }

    public async Task<bool> IsPlanCodeInUseAsync(string planCode, int? excludePlanId = null, CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT TOP 1 1
            FROM [TblPricingPlan]
            WHERE LOWER(LTRIM(RTRIM([PlanCode]))) = LOWER(@planCode)
              AND (@excludePlanId IS NULL OR [ID] <> @excludePlanId)
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@planCode", SqlDbType.NVarChar, 100).Value = planCode;
        command.Parameters.Add("@excludePlanId", SqlDbType.Int).Value = (object?)excludePlanId ?? DBNull.Value;

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value;
    }

    public async Task<int> CreatePlanAsync(
        PricingPlanFormViewModel model,
        int? userId,
        string username,
        CancellationToken cancellationToken = default)
    {
        const string query = """
            INSERT INTO [TblPricingPlan]
                ([PlanName], [PlanCode], [ResellerPrice], [FinalPrice], [BaseData], [ResellerOverChargePrice], [FinalOverChargePrice], [Status], [Created_Date], [Created_By], [Updated_Date], [Updated_By])
            OUTPUT INSERTED.[ID]
            VALUES
                (@planName, @planCode, @resellerPrice, @finalPrice, @baseData, @resellerOverChargePrice, @finalOverChargePrice, @status, GETDATE(), @createdBy, GETDATE(), @updatedBy)
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        await using var command = new SqlCommand(query, connection, transaction);
        AddFormParameters(command, model);
        command.Parameters.Add("@createdBy", SqlDbType.NVarChar, 50).Value = username;
        command.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 50).Value = username;

        var planId = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        await InsertAuditAsync(connection, transaction, userId, CreateAuditAction, $"Created pricing plan '{model.PlanCode}' (ID: {planId}).", cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return planId;
    }

    public async Task UpdatePlanAsync(
        PricingPlanFormViewModel model,
        int? userId,
        string username,
        CancellationToken cancellationToken = default)
    {
        const string selectQuery = """
            SELECT TOP 1
                [ID],
                [PlanName],
                [PlanCode],
                [ResellerPrice],
                [FinalPrice],
                [BaseData],
                [ResellerOverChargePrice],
                [FinalOverChargePrice],
                [Status]
            FROM [TblPricingPlan]
            WHERE [ID] = @id
            """;

        const string updateQuery = """
            UPDATE [TblPricingPlan]
            SET
                [PlanName] = @planName,
                [PlanCode] = @planCode,
                [ResellerPrice] = @resellerPrice,
                [FinalPrice] = @finalPrice,
                [BaseData] = @baseData,
                [ResellerOverChargePrice] = @resellerOverChargePrice,
                [FinalOverChargePrice] = @finalOverChargePrice,
                [Status] = @status,
                [Updated_Date] = GETDATE(),
                [Updated_By] = @updatedBy
            WHERE [ID] = @id
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        PricingPlanFormViewModel existingPlan;
        await using (var selectCommand = new SqlCommand(selectQuery, connection, transaction))
        {
            selectCommand.Parameters.Add("@id", SqlDbType.Int).Value = model.Id;
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new KeyNotFoundException($"Pricing plan with id {model.Id} was not found.");
            }

            existingPlan = MapForm(reader);
        }

        await using (var updateCommand = new SqlCommand(updateQuery, connection, transaction))
        {
            updateCommand.Parameters.Add("@id", SqlDbType.Int).Value = model.Id;
            AddFormParameters(updateCommand, model);
            updateCommand.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 50).Value = username;
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(connection, transaction, userId, UpdateAuditAction, BuildUpdateAuditDetail(existingPlan, model), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeletePlanAsync(int id, int? userId, string username, CancellationToken cancellationToken = default)
    {
        const string selectQuery = """
            SELECT TOP 1 [PlanName], [PlanCode]
            FROM [TblPricingPlan]
            WHERE [ID] = @id
            """;

        const string deleteQuery = "DELETE FROM [TblPricingPlan] WHERE [ID] = @id";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        string planName;
        string planCode;
        await using (var selectCommand = new SqlCommand(selectQuery, connection, transaction))
        {
            selectCommand.Parameters.Add("@id", SqlDbType.Int).Value = id;
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new KeyNotFoundException($"Pricing plan with id {id} was not found.");
            }

            planName = reader["PlanName"]?.ToString() ?? string.Empty;
            planCode = reader["PlanCode"]?.ToString() ?? string.Empty;
        }

        await using (var deleteCommand = new SqlCommand(deleteQuery, connection, transaction))
        {
            deleteCommand.Parameters.Add("@id", SqlDbType.Int).Value = id;
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(connection, transaction, userId, DeleteAuditAction, $"Deleted pricing plan '{planCode}' - '{planName}' (ID: {id}) by '{username}'.", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PricingPlanImportResult> ImportPlansAsync(
        IReadOnlyList<PricingPlanFormViewModel> plans,
        int? userId,
        string username,
        CancellationToken cancellationToken = default)
    {
        const string selectQuery = """
            SELECT TOP 1 [ID]
            FROM [TblPricingPlan]
            WHERE LOWER(LTRIM(RTRIM([PlanCode]))) = LOWER(@planCode)
            """;

        const string insertQuery = """
            INSERT INTO [TblPricingPlan]
                ([PlanName], [PlanCode], [ResellerPrice], [FinalPrice], [BaseData], [ResellerOverChargePrice], [FinalOverChargePrice], [Status], [Created_Date], [Created_By], [Updated_Date], [Updated_By])
            VALUES
                (@planName, @planCode, @resellerPrice, @finalPrice, @baseData, @resellerOverChargePrice, @finalOverChargePrice, @status, GETDATE(), @createdBy, GETDATE(), @updatedBy)
            """;

        const string updateQuery = """
            UPDATE [TblPricingPlan]
            SET
                [PlanName] = @planName,
                [ResellerPrice] = @resellerPrice,
                [FinalPrice] = @finalPrice,
                [BaseData] = @baseData,
                [ResellerOverChargePrice] = @resellerOverChargePrice,
                [FinalOverChargePrice] = @finalOverChargePrice,
                [Status] = @status,
                [Updated_Date] = GETDATE(),
                [Updated_By] = @updatedBy
            WHERE [ID] = @id
            """;

        var result = new PricingPlanImportResult();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        foreach (var plan in plans)
        {
            int? existingId;
            await using (var selectCommand = new SqlCommand(selectQuery, connection, transaction))
            {
                selectCommand.Parameters.Add("@planCode", SqlDbType.NVarChar, 100).Value = plan.PlanCode;
                var scalar = await selectCommand.ExecuteScalarAsync(cancellationToken);
                existingId = scalar is null || scalar == DBNull.Value ? null : Convert.ToInt32(scalar);
            }

            if (existingId.HasValue)
            {
                await using var updateCommand = new SqlCommand(updateQuery, connection, transaction);
                updateCommand.Parameters.Add("@id", SqlDbType.Int).Value = existingId.Value;
                AddFormParameters(updateCommand, plan);
                updateCommand.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 50).Value = username;
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                result.UpdatedCount++;
            }
            else
            {
                await using var insertCommand = new SqlCommand(insertQuery, connection, transaction);
                AddFormParameters(insertCommand, plan);
                insertCommand.Parameters.Add("@createdBy", SqlDbType.NVarChar, 50).Value = username;
                insertCommand.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 50).Value = username;
                await insertCommand.ExecuteNonQueryAsync(cancellationToken);
                result.CreatedCount++;
            }
        }

        await InsertAuditAsync(
            connection,
            transaction,
            userId,
            ImportAuditAction,
            $"Imported pricing plans by '{username}'. Created: {result.CreatedCount}, updated: {result.UpdatedCount}, skipped: {result.SkippedCount}.",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return result;
    }

    public async Task<TenantPricingPageResult> GetTenantPricesAsync(int page, int pageSize, int? tenantId = null, string? search = null, CancellationToken cancellationToken = default)
    {
        const string countQuery = """
            SELECT COUNT(1)
            FROM [TblTenantPricing] tp
            INNER JOIN [TblTenant] t ON t.[ID] = tp.[TenantId]
            INNER JOIN [TblPricingPlan] pp ON pp.[ID] = tp.[PricingPlanId]
            WHERE (@tenantId IS NULL OR tp.[TenantId] = @tenantId)
              AND (
                @search = N''
                OR pp.[PlanName] LIKE @searchPattern
                OR pp.[PlanCode] LIKE @searchPattern
              )
            """;
        const string listQuery = """
            SELECT
                tp.[ID],
                tp.[TenantId],
                t.[TenantName],
                tp.[PricingPlanId],
                pp.[PlanName],
                pp.[PlanCode],
                pp.[BaseData],
                tp.[ResellerPrice],
                tp.[FinalPrice],
                tp.[ResellerOverChargePrice],
                tp.[FinalOverChargePrice],
                tp.[Updated_Date],
                tp.[Updated_By]
            FROM [TblTenantPricing] tp
            INNER JOIN [TblTenant] t ON t.[ID] = tp.[TenantId]
            INNER JOIN [TblPricingPlan] pp ON pp.[ID] = tp.[PricingPlanId]
            WHERE (@tenantId IS NULL OR tp.[TenantId] = @tenantId)
              AND (
                @search = N''
                OR pp.[PlanName] LIKE @searchPattern
                OR pp.[PlanCode] LIKE @searchPattern
              )
            ORDER BY t.[TenantName] ASC, pp.[PlanName] ASC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            """;

        var normalizedPageSize = pageSize <= 0 ? 10 : pageSize;
        var normalizedPage = page <= 0 ? 1 : page;
        var normalizedTenantId = tenantId.GetValueOrDefault() > 0 ? tenantId : null;
        var normalizedSearch = (search ?? string.Empty).Trim();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);

        int totalPrices;
        await using (var countCommand = new SqlCommand(countQuery, connection))
        {
            AddTenantPricingFilterParameters(countCommand, normalizedTenantId, normalizedSearch);
            totalPrices = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        }

        var totalPages = totalPrices == 0 ? 1 : (int)Math.Ceiling(totalPrices / (double)normalizedPageSize);
        var clampedPage = Math.Min(normalizedPage, totalPages);
        var offset = (clampedPage - 1) * normalizedPageSize;

        var prices = new List<TenantPricingListItemViewModel>();
        await using (var listCommand = new SqlCommand(listQuery, connection))
        {
            AddTenantPricingFilterParameters(listCommand, normalizedTenantId, normalizedSearch);
            listCommand.Parameters.Add("@offset", SqlDbType.Int).Value = offset;
            listCommand.Parameters.Add("@pageSize", SqlDbType.Int).Value = normalizedPageSize;
            await using var reader = await listCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                prices.Add(MapTenantPriceListItem(reader));
            }
        }

        return new TenantPricingPageResult
        {
            Prices = prices,
            CurrentPage = clampedPage,
            PageSize = normalizedPageSize,
            TotalPrices = totalPrices
        };
    }

    public async Task<List<TenantPricingListItemViewModel>> GetTenantPricesForExportAsync(int? tenantId = null, string? search = null, CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT
                tp.[ID],
                tp.[TenantId],
                t.[TenantName],
                tp.[PricingPlanId],
                pp.[PlanName],
                pp.[PlanCode],
                pp.[BaseData],
                tp.[ResellerPrice],
                tp.[FinalPrice],
                tp.[ResellerOverChargePrice],
                tp.[FinalOverChargePrice],
                tp.[Updated_Date],
                tp.[Updated_By]
            FROM [TblTenantPricing] tp
            INNER JOIN [TblTenant] t ON t.[ID] = tp.[TenantId]
            INNER JOIN [TblPricingPlan] pp ON pp.[ID] = tp.[PricingPlanId]
            WHERE (@tenantId IS NULL OR tp.[TenantId] = @tenantId)
              AND (
                @search = N''
                OR pp.[PlanName] LIKE @searchPattern
                OR pp.[PlanCode] LIKE @searchPattern
              )
            ORDER BY t.[TenantName] ASC, pp.[PlanName] ASC
            """;
        var normalizedTenantId = tenantId.GetValueOrDefault() > 0 ? tenantId : null;
        var normalizedSearch = (search ?? string.Empty).Trim();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);

        var prices = new List<TenantPricingListItemViewModel>();
        await using var command = new SqlCommand(query, connection);
        AddTenantPricingFilterParameters(command, normalizedTenantId, normalizedSearch);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            prices.Add(MapTenantPriceListItem(reader));
        }

        return prices;
    }

    public async Task<TenantPricingFormViewModel?> GetTenantPriceByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT TOP 1
                [ID],
                [TenantId],
                [PricingPlanId],
                [ResellerPrice],
                [FinalPrice],
                [ResellerOverChargePrice],
                [FinalOverChargePrice]
            FROM [TblTenantPricing]
            WHERE [ID] = @id
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapTenantPriceForm(reader);
    }

    public async Task<bool> IsTenantPlanPriceInUseAsync(int tenantId, int pricingPlanId, int? excludeTenantPriceId = null, CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT TOP 1 1
            FROM [TblTenantPricing]
            WHERE [TenantId] = @tenantId
              AND [PricingPlanId] = @pricingPlanId
              AND (@excludeId IS NULL OR [ID] <> @excludeId)
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@pricingPlanId", SqlDbType.Int).Value = pricingPlanId;
        command.Parameters.Add("@excludeId", SqlDbType.Int).Value = (object?)excludeTenantPriceId ?? DBNull.Value;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value;
    }

    public async Task<int> CreateTenantPriceAsync(TenantPricingFormViewModel model, int? userId, string username, CancellationToken cancellationToken = default)
    {
        const string query = """
            INSERT INTO [TblTenantPricing]
                ([TenantId], [PricingPlanId], [ResellerPrice], [FinalPrice], [ResellerOverChargePrice], [FinalOverChargePrice], [Created_Date], [Created_By], [Updated_Date], [Updated_By])
            OUTPUT INSERTED.[ID]
            VALUES
                (@tenantId, @pricingPlanId, @resellerPrice, @finalPrice, @resellerOverChargePrice, @finalOverChargePrice, GETDATE(), @createdBy, GETDATE(), @updatedBy)
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        await using var command = new SqlCommand(query, connection, transaction);
        AddTenantPriceParameters(command, model);
        command.Parameters.Add("@createdBy", SqlDbType.NVarChar, 50).Value = username;
        command.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 50).Value = username;
        var tenantPriceId = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));

        await InsertAuditAsync(connection, transaction, userId, CreateTenantPriceAuditAction, $"Created tenant pricing ID {tenantPriceId}.", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return tenantPriceId;
    }

    public async Task UpdateTenantPriceAsync(TenantPricingFormViewModel model, int? userId, string username, CancellationToken cancellationToken = default)
    {
        const string updateQuery = """
            UPDATE [TblTenantPricing]
            SET
                [TenantId] = @tenantId,
                [PricingPlanId] = @pricingPlanId,
                [ResellerPrice] = @resellerPrice,
                [FinalPrice] = @finalPrice,
                [ResellerOverChargePrice] = @resellerOverChargePrice,
                [FinalOverChargePrice] = @finalOverChargePrice,
                [Updated_Date] = GETDATE(),
                [Updated_By] = @updatedBy
            WHERE [ID] = @id
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        await using var command = new SqlCommand(updateQuery, connection, transaction);
        command.Parameters.Add("@id", SqlDbType.Int).Value = model.Id;
        AddTenantPriceParameters(command, model);
        command.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 50).Value = username;
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            throw new KeyNotFoundException($"Tenant pricing with id {model.Id} was not found.");
        }

        await InsertAuditAsync(connection, transaction, userId, UpdateTenantPriceAuditAction, $"Updated tenant pricing ID {model.Id}.", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteTenantPriceAsync(int id, int? userId, string username, CancellationToken cancellationToken = default)
    {
        const string query = "DELETE FROM [TblTenantPricing] WHERE [ID] = @id";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            throw new KeyNotFoundException($"Tenant pricing with id {id} was not found.");
        }

        await InsertAuditAsync(connection, transaction, userId, DeleteTenantPriceAuditAction, $"Deleted tenant pricing ID {id} by '{username}'.", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<TenantPricingImportResult> ImportTenantPricesAsync(IReadOnlyList<TenantPricingImportRow> prices, int? userId, string username, CancellationToken cancellationToken = default)
    {
        const string selectTenantQuery = "SELECT TOP 1 [ID] FROM [TblTenant] WHERE [ID] = @tenantId";
        const string selectPlanQuery = "SELECT TOP 1 [ID] FROM [TblPricingPlan] WHERE LOWER(LTRIM(RTRIM([PlanCode]))) = LOWER(@planCode)";
        const string selectTenantPriceQuery = "SELECT TOP 1 [ID] FROM [TblTenantPricing] WHERE [TenantId] = @tenantId AND [PricingPlanId] = @pricingPlanId";
        const string insertQuery = """
            INSERT INTO [TblTenantPricing]
                ([TenantId], [PricingPlanId], [ResellerPrice], [FinalPrice], [ResellerOverChargePrice], [FinalOverChargePrice], [Created_Date], [Created_By], [Updated_Date], [Updated_By])
            VALUES
                (@tenantId, @pricingPlanId, @resellerPrice, @finalPrice, @resellerOverChargePrice, @finalOverChargePrice, GETDATE(), @createdBy, GETDATE(), @updatedBy)
            """;
        const string updateQuery = """
            UPDATE [TblTenantPricing]
            SET
                [ResellerPrice] = @resellerPrice,
                [FinalPrice] = @finalPrice,
                [ResellerOverChargePrice] = @resellerOverChargePrice,
                [FinalOverChargePrice] = @finalOverChargePrice,
                [Updated_Date] = GETDATE(),
                [Updated_By] = @updatedBy
            WHERE [ID] = @id
            """;

        var result = new TenantPricingImportResult();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        foreach (var row in prices)
        {
            int? tenantId;
            await using (var tenantCommand = new SqlCommand(selectTenantQuery, connection, transaction))
            {
                tenantCommand.Parameters.Add("@tenantId", SqlDbType.Int).Value = row.TenantId;
                var scalar = await tenantCommand.ExecuteScalarAsync(cancellationToken);
                tenantId = scalar is null || scalar == DBNull.Value ? null : Convert.ToInt32(scalar);
            }

            if (!tenantId.HasValue)
            {
                result.Errors.Add($"Không tìm thấy tenant '{row.TenantName}' trong file import.");
                continue;
            }

            int? pricingPlanId;
            await using (var planCommand = new SqlCommand(selectPlanQuery, connection, transaction))
            {
                planCommand.Parameters.Add("@planCode", SqlDbType.NVarChar, 100).Value = row.PlanCode;
                var scalar = await planCommand.ExecuteScalarAsync(cancellationToken);
                pricingPlanId = scalar is null || scalar == DBNull.Value ? null : Convert.ToInt32(scalar);
            }

            if (!pricingPlanId.HasValue)
            {
                result.Errors.Add($"Không tìm thấy gói giá '{row.PlanCode}'.");
                continue;
            }

            int? existingId;
            await using (var selectCommand = new SqlCommand(selectTenantPriceQuery, connection, transaction))
            {
                selectCommand.Parameters.Add("@tenantId", SqlDbType.Int).Value = row.TenantId;
                selectCommand.Parameters.Add("@pricingPlanId", SqlDbType.Int).Value = pricingPlanId.Value;
                var scalar = await selectCommand.ExecuteScalarAsync(cancellationToken);
                existingId = scalar is null || scalar == DBNull.Value ? null : Convert.ToInt32(scalar);
            }

            if (existingId.HasValue)
            {
                await using var updateCommand = new SqlCommand(updateQuery, connection, transaction);
                updateCommand.Parameters.Add("@id", SqlDbType.Int).Value = existingId.Value;
                AddTenantPriceImportParameters(updateCommand, row, pricingPlanId.Value);
                updateCommand.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 50).Value = username;
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                result.UpdatedCount++;
            }
            else
            {
                await using var insertCommand = new SqlCommand(insertQuery, connection, transaction);
                AddTenantPriceImportParameters(insertCommand, row, pricingPlanId.Value);
                insertCommand.Parameters.Add("@createdBy", SqlDbType.NVarChar, 50).Value = username;
                insertCommand.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 50).Value = username;
                await insertCommand.ExecuteNonQueryAsync(cancellationToken);
                result.CreatedCount++;
            }
        }

        result.SkippedCount = result.Errors.Count;
        if (result.Errors.Count > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return result;
        }

        await InsertAuditAsync(connection, transaction, userId, ImportTenantPriceAuditAction, $"Imported tenant pricing by '{username}'. Created: {result.CreatedCount}, updated: {result.UpdatedCount}.", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static PricingPlanListItemViewModel MapListItem(SqlDataReader reader)
    {
        return new PricingPlanListItemViewModel
        {
            Id = reader["ID"] is int id ? id : 0,
            PlanName = reader["PlanName"]?.ToString() ?? string.Empty,
            PlanCode = reader["PlanCode"]?.ToString() ?? string.Empty,
            ResellerPrice = ReadDecimal(reader, "ResellerPrice"),
            FinalPrice = ReadDecimal(reader, "FinalPrice"),
            BaseData = ReadDecimal(reader, "BaseData"),
            ResellerOverChargePrice = ReadDecimal(reader, "ResellerOverChargePrice"),
            FinalOverChargePrice = ReadDecimal(reader, "FinalOverChargePrice"),
            Status = reader["Status"]?.ToString() ?? "active",
            UpdatedDate = reader["Updated_Date"] as DateTime?,
            UpdatedBy = reader["Updated_By"]?.ToString()
        };
    }

    private static PricingPlanFormViewModel MapForm(SqlDataReader reader)
    {
        return new PricingPlanFormViewModel
        {
            Id = reader["ID"] is int id ? id : 0,
            PlanName = reader["PlanName"]?.ToString() ?? string.Empty,
            PlanCode = reader["PlanCode"]?.ToString() ?? string.Empty,
            ResellerPrice = ReadDecimal(reader, "ResellerPrice"),
            FinalPrice = ReadDecimal(reader, "FinalPrice"),
            BaseData = ReadDecimal(reader, "BaseData"),
            ResellerOverChargePrice = ReadDecimal(reader, "ResellerOverChargePrice"),
            FinalOverChargePrice = ReadDecimal(reader, "FinalOverChargePrice"),
            Status = reader["Status"]?.ToString() ?? "active"
        };
    }

    private static TenantPricingListItemViewModel MapTenantPriceListItem(SqlDataReader reader)
    {
        return new TenantPricingListItemViewModel
        {
            Id = reader["ID"] is int id ? id : 0,
            TenantId = reader["TenantId"] is int tenantId ? tenantId : 0,
            TenantName = reader["TenantName"]?.ToString() ?? string.Empty,
            PricingPlanId = reader["PricingPlanId"] is int pricingPlanId ? pricingPlanId : 0,
            PlanName = reader["PlanName"]?.ToString() ?? string.Empty,
            PlanCode = reader["PlanCode"]?.ToString() ?? string.Empty,
            BaseData = ReadDecimal(reader, "BaseData"),
            ResellerPrice = ReadDecimal(reader, "ResellerPrice"),
            FinalPrice = ReadDecimal(reader, "FinalPrice"),
            ResellerOverChargePrice = ReadDecimal(reader, "ResellerOverChargePrice"),
            FinalOverChargePrice = ReadDecimal(reader, "FinalOverChargePrice"),
            UpdatedDate = reader["Updated_Date"] as DateTime?,
            UpdatedBy = reader["Updated_By"]?.ToString()
        };
    }

    private static TenantPricingFormViewModel MapTenantPriceForm(SqlDataReader reader)
    {
        return new TenantPricingFormViewModel
        {
            Id = reader["ID"] is int id ? id : 0,
            TenantId = reader["TenantId"] is int tenantId ? tenantId : 0,
            PricingPlanId = reader["PricingPlanId"] is int pricingPlanId ? pricingPlanId : 0,
            ResellerPrice = ReadDecimal(reader, "ResellerPrice"),
            FinalPrice = ReadDecimal(reader, "FinalPrice"),
            ResellerOverChargePrice = ReadDecimal(reader, "ResellerOverChargePrice"),
            FinalOverChargePrice = ReadDecimal(reader, "FinalOverChargePrice")
        };
    }

    private static void AddFormParameters(SqlCommand command, PricingPlanFormViewModel model)
    {
        command.Parameters.Add("@planName", SqlDbType.NVarChar, 250).Value = model.PlanName;
        command.Parameters.Add("@planCode", SqlDbType.NVarChar, 100).Value = model.PlanCode;
        command.Parameters.Add("@resellerPrice", SqlDbType.Decimal).Value = model.ResellerPrice;
        command.Parameters["@resellerPrice"].Precision = 18;
        command.Parameters["@resellerPrice"].Scale = 2;
        command.Parameters.Add("@finalPrice", SqlDbType.Decimal).Value = model.FinalPrice;
        command.Parameters["@finalPrice"].Precision = 18;
        command.Parameters["@finalPrice"].Scale = 2;
        command.Parameters.Add("@baseData", SqlDbType.Decimal).Value = model.BaseData;
        command.Parameters["@baseData"].Precision = 18;
        command.Parameters["@baseData"].Scale = 2;
        command.Parameters.Add("@resellerOverChargePrice", SqlDbType.Decimal).Value = model.ResellerOverChargePrice;
        command.Parameters["@resellerOverChargePrice"].Precision = 18;
        command.Parameters["@resellerOverChargePrice"].Scale = 2;
        command.Parameters.Add("@finalOverChargePrice", SqlDbType.Decimal).Value = model.FinalOverChargePrice;
        command.Parameters["@finalOverChargePrice"].Precision = 18;
        command.Parameters["@finalOverChargePrice"].Scale = 2;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 50).Value = model.Status;
    }

    private static void AddTenantPriceParameters(SqlCommand command, TenantPricingFormViewModel model)
    {
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = model.TenantId;
        command.Parameters.Add("@pricingPlanId", SqlDbType.Int).Value = model.PricingPlanId;
        command.Parameters.Add("@resellerPrice", SqlDbType.Decimal).Value = model.ResellerPrice;
        command.Parameters["@resellerPrice"].Precision = 18;
        command.Parameters["@resellerPrice"].Scale = 2;
        command.Parameters.Add("@finalPrice", SqlDbType.Decimal).Value = model.FinalPrice;
        command.Parameters["@finalPrice"].Precision = 18;
        command.Parameters["@finalPrice"].Scale = 2;
        command.Parameters.Add("@resellerOverChargePrice", SqlDbType.Decimal).Value = model.ResellerOverChargePrice;
        command.Parameters["@resellerOverChargePrice"].Precision = 18;
        command.Parameters["@resellerOverChargePrice"].Scale = 2;
        command.Parameters.Add("@finalOverChargePrice", SqlDbType.Decimal).Value = model.FinalOverChargePrice;
        command.Parameters["@finalOverChargePrice"].Precision = 18;
        command.Parameters["@finalOverChargePrice"].Scale = 2;
    }

    private static void AddTenantPriceImportParameters(SqlCommand command, TenantPricingImportRow row, int pricingPlanId)
    {
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = row.TenantId;
        command.Parameters.Add("@pricingPlanId", SqlDbType.Int).Value = pricingPlanId;
        command.Parameters.Add("@resellerPrice", SqlDbType.Decimal).Value = row.ResellerPrice;
        command.Parameters["@resellerPrice"].Precision = 18;
        command.Parameters["@resellerPrice"].Scale = 2;
        command.Parameters.Add("@finalPrice", SqlDbType.Decimal).Value = row.FinalPrice;
        command.Parameters["@finalPrice"].Precision = 18;
        command.Parameters["@finalPrice"].Scale = 2;
        command.Parameters.Add("@resellerOverChargePrice", SqlDbType.Decimal).Value = row.ResellerOverChargePrice;
        command.Parameters["@resellerOverChargePrice"].Precision = 18;
        command.Parameters["@resellerOverChargePrice"].Scale = 2;
        command.Parameters.Add("@finalOverChargePrice", SqlDbType.Decimal).Value = row.FinalOverChargePrice;
        command.Parameters["@finalOverChargePrice"].Precision = 18;
        command.Parameters["@finalOverChargePrice"].Scale = 2;
    }

    private static void AddTenantPricingFilterParameters(SqlCommand command, int? tenantId, string search)
    {
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@search", SqlDbType.NVarChar, 250).Value = search;
        command.Parameters.Add("@searchPattern", SqlDbType.NVarChar, 260).Value = $"%{EscapeLikeValue(search)}%";
    }

    private static string EscapeLikeValue(string value)
    {
        return value
            .Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal);
    }

    private async Task EnsureSchemaAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        if (_schemaEnsured)
        {
            return;
        }

        const string query = """
            IF OBJECT_ID(N'[dbo].[TblPricingPlan]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[TblPricingPlan](
                    [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblPricingPlan] PRIMARY KEY,
                    [PlanName] nvarchar(250) NOT NULL,
                    [PlanCode] nvarchar(100) NOT NULL,
                    [ResellerPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPricingPlan_ResellerPrice] DEFAULT(0),
                    [FinalPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPricingPlan_FinalPrice] DEFAULT(0),
                    [BaseData] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPricingPlan_BaseData] DEFAULT(0),
                    [ResellerOverChargePrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPricingPlan_ResellerOverChargePrice] DEFAULT(0),
                    [FinalOverChargePrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPricingPlan_FinalOverChargePrice] DEFAULT(0),
                    [Status] nvarchar(50) NOT NULL CONSTRAINT [DF_TblPricingPlan_Status] DEFAULT('active'),
                    [Created_Date] datetime NULL,
                    [Created_By] nvarchar(50) NULL,
                    [Updated_Date] datetime NULL,
                    [Updated_By] nvarchar(50) NULL
                );
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE name = N'UX_TblPricingPlan_PlanCode'
                  AND object_id = OBJECT_ID(N'[dbo].[TblPricingPlan]')
            )
            BEGIN
                CREATE UNIQUE INDEX [UX_TblPricingPlan_PlanCode]
                    ON [dbo].[TblPricingPlan]([PlanCode]);
            END;

            IF OBJECT_ID(N'[dbo].[TblTenantPricing]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[TblTenantPricing](
                    [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblTenantPricing] PRIMARY KEY,
                    [TenantId] int NOT NULL,
                    [PricingPlanId] int NOT NULL,
                    [ResellerPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblTenantPricing_ResellerPrice] DEFAULT(0),
                    [FinalPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblTenantPricing_FinalPrice] DEFAULT(0),
                    [ResellerOverChargePrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblTenantPricing_ResellerOverChargePrice] DEFAULT(0),
                    [FinalOverChargePrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblTenantPricing_FinalOverChargePrice] DEFAULT(0),
                    [Created_Date] datetime NULL,
                    [Created_By] nvarchar(50) NULL,
                    [Updated_Date] datetime NULL,
                    [Updated_By] nvarchar(50) NULL
                );
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.foreign_keys
                WHERE name = N'FK_TblTenantPricing_TblTenant'
                  AND parent_object_id = OBJECT_ID(N'[dbo].[TblTenantPricing]')
            )
            BEGIN
                ALTER TABLE [dbo].[TblTenantPricing]
                ADD CONSTRAINT [FK_TblTenantPricing_TblTenant]
                    FOREIGN KEY ([TenantId]) REFERENCES [dbo].[TblTenant]([ID]);
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.foreign_keys
                WHERE name = N'FK_TblTenantPricing_TblPricingPlan'
                  AND parent_object_id = OBJECT_ID(N'[dbo].[TblTenantPricing]')
            )
            BEGIN
                ALTER TABLE [dbo].[TblTenantPricing]
                ADD CONSTRAINT [FK_TblTenantPricing_TblPricingPlan]
                    FOREIGN KEY ([PricingPlanId]) REFERENCES [dbo].[TblPricingPlan]([ID]);
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE name = N'UX_TblTenantPricing_Tenant_Plan'
                  AND object_id = OBJECT_ID(N'[dbo].[TblTenantPricing]')
            )
            BEGIN
                CREATE UNIQUE INDEX [UX_TblTenantPricing_Tenant_Plan]
                    ON [dbo].[TblTenantPricing]([TenantId], [PricingPlanId]);
            END;
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        _schemaEnsured = true;
    }

    private static async Task InsertAuditAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int? userId,
        string logAction,
        string logDetail,
        CancellationToken cancellationToken)
    {
        const string query = """
            INSERT INTO [TblAudit]
                ([IDUser], [LogDate], [LogAction], [LogDetail], [IDDevice])
            VALUES
                (@userId, GETDATE(), @logAction, @logDetail, @deviceId)
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@userId", SqlDbType.Int).Value = (object?)userId ?? DBNull.Value;
        command.Parameters.Add("@logAction", SqlDbType.NVarChar, 100).Value = logAction;
        command.Parameters.Add("@logDetail", SqlDbType.NVarChar, -1).Value = logDetail;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static decimal ReadDecimal(SqlDataReader reader, string columnName)
    {
        return reader[columnName] is decimal value ? value : 0;
    }

    private static string BuildUpdateAuditDetail(PricingPlanFormViewModel existingPlan, PricingPlanFormViewModel updatedPlan)
    {
        var changedFields = new List<string>();

        if (!string.Equals(existingPlan.PlanName, updatedPlan.PlanName, StringComparison.Ordinal))
        {
            changedFields.Add("PlanName");
        }

        if (!string.Equals(existingPlan.PlanCode, updatedPlan.PlanCode, StringComparison.OrdinalIgnoreCase))
        {
            changedFields.Add("PlanCode");
        }

        if (existingPlan.ResellerPrice != updatedPlan.ResellerPrice)
        {
            changedFields.Add("ResellerPrice");
        }

        if (existingPlan.FinalPrice != updatedPlan.FinalPrice)
        {
            changedFields.Add("FinalPrice");
        }

        if (existingPlan.BaseData != updatedPlan.BaseData)
        {
            changedFields.Add("BaseData");
        }

        if (existingPlan.ResellerOverChargePrice != updatedPlan.ResellerOverChargePrice)
        {
            changedFields.Add("ResellerOverChargePrice");
        }

        if (existingPlan.FinalOverChargePrice != updatedPlan.FinalOverChargePrice)
        {
            changedFields.Add("FinalOverChargePrice");
        }

        if (!string.Equals(existingPlan.Status, updatedPlan.Status, StringComparison.OrdinalIgnoreCase))
        {
            changedFields.Add("Status");
        }

        return changedFields.Count == 0
            ? $"Updated pricing plan '{updatedPlan.PlanCode}' (ID: {updatedPlan.Id}). No field changes detected."
            : $"Updated pricing plan '{updatedPlan.PlanCode}' (ID: {updatedPlan.Id}). Changed fields: {string.Join(", ", changedFields)}.";
    }
}
