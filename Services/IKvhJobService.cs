using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface IKvhJobService
{
    Task<IReadOnlyList<KvhCommand>> ClaimCommandsForPollingAsync(int batchSize, CancellationToken cancellationToken = default);
    Task PollCommandAsync(KvhCommand command, CancellationToken cancellationToken = default);
}
