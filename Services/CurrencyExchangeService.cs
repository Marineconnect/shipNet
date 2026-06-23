using System.Data;
using Microsoft.Data.SqlClient;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public class CurrencyExchangeService(IConfiguration configuration) : ICurrencyExchangeService
{
    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing connection string: DefaultConnection");

    private bool _schemaEnsured;

    public async Task<CurrencyExchangePageResult> GetRatesAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        const string countQuery = "SELECT COUNT(1) FROM [dbo].[TblCurrencyExchangeRate]";
        const string listQuery = """
            SELECT [ID], [FromCurrency], [ToCurrency], [Rate], [EffectiveDate], [Status], [Updated_Date], [Updated_By]
            FROM [dbo].[TblCurrencyExchangeRate]
            ORDER BY [EffectiveDate] DESC, [FromCurrency] ASC, [ToCurrency] ASC, [ID] DESC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            """;

        var normalizedPageSize = pageSize <= 0 ? 10 : pageSize;
        var normalizedPage = page <= 0 ? 1 : page;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);

        int totalRates;
        await using (var countCommand = new SqlCommand(countQuery, connection))
        {
            totalRates = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        }

        var totalPages = totalRates == 0 ? 1 : (int)Math.Ceiling(totalRates / (double)normalizedPageSize);
        var clampedPage = Math.Min(normalizedPage, totalPages);
        var offset = (clampedPage - 1) * normalizedPageSize;

        var rates = new List<CurrencyExchangeRateViewModel>();
        await using (var command = new SqlCommand(listQuery, connection))
        {
            command.Parameters.Add("@offset", SqlDbType.Int).Value = offset;
            command.Parameters.Add("@pageSize", SqlDbType.Int).Value = normalizedPageSize;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rates.Add(MapRate(reader));
            }
        }

        return new CurrencyExchangePageResult
        {
            Rates = rates,
            CurrentPage = clampedPage,
            PageSize = normalizedPageSize,
            TotalRates = totalRates
        };
    }

    public async Task<CurrencyExchangeRateFormViewModel?> GetRateByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT TOP 1 [ID], [FromCurrency], [ToCurrency], [Rate], [EffectiveDate], [Status]
            FROM [dbo].[TblCurrencyExchangeRate]
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

        return new CurrencyExchangeRateFormViewModel
        {
            Id = ReadInt(reader, "ID"),
            FromCurrency = reader["FromCurrency"]?.ToString() ?? string.Empty,
            ToCurrency = reader["ToCurrency"]?.ToString() ?? string.Empty,
            Rate = ReadDecimal(reader, "Rate"),
            EffectiveDate = ReadDate(reader, "EffectiveDate") ?? DateTime.Today,
            Status = reader["Status"]?.ToString() ?? "active"
        };
    }

    public async Task<bool> IsRateInUseAsync(string fromCurrency, string toCurrency, DateTime effectiveDate, int? excludeId = null, CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT TOP 1 1
            FROM [dbo].[TblCurrencyExchangeRate]
            WHERE [FromCurrency] = @fromCurrency
              AND [ToCurrency] = @toCurrency
              AND CONVERT(date, [EffectiveDate]) = CONVERT(date, @effectiveDate)
              AND (@excludeId IS NULL OR [ID] <> @excludeId)
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@fromCurrency", SqlDbType.NVarChar, 10).Value = NormalizeCurrency(fromCurrency);
        command.Parameters.Add("@toCurrency", SqlDbType.NVarChar, 10).Value = NormalizeCurrency(toCurrency);
        command.Parameters.Add("@effectiveDate", SqlDbType.Date).Value = effectiveDate.Date;
        command.Parameters.Add("@excludeId", SqlDbType.Int).Value = (object?)excludeId ?? DBNull.Value;

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value;
    }

    public async Task<int> CreateRateAsync(CurrencyExchangeRateFormViewModel model, int? userId, string username, CancellationToken cancellationToken = default)
    {
        const string query = """
            INSERT INTO [dbo].[TblCurrencyExchangeRate]
                ([FromCurrency], [ToCurrency], [Rate], [EffectiveDate], [Status], [Created_Date], [Created_By], [Updated_Date], [Updated_By])
            OUTPUT INSERTED.[ID]
            VALUES
                (@fromCurrency, @toCurrency, @rate, @effectiveDate, @status, GETDATE(), @createdBy, GETDATE(), @updatedBy)
            """;

        NormalizeRate(model);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        await using var command = new SqlCommand(query, connection, transaction);
        AddRateParameters(command, model);
        command.Parameters.Add("@createdBy", SqlDbType.NVarChar, 100).Value = username;
        command.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 100).Value = username;

        var id = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        await InsertAuditAsync(connection, transaction, userId, "created_currency_exchange_rate", $"Created currency rate {model.FromCurrency}->{model.ToCurrency} effective {model.EffectiveDate:yyyy-MM-dd}.", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task UpdateRateAsync(CurrencyExchangeRateFormViewModel model, int? userId, string username, CancellationToken cancellationToken = default)
    {
        const string query = """
            UPDATE [dbo].[TblCurrencyExchangeRate]
            SET [FromCurrency] = @fromCurrency,
                [ToCurrency] = @toCurrency,
                [Rate] = @rate,
                [EffectiveDate] = @effectiveDate,
                [Status] = @status,
                [Updated_Date] = GETDATE(),
                [Updated_By] = @updatedBy
            WHERE [ID] = @id
            """;

        NormalizeRate(model);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        await using var command = new SqlCommand(query, connection, transaction);
        AddRateParameters(command, model);
        command.Parameters.Add("@id", SqlDbType.Int).Value = model.Id;
        command.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 100).Value = username;

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            throw new KeyNotFoundException("Currency exchange rate not found.");
        }

        await InsertAuditAsync(connection, transaction, userId, "updated_currency_exchange_rate", $"Updated currency rate #{model.Id}.", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteRateAsync(int id, int? userId, string username, CancellationToken cancellationToken = default)
    {
        const string query = "DELETE FROM [dbo].[TblCurrencyExchangeRate] WHERE [ID] = @id";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            throw new KeyNotFoundException("Currency exchange rate not found.");
        }

        await InsertAuditAsync(connection, transaction, userId, "deleted_currency_exchange_rate", $"Deleted currency rate #{id} by '{username}'.", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<CurrencyConversionResultViewModel?> ConvertAsync(CurrencyConversionFormViewModel model, CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT TOP 1 [FromCurrency], [ToCurrency], [Rate], [EffectiveDate]
            FROM [dbo].[TblCurrencyExchangeRate]
            WHERE [FromCurrency] = @fromCurrency
              AND [ToCurrency] = @toCurrency
              AND LOWER([Status]) = N'active'
              AND CONVERT(date, [EffectiveDate]) <= CONVERT(date, @conversionDate)
            ORDER BY [EffectiveDate] DESC, [ID] DESC
            """;

        var fromCurrency = NormalizeCurrency(model.FromCurrency);
        var toCurrency = NormalizeCurrency(model.ToCurrency);
        if (string.Equals(fromCurrency, toCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return new CurrencyConversionResultViewModel
            {
                Amount = model.Amount,
                FromCurrency = fromCurrency,
                ToCurrency = toCurrency,
                Rate = 1,
                EffectiveDate = model.ConversionDate.Date,
                ConvertedAmount = model.Amount
            };
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, null, cancellationToken);

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@fromCurrency", SqlDbType.NVarChar, 10).Value = fromCurrency;
        command.Parameters.Add("@toCurrency", SqlDbType.NVarChar, 10).Value = toCurrency;
        command.Parameters.Add("@conversionDate", SqlDbType.Date).Value = model.ConversionDate.Date;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var rate = ReadDecimal(reader, "Rate");
        return new CurrencyConversionResultViewModel
        {
            Amount = model.Amount,
            FromCurrency = reader["FromCurrency"]?.ToString() ?? fromCurrency,
            ToCurrency = reader["ToCurrency"]?.ToString() ?? toCurrency,
            Rate = rate,
            EffectiveDate = ReadDate(reader, "EffectiveDate") ?? model.ConversionDate.Date,
            ConvertedAmount = model.Amount * rate
        };
    }

    private async Task EnsureSchemaAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        if (_schemaEnsured)
        {
            return;
        }

        const string query = """
            IF OBJECT_ID(N'[dbo].[TblCurrencyExchangeRate]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[TblCurrencyExchangeRate](
                    [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblCurrencyExchangeRate] PRIMARY KEY,
                    [FromCurrency] nvarchar(10) NOT NULL,
                    [ToCurrency] nvarchar(10) NOT NULL,
                    [Rate] decimal(18,6) NOT NULL,
                    [EffectiveDate] date NOT NULL,
                    [Status] nvarchar(50) NOT NULL CONSTRAINT [DF_TblCurrencyExchangeRate_Status] DEFAULT N'active',
                    [Created_Date] datetime NULL,
                    [Created_By] nvarchar(100) NULL,
                    [Updated_Date] datetime NULL,
                    [Updated_By] nvarchar(100) NULL
                );
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE [name] = N'UX_TblCurrencyExchangeRate_PairDate'
                  AND [object_id] = OBJECT_ID(N'[dbo].[TblCurrencyExchangeRate]')
            )
            BEGIN
                CREATE UNIQUE INDEX [UX_TblCurrencyExchangeRate_PairDate]
                ON [dbo].[TblCurrencyExchangeRate]([FromCurrency], [ToCurrency], [EffectiveDate]);
            END;
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        _schemaEnsured = true;
    }

    private static void AddRateParameters(SqlCommand command, CurrencyExchangeRateFormViewModel model)
    {
        command.Parameters.Add("@fromCurrency", SqlDbType.NVarChar, 10).Value = model.FromCurrency;
        command.Parameters.Add("@toCurrency", SqlDbType.NVarChar, 10).Value = model.ToCurrency;
        command.Parameters.Add("@rate", SqlDbType.Decimal).Value = model.Rate;
        command.Parameters["@rate"].Precision = 18;
        command.Parameters["@rate"].Scale = 6;
        command.Parameters.Add("@effectiveDate", SqlDbType.Date).Value = model.EffectiveDate.Date;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 50).Value = model.Status;
    }

    private static CurrencyExchangeRateViewModel MapRate(SqlDataReader reader)
    {
        return new CurrencyExchangeRateViewModel
        {
            Id = ReadInt(reader, "ID"),
            FromCurrency = reader["FromCurrency"]?.ToString() ?? string.Empty,
            ToCurrency = reader["ToCurrency"]?.ToString() ?? string.Empty,
            Rate = ReadDecimal(reader, "Rate"),
            EffectiveDate = ReadDate(reader, "EffectiveDate") ?? DateTime.MinValue,
            Status = reader["Status"]?.ToString() ?? "active",
            UpdatedDate = ReadDate(reader, "Updated_Date"),
            UpdatedBy = reader["Updated_By"]?.ToString()
        };
    }

    private static void NormalizeRate(CurrencyExchangeRateFormViewModel model)
    {
        model.FromCurrency = NormalizeCurrency(model.FromCurrency);
        model.ToCurrency = NormalizeCurrency(model.ToCurrency);
        model.EffectiveDate = model.EffectiveDate.Date;
        model.Status = string.IsNullOrWhiteSpace(model.Status) ? "active" : model.Status.Trim().ToLowerInvariant();
    }

    private static string NormalizeCurrency(string value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static int ReadInt(SqlDataReader reader, string columnName)
    {
        var value = reader[columnName];
        return value is int number ? number : Convert.ToInt32(value);
    }

    private static decimal ReadDecimal(SqlDataReader reader, string columnName)
    {
        var value = reader[columnName];
        return value == DBNull.Value ? 0 : Convert.ToDecimal(value);
    }

    private static DateTime? ReadDate(SqlDataReader reader, string columnName)
    {
        var value = reader[columnName];
        return value == DBNull.Value ? null : Convert.ToDateTime(value);
    }

    private static async Task InsertAuditAsync(SqlConnection connection, SqlTransaction transaction, int? userId, string action, string detail, CancellationToken cancellationToken)
    {
        const string query = """
            IF OBJECT_ID(N'[dbo].[TblAudit]', N'U') IS NOT NULL
            BEGIN
                INSERT INTO [dbo].[TblAudit]
                    ([IDUser], [LogDate], [LogAction], [LogDetail], [IDDevice])
                VALUES
                    (@userId, GETDATE(), @action, @detail, @deviceId)
            END
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@userId", SqlDbType.Int).Value = (object?)userId ?? DBNull.Value;
        command.Parameters.Add("@action", SqlDbType.NVarChar, 100).Value = action;
        command.Parameters.Add("@detail", SqlDbType.NVarChar, -1).Value = detail;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
