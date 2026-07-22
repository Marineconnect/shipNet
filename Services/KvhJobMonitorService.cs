using Microsoft.Extensions.Options;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public sealed class KvhJobMonitorService(
    IServiceScopeFactory scopeFactory,
    IOptions<KvhJobMonitorOptions> options,
    ILogger<KvhJobMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("KVH job monitor is disabled by configuration.");
            return;
        }

        logger.LogInformation("KVH job monitor started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var jobService = scope.ServiceProvider.GetRequiredService<IKvhJobService>();
                var commands = await jobService.ClaimCommandsForPollingAsync(Math.Max(1, options.Value.BatchSize), stoppingToken);
                foreach (var command in commands)
                {
                    await jobService.PollCommandAsync(command, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "KVH job monitor loop failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.WorkerIntervalSeconds)), stoppingToken);
        }
    }
}
