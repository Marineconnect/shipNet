using System.Data;
using Microsoft.Data.SqlClient;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public sealed class DashboardKpiService(IConfiguration configuration) : IDashboardKpiService
{
    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing DefaultConnection connection string.");

    public async Task<DashboardKpiViewModel> GetKpiAsync(int month, int year, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default)
    {
        var normalizedYear = year is >= 2000 and <= 2100 ? year : DateTime.Now.Year;
        var normalizedMonth = month is >= 0 and <= 12 ? month : DateTime.Now.Month;
        var periodStart = normalizedMonth == 0
            ? new DateTime(normalizedYear, 1, 1)
            : new DateTime(normalizedYear, normalizedMonth, 1);
        var periodEndExclusive = normalizedMonth == 0
            ? periodStart.AddYears(1)
            : periodStart.AddMonths(1);

        var model = new DashboardKpiViewModel
        {
            Month = normalizedMonth,
            Year = normalizedYear,
            PeriodStart = periodStart,
            PeriodEnd = periodEndExclusive.AddDays(-1)
        };

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await BillingTablesExistAsync(connection, cancellationToken))
        {
            model.Years = BuildFallbackYears(normalizedYear);
            return model;
        }

        const string summarySql = """
            ;WITH ScopedSubscriptions AS
            (
                SELECT s.[ID], s.[TenantId], s.[DeviceId], s.[Status]
                FROM [dbo].[TblMonthlySubscription] s
                WHERE s.[UsageMonth] >= @periodStart
                  AND s.[UsageMonth] < @periodEndExclusive
                  AND (@allowedTenantId IS NULL OR s.[TenantId] = @allowedTenantId)
                  AND (@allowedDeviceId IS NULL OR s.[DeviceId] = @allowedDeviceId)
            ),
            ValidInvoices AS
            (
                SELECT i.[ID], i.[SubscriptionId], i.[Amount], i.[SalePrice], i.[BuyPrice], i.[MarginAmount]
                FROM [dbo].[TblSubscriptionInvoice] i
                INNER JOIN ScopedSubscriptions s ON s.[ID] = i.[SubscriptionId]
                WHERE LOWER(COALESCE(i.[Status], N'')) NOT IN (N'void', N'cancelled', N'canceled', N'refunded')
            )
            SELECT
                COALESCE(SUM(v.[Amount]), 0) AS [TotalRevenue],
                COALESCE(SUM(COALESCE(NULLIF(v.[MarginAmount], 0), v.[SalePrice] - v.[BuyPrice])), 0) AS [TotalCommission],
                COUNT(DISTINCT CASE
                    WHEN LOWER(COALESCE(s.[Status], N'')) NOT IN (N'void', N'cancelled', N'canceled', N'inactive')
                    THEN s.[DeviceId]
                END) AS [ActiveKitCount],
                COUNT(DISTINCT CASE WHEN v.[ID] IS NOT NULL THEN s.[DeviceId] END) AS [BilledKitCount]
            FROM ScopedSubscriptions s
            LEFT JOIN ValidInvoices v ON v.[SubscriptionId] = s.[ID];
            """;

        await using (var command = new SqlCommand(summarySql, connection))
        {
            AddScopeParameters(command, periodStart, periodEndExclusive, allowedTenantId, allowedDeviceId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                model.TotalRevenue = ReadDecimal(reader, "TotalRevenue");
                model.TotalCommission = Math.Max(0, ReadDecimal(reader, "TotalCommission"));
                model.ActiveKitCount = ReadInt(reader, "ActiveKitCount");
                model.BilledKitCount = ReadInt(reader, "BilledKitCount");
            }
        }

        model.Years = await GetYearsAsync(connection, normalizedYear, allowedTenantId, allowedDeviceId, cancellationToken);
        return model;
    }

    private static async Task<bool> BillingTablesExistAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT CASE
                WHEN OBJECT_ID(N'[dbo].[TblMonthlySubscription]', N'U') IS NOT NULL
                 AND OBJECT_ID(N'[dbo].[TblSubscriptionInvoice]', N'U') IS NOT NULL
                THEN 1 ELSE 0 END
            """;
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task<List<int>> GetYearsAsync(SqlConnection connection, int selectedYear, int? allowedTenantId, int? allowedDeviceId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DISTINCT YEAR(s.[UsageMonth]) AS [Year]
            FROM [dbo].[TblMonthlySubscription] s
            WHERE (@allowedTenantId IS NULL OR s.[TenantId] = @allowedTenantId)
              AND (@allowedDeviceId IS NULL OR s.[DeviceId] = @allowedDeviceId)
            ORDER BY [Year] DESC;
            """;

        var years = new HashSet<int>(BuildFallbackYears(selectedYear));
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@allowedTenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        command.Parameters.Add("@allowedDeviceId", SqlDbType.Int).Value = (object?)allowedDeviceId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            years.Add(ReadInt(reader, "Year"));
        }

        return years.Where(year => year > 0).OrderByDescending(year => year).ToList();
    }

    private static List<int> BuildFallbackYears(int selectedYear)
    {
        var currentYear = DateTime.Now.Year;
        return new[] { selectedYear, currentYear, currentYear - 1, currentYear - 2 }
            .Where(year => year is >= 2000 and <= 2100)
            .Distinct()
            .OrderByDescending(year => year)
            .ToList();
    }

    private static void AddScopeParameters(SqlCommand command, DateTime periodStart, DateTime periodEndExclusive, int? allowedTenantId, int? allowedDeviceId)
    {
        command.Parameters.Add("@periodStart", SqlDbType.Date).Value = periodStart;
        command.Parameters.Add("@periodEndExclusive", SqlDbType.Date).Value = periodEndExclusive;
        command.Parameters.Add("@allowedTenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        command.Parameters.Add("@allowedDeviceId", SqlDbType.Int).Value = (object?)allowedDeviceId ?? DBNull.Value;
    }

    private static decimal ReadDecimal(SqlDataReader reader, string column)
    {
        var value = reader[column];
        return value == DBNull.Value ? 0 : Convert.ToDecimal(value);
    }

    private static int ReadInt(SqlDataReader reader, string column)
    {
        var value = reader[column];
        return value == DBNull.Value ? 0 : Convert.ToInt32(value);
    }
}
