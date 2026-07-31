using Microsoft.Extensions.Options;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public sealed class KvhSubscriptionOperationWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<KvhSubscriptionOperationOptions> options,
    ILogger<KvhSubscriptionOperationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("KVH subscription operation worker is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IKvhSubscriptionOperationService>();

                await service.SyncCommandStatusesAsync(stoppingToken);
                await service.MonitorWaitingEffectiveAsync(stoppingToken);
                var ids = await service.ClaimQueuedItemsAsync(Math.Max(1, options.Value.BatchSize), stoppingToken);
                foreach (var id in ids)
                {
                    await service.SubmitItemAsync(id, null, "KVH Operation Worker", stoppingToken);
                }
                await service.SyncCommandStatusesAsync(stoppingToken);
                await service.MonitorWaitingEffectiveAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "KVH subscription operation worker failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.WorkerIntervalSeconds)), stoppingToken);
        }
    }
}
