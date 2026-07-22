using System.Data;
using Microsoft.Data.SqlClient;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public class SystemSettingsService(IConfiguration configuration) : ISystemSettingsService
{
    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing connection string: DefaultConnection");

    private bool _schemaEnsured;

    private static readonly SystemSettingSeed[] SeedSettings =
    [
        new("System", "system_default_currency", "System default currency", "VND", false, "Default reference currency used by billing and payment calculations."),
        new("9Pay", "ninepay_transaction_fee_vnd", "9Pay transaction fee (VND)", "4400", false, "Transaction fee added to the QR payment total."),
        new("9Pay", "ninepay_qr_expire_hours", "9Pay QR expiry hours", "72", false, "Number of hours a generated 9Pay QR remains valid."),
        new("Invoice", "invoice_po_number", "Invoice PO number", "", false, "Optional PO number included in invoice messages sent to RabbitMQ."),
        new("Invoice", "invoice_sequence_start", "Invoice sequence start", "00236", false, "Starting number for invoice codes. The sequence is padded to 5 digits.")
    ];

    public async Task<List<SystemSettingViewModel>> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT [ID], [Category], [SettingCode], [DisplayName], [SettingValue], [IsSecret], [Description], [Updated_Date], [Updated_By]
            FROM [dbo].[TblSystemSetting]
            WHERE [SettingCode] IN (N'ninepay_transaction_fee_vnd', N'system_default_currency', N'ninepay_qr_expire_hours', N'invoice_po_number', N'invoice_sequence_start')
            ORDER BY [Category] ASC, [DisplayOrder] ASC, [ID] ASC
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);

        var settings = new List<SystemSettingViewModel>();
        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            settings.Add(MapSetting(reader));
        }

        return settings;
    }

    public async Task<SystemSettingFormViewModel?> GetSettingByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT TOP 1 [ID], [Category], [SettingCode], [DisplayName], [SettingValue], [IsSecret]
            FROM [dbo].[TblSystemSetting]
            WHERE [ID] = @id
              AND [SettingCode] IN (N'ninepay_transaction_fee_vnd', N'system_default_currency', N'ninepay_qr_expire_hours', N'invoice_po_number', N'invoice_sequence_start')
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

        return new SystemSettingFormViewModel
        {
            Id = ReadInt(reader, "ID"),
            Category = reader["Category"]?.ToString() ?? string.Empty,
            SettingCode = reader["SettingCode"]?.ToString() ?? string.Empty,
            DisplayName = reader["DisplayName"]?.ToString() ?? string.Empty,
            SettingValue = reader["SettingValue"]?.ToString() ?? string.Empty,
            IsSecret = ReadBool(reader, "IsSecret")
        };
    }

    public async Task UpdateSettingAsync(SystemSettingFormViewModel model, int? userId, string username, CancellationToken cancellationToken = default)
    {
        const string query = """
            UPDATE [dbo].[TblSystemSetting]
            SET [SettingValue] = @settingValue,
                [Updated_Date] = GETDATE(),
                [Updated_By] = @updatedBy
            WHERE [ID] = @id
              AND [SettingCode] IN (N'ninepay_transaction_fee_vnd', N'system_default_currency', N'ninepay_qr_expire_hours', N'invoice_po_number', N'invoice_sequence_start')
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@id", SqlDbType.Int).Value = model.Id;
        command.Parameters.Add("@settingValue", SqlDbType.NVarChar, 2000).Value = model.SettingValue?.Trim() ?? string.Empty;
        command.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 100).Value = username;
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            throw new KeyNotFoundException("System setting not found.");
        }

        await InsertAuditAsync(connection, transaction, userId, "updated_system_setting", $"Updated system setting '{model.SettingCode}' by '{username}'.", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<Dictionary<string, string>> GetSettingsByCodesAsync(IEnumerable<string> settingCodes, CancellationToken cancellationToken = default)
    {
        var codes = settingCodes.Select(code => code.Trim()).Where(code => !string.IsNullOrWhiteSpace(code)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (codes.Count == 0)
        {
            return [];
        }

        var parameterNames = codes.Select((_, index) => $"@code{index}").ToList();
        var query = $"""
            SELECT [SettingCode], [SettingValue]
            FROM [dbo].[TblSystemSetting]
            WHERE [SettingCode] IN ({string.Join(",", parameterNames)})
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);

        await using var command = new SqlCommand(query, connection);
        for (var i = 0; i < codes.Count; i++)
        {
            command.Parameters.Add(parameterNames[i], SqlDbType.NVarChar, 100).Value = codes[i];
        }

        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            settings[reader["SettingCode"]?.ToString() ?? string.Empty] = reader["SettingValue"]?.ToString() ?? string.Empty;
        }

        return settings;
    }

    private async Task EnsureSchemaAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        if (_schemaEnsured)
        {
            return;
        }

        const string query = """
            IF OBJECT_ID(N'[dbo].[TblSystemSetting]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[TblSystemSetting](
                    [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblSystemSetting] PRIMARY KEY,
                    [Category] nvarchar(100) NOT NULL,
                    [SettingCode] nvarchar(100) NOT NULL,
                    [DisplayName] nvarchar(250) NOT NULL,
                    [SettingValue] nvarchar(2000) NOT NULL CONSTRAINT [DF_TblSystemSetting_SettingValue] DEFAULT N'',
                    [IsSecret] bit NOT NULL CONSTRAINT [DF_TblSystemSetting_IsSecret] DEFAULT 0,
                    [Description] nvarchar(500) NULL,
                    [DisplayOrder] int NOT NULL CONSTRAINT [DF_TblSystemSetting_DisplayOrder] DEFAULT 0,
                    [Created_Date] datetime NULL,
                    [Created_By] nvarchar(100) NULL,
                    [Updated_Date] datetime NULL,
                    [Updated_By] nvarchar(100) NULL
                );
                CREATE UNIQUE INDEX [UX_TblSystemSetting_SettingCode] ON [dbo].[TblSystemSetting]([SettingCode]);
            END
            """;

        await using (var command = new SqlCommand(query, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var index = 0; index < SeedSettings.Length; index++)
        {
            await UpsertSeedAsync(connection, transaction, SeedSettings[index], index + 1, cancellationToken);
        }

        _schemaEnsured = true;
    }

    private static async Task UpsertSeedAsync(SqlConnection connection, SqlTransaction? transaction, SystemSettingSeed seed, int displayOrder, CancellationToken cancellationToken)
    {
        const string query = """
            IF NOT EXISTS (SELECT 1 FROM [dbo].[TblSystemSetting] WHERE [SettingCode] = @settingCode)
            BEGIN
                INSERT INTO [dbo].[TblSystemSetting]
                    ([Category], [SettingCode], [DisplayName], [SettingValue], [IsSecret], [Description], [DisplayOrder], [Created_Date], [Created_By], [Updated_Date], [Updated_By])
                VALUES
                    (@category, @settingCode, @displayName, @settingValue, @isSecret, @description, @displayOrder, GETDATE(), N'system', GETDATE(), N'system')
            END
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@category", SqlDbType.NVarChar, 100).Value = seed.Category;
        command.Parameters.Add("@settingCode", SqlDbType.NVarChar, 100).Value = seed.SettingCode;
        command.Parameters.Add("@displayName", SqlDbType.NVarChar, 250).Value = seed.DisplayName;
        command.Parameters.Add("@settingValue", SqlDbType.NVarChar, 2000).Value = seed.SettingValue;
        command.Parameters.Add("@isSecret", SqlDbType.Bit).Value = seed.IsSecret;
        command.Parameters.Add("@description", SqlDbType.NVarChar, 500).Value = seed.Description;
        command.Parameters.Add("@displayOrder", SqlDbType.Int).Value = displayOrder;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SystemSettingViewModel MapSetting(SqlDataReader reader)
    {
        return new SystemSettingViewModel
        {
            Id = ReadInt(reader, "ID"),
            Category = reader["Category"]?.ToString() ?? string.Empty,
            SettingCode = reader["SettingCode"]?.ToString() ?? string.Empty,
            DisplayName = reader["DisplayName"]?.ToString() ?? string.Empty,
            SettingValue = reader["SettingValue"]?.ToString() ?? string.Empty,
            IsSecret = ReadBool(reader, "IsSecret"),
            Description = reader["Description"]?.ToString() ?? string.Empty,
            UpdatedDate = ReadDate(reader, "Updated_Date"),
            UpdatedBy = reader["Updated_By"]?.ToString()
        };
    }

    private static int ReadInt(SqlDataReader reader, string columnName) => reader[columnName] is int value ? value : Convert.ToInt32(reader[columnName]);
    private static bool ReadBool(SqlDataReader reader, string columnName) => reader[columnName] is bool value && value;
    private static DateTime? ReadDate(SqlDataReader reader, string columnName) => reader[columnName] == DBNull.Value ? null : Convert.ToDateTime(reader[columnName]);

    private static async Task InsertAuditAsync(SqlConnection connection, SqlTransaction transaction, int? userId, string action, string detail, CancellationToken cancellationToken)
    {
        const string query = """
            IF OBJECT_ID(N'[dbo].[TblAudit]', N'U') IS NOT NULL
            BEGIN
                INSERT INTO [dbo].[TblAudit] ([IDUser], [LogDate], [LogAction], [LogDetail], [IDDevice])
                VALUES (@userId, GETDATE(), @action, @detail, @deviceId)
            END
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@userId", SqlDbType.Int).Value = (object?)userId ?? DBNull.Value;
        command.Parameters.Add("@action", SqlDbType.NVarChar, 100).Value = action;
        command.Parameters.Add("@detail", SqlDbType.NVarChar, -1).Value = detail;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record SystemSettingSeed(string Category, string SettingCode, string DisplayName, string SettingValue, bool IsSecret, string Description);
}
