using System.Data;
using Microsoft.Data.SqlClient;

namespace StarlinkDeviceManager.Services;

public sealed class DeviceRefreshHostedService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<DeviceRefreshHostedService> logger) : BackgroundService
{
    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing connection string: DefaultConnection");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = Math.Max(5, configuration.GetValue<int?>("DeviceRefresh:IntervalMinutes") ?? 120);
        var batchSize = Math.Max(1, configuration.GetValue<int?>("DeviceRefresh:BatchSize") ?? 50);
        logger.LogInformation("Device refresh worker started with interval {IntervalMinutes} minutes.", intervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deviceIds = await GetDueDeviceIdsAsync(batchSize, stoppingToken);
                foreach (var deviceId in deviceIds)
                {
                    using var scope = scopeFactory.CreateScope();
                    var deviceService = scope.ServiceProvider.GetRequiredService<IDeviceService>();
                    await deviceService.RefreshExpiredDeviceAsync(deviceId, stoppingToken);
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Device refresh worker loop failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
        }
    }

    private async Task<IReadOnlyList<int>> GetDueDeviceIdsAsync(int batchSize, CancellationToken cancellationToken)
    {
        var ids = new List<int>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        const string query = """
            SELECT TOP (@batchSize) [ID]
            FROM [dbo].[TblDevices]
            WHERE NULLIF(LTRIM(RTRIM(ISNULL([DeviceCode], ''))), '') IS NOT NULL
              AND ([LastSysnTime] IS NULL OR [LastSysnTime] < DATEADD(minute, -120, SYSUTCDATETIME()))
            ORDER BY COALESCE([LastSysnTime], '19000101'), [ID]
            """;
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@batchSize", SqlDbType.Int).Value = batchSize;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(Convert.ToInt32(reader["ID"]));
        }

        return ids;
    }
}
