using System.Data;
using Microsoft.Data.SqlClient;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public class TenantService(IConfiguration configuration) : ITenantService
{
    private const string CreateTenantAuditAction = "created_tenant";
    private const string UpdateTenantAuditAction = "updated_tenant";
    private const string DeleteTenantAuditAction = "deleted_tenant";

    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing connection string: DefaultConnection");

    public async Task<TenantPageResult> GetTenantsAsync(int page, int pageSize, int? tenantId = null, CancellationToken cancellationToken = default)
    {
        const string countQuery = """
            SELECT COUNT(1)
            FROM [TblTenant]
            WHERE (@tenantId IS NULL OR [ID] = @tenantId)
            """;

        const string listQuery = """
            SELECT
                [ID],
                [TenantName],
                [Email],
                [PhoneNumber],
                [Description],
                [Logo],
                [Address],
                [Created_Date],
                [Created_By],
                [Updated_Date],
                [Updated_By]
            FROM [TblTenant]
            WHERE (@tenantId IS NULL OR [ID] = @tenantId)
            ORDER BY [ID] DESC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            """;

        var normalizedPageSize = pageSize <= 0 ? 10 : pageSize;
        var normalizedPage = page <= 0 ? 1 : page;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        int totalTenants;
        await using (var countCommand = new SqlCommand(countQuery, connection))
        {
            countCommand.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
            totalTenants = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        }

        var totalPages = totalTenants == 0 ? 1 : (int)Math.Ceiling(totalTenants / (double)normalizedPageSize);
        var clampedPage = Math.Min(normalizedPage, totalPages);
        var offset = (clampedPage - 1) * normalizedPageSize;

        var tenants = new List<TenantListItemViewModel>();
        await using (var listCommand = new SqlCommand(listQuery, connection))
        {
            listCommand.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
            listCommand.Parameters.Add("@offset", SqlDbType.Int).Value = offset;
            listCommand.Parameters.Add("@pageSize", SqlDbType.Int).Value = normalizedPageSize;

            await using var reader = await listCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tenants.Add(MapTenantListItem(reader));
            }
        }

        return new TenantPageResult
        {
            Tenants = tenants,
            CurrentPage = clampedPage,
            PageSize = normalizedPageSize,
            TotalTenants = totalTenants
        };
    }

    public async Task<List<DeviceTenantOptionViewModel>> GetTenantOptionsAsync(int? tenantId = null, CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT
                [ID],
                [TenantName]
            FROM [TblTenant]
            WHERE (@tenantId IS NULL OR [ID] = @tenantId)
            ORDER BY [TenantName] ASC, [ID] ASC
            """;

        var tenants = new List<DeviceTenantOptionViewModel>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            tenants.Add(new DeviceTenantOptionViewModel
            {
                Id = reader["ID"] is int id ? id : 0,
                TenantName = reader["TenantName"]?.ToString() ?? string.Empty
            });
        }

        return tenants;
    }

    public async Task<TenantFormViewModel?> GetTenantByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT TOP 1
                [ID],
                [TenantName],
                [Email],
                [PhoneNumber],
                [Description],
                [Logo],
                [Address]
            FROM [TblTenant]
            WHERE [ID] = @id
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TenantFormViewModel
        {
            Id = reader["ID"] is int tenantId ? tenantId : 0,
            TenantName = reader["TenantName"]?.ToString() ?? string.Empty,
            Email = reader["Email"]?.ToString(),
            Phone = reader["PhoneNumber"]?.ToString(),
            Description = reader["Description"]?.ToString(),
            ExistingLogoPath = reader["Logo"]?.ToString(),
            Address = reader["Address"]?.ToString()
        };
    }

    public async Task<int> CreateTenantAsync(
        TenantFormViewModel model,
        int? userId,
        string username,
        CancellationToken cancellationToken = default)
    {
        const string query = """
            INSERT INTO [TblTenant]
                ([TenantName], [Email], [PhoneNumber], [Description], [Logo], [Address], [Created_Date], [Created_By], [Updated_Date], [Updated_By])
            OUTPUT INSERTED.[ID]
            VALUES
                (@tenantName, @email, @phoneNumber, @description, @logo, @address, GETDATE(), @createdBy, GETDATE(), @updatedBy)
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@tenantName", SqlDbType.NVarChar, 250).Value = model.TenantName;
        command.Parameters.Add("@email", SqlDbType.NVarChar, 350).Value = (object?)model.Email ?? DBNull.Value;
        command.Parameters.Add("@phoneNumber", SqlDbType.NVarChar, 50).Value = (object?)model.Phone ?? DBNull.Value;
        command.Parameters.Add("@description", SqlDbType.NVarChar, 1000).Value = (object?)model.Description ?? DBNull.Value;
        command.Parameters.Add("@logo", SqlDbType.NVarChar, 550).Value = (object?)model.ExistingLogoPath ?? DBNull.Value;
        command.Parameters.Add("@address", SqlDbType.NVarChar, 550).Value = (object?)model.Address ?? DBNull.Value;
        command.Parameters.Add("@createdBy", SqlDbType.NVarChar, 50).Value = username;
        command.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 50).Value = username;

        var tenantId = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));

        var auditDetail = $"Created tenant '{model.TenantName}' (ID: {tenantId}).";
        await InsertAuditAsync(connection, transaction, userId, CreateTenantAuditAction, auditDetail, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return tenantId;
    }

    public async Task UpdateTenantAsync(
        TenantFormViewModel model,
        int? userId,
        string username,
        CancellationToken cancellationToken = default)
    {
        const string selectQuery = """
            SELECT TOP 1
                [ID],
                [TenantName],
                [Email],
                [PhoneNumber],
                [Description],
                [Logo],
                [Address]
            FROM [TblTenant]
            WHERE [ID] = @id
            """;

        const string updateQuery = """
            UPDATE [TblTenant]
            SET
                [TenantName] = @tenantName,
                [Email] = @email,
                [PhoneNumber] = @phoneNumber,
                [Description] = @description,
                [Logo] = @logo,
                [Address] = @address,
                [Updated_Date] = GETDATE(),
                [Updated_By] = @updatedBy
            WHERE [ID] = @id
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        TenantFormViewModel? existingTenant;
        await using (var selectCommand = new SqlCommand(selectQuery, connection, transaction))
        {
            selectCommand.Parameters.Add("@id", SqlDbType.Int).Value = model.Id;
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new KeyNotFoundException($"Tenant with id {model.Id} was not found.");
            }

            existingTenant = new TenantFormViewModel
            {
                Id = reader["ID"] is int tenantId ? tenantId : 0,
                TenantName = reader["TenantName"]?.ToString() ?? string.Empty,
                Email = reader["Email"]?.ToString(),
                Phone = reader["PhoneNumber"]?.ToString(),
                Description = reader["Description"]?.ToString(),
                ExistingLogoPath = reader["Logo"]?.ToString(),
                Address = reader["Address"]?.ToString()
            };
        }

        await using (var updateCommand = new SqlCommand(updateQuery, connection, transaction))
        {
            updateCommand.Parameters.Add("@id", SqlDbType.Int).Value = model.Id;
            updateCommand.Parameters.Add("@tenantName", SqlDbType.NVarChar, 250).Value = model.TenantName;
            updateCommand.Parameters.Add("@email", SqlDbType.NVarChar, 350).Value = (object?)model.Email ?? DBNull.Value;
            updateCommand.Parameters.Add("@phoneNumber", SqlDbType.NVarChar, 50).Value = (object?)model.Phone ?? DBNull.Value;
            updateCommand.Parameters.Add("@description", SqlDbType.NVarChar, 1000).Value = (object?)model.Description ?? DBNull.Value;
            updateCommand.Parameters.Add("@logo", SqlDbType.NVarChar, 550).Value = (object?)model.ExistingLogoPath ?? DBNull.Value;
            updateCommand.Parameters.Add("@address", SqlDbType.NVarChar, 550).Value = (object?)model.Address ?? DBNull.Value;
            updateCommand.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 50).Value = username;
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var auditDetail = BuildUpdateAuditDetail(existingTenant, model);
        await InsertAuditAsync(connection, transaction, userId, UpdateTenantAuditAction, auditDetail, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteTenantAsync(int id, int? userId, string username, CancellationToken cancellationToken = default)
    {
        const string selectQuery = """
            SELECT TOP 1
                [TenantName],
                [Email],
                [PhoneNumber],
                [Description],
                [Logo],
                [Address]
            FROM [TblTenant]
            WHERE [ID] = @id
            """;

        const string deleteQuery = """
            DELETE FROM [TblTenant]
            WHERE [ID] = @id
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        TenantFormViewModel? existingTenant;
        await using (var selectCommand = new SqlCommand(selectQuery, connection, transaction))
        {
            selectCommand.Parameters.Add("@id", SqlDbType.Int).Value = id;
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new KeyNotFoundException($"Tenant with id {id} was not found.");
            }

            existingTenant = new TenantFormViewModel
            {
                Id = id,
                TenantName = reader["TenantName"]?.ToString() ?? string.Empty,
                Email = reader["Email"]?.ToString(),
                Phone = reader["PhoneNumber"]?.ToString(),
                Description = reader["Description"]?.ToString(),
                ExistingLogoPath = reader["Logo"]?.ToString(),
                Address = reader["Address"]?.ToString()
            };
        }

        await using (var deleteCommand = new SqlCommand(deleteQuery, connection, transaction))
        {
            deleteCommand.Parameters.Add("@id", SqlDbType.Int).Value = id;
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var auditDetail = $"Deleted tenant '{existingTenant.TenantName}' (ID: {id}) by '{username}'.";
        await InsertAuditAsync(connection, transaction, userId, DeleteTenantAuditAction, auditDetail, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static TenantListItemViewModel MapTenantListItem(SqlDataReader reader)
    {
        return new TenantListItemViewModel
        {
            Id = reader["ID"] is int id ? id : 0,
            TenantName = reader["TenantName"]?.ToString() ?? string.Empty,
            Email = reader["Email"]?.ToString(),
            Phone = reader["PhoneNumber"]?.ToString(),
            Description = reader["Description"]?.ToString(),
            Logo = reader["Logo"]?.ToString(),
            Address = reader["Address"]?.ToString(),
            CreatedDate = reader["Created_Date"] as DateTime?,
            CreatedBy = reader["Created_By"]?.ToString(),
            UpdatedDate = reader["Updated_Date"] as DateTime?,
            UpdatedBy = reader["Updated_By"]?.ToString()
        };
    }

    private static string BuildUpdateAuditDetail(TenantFormViewModel existingTenant, TenantFormViewModel updatedTenant)
    {
        var changedFields = new List<string>();

        if (!string.Equals(NormalizeOptionalValue(existingTenant.TenantName), NormalizeOptionalValue(updatedTenant.TenantName), StringComparison.Ordinal))
        {
            changedFields.Add("TenantName");
        }

        if (!string.Equals(NormalizeOptionalValue(existingTenant.Email), NormalizeOptionalValue(updatedTenant.Email), StringComparison.OrdinalIgnoreCase))
        {
            changedFields.Add("Email");
        }

        if (!string.Equals(NormalizeOptionalValue(existingTenant.Phone), NormalizeOptionalValue(updatedTenant.Phone), StringComparison.Ordinal))
        {
            changedFields.Add("PhoneNumber");
        }

        if (!string.Equals(NormalizeOptionalValue(existingTenant.Description), NormalizeOptionalValue(updatedTenant.Description), StringComparison.Ordinal))
        {
            changedFields.Add("Description");
        }

        if (!string.Equals(NormalizeOptionalValue(existingTenant.ExistingLogoPath), NormalizeOptionalValue(updatedTenant.ExistingLogoPath), StringComparison.OrdinalIgnoreCase))
        {
            changedFields.Add("Logo");
        }

        if (!string.Equals(NormalizeOptionalValue(existingTenant.Address), NormalizeOptionalValue(updatedTenant.Address), StringComparison.Ordinal))
        {
            changedFields.Add("Address");
        }

        return changedFields.Count == 0
            ? $"Updated tenant '{updatedTenant.TenantName}' (ID: {updatedTenant.Id}). No field changes detected."
            : $"Updated tenant '{updatedTenant.TenantName}' (ID: {updatedTenant.Id}). Changed fields: {string.Join(", ", changedFields)}.";
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
        command.Parameters.AddWithValue("@userId", (object?)userId ?? DBNull.Value);
        command.Parameters.Add("@logAction", SqlDbType.NVarChar, 100).Value = logAction;
        command.Parameters.Add("@logDetail", SqlDbType.NVarChar, -1).Value = logDetail;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? NormalizeOptionalValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
