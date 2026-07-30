using Microsoft.Extensions.Options;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public sealed class KvhBulkSyncWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<KvhBulkSyncOptions> options,
    ILogger<KvhBulkSyncWorker> logger) : BackgroundService
{
    private readonly KvhBulkSyncOptions settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled)
        {
            logger.LogInformation("KVH bulk sync worker is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IKvhBulkSyncService>();
                await service.ProcessPendingItemsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "KVH bulk sync worker cycle failed.");
            }

            var intervalSeconds = Math.Clamp(settings.WorkerIntervalSeconds, 5, 60);
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }
    }
}
