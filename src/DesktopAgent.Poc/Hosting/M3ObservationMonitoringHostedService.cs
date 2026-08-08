using DesktopAgent.Application.DesktopObservations;
using Microsoft.Extensions.Hosting;

namespace DesktopAgent.Poc.Hosting;

/// <summary>
/// Generic Host 与 M3 Application runtime 之间的薄生命周期适配器。
/// 不承载 debounce / observation / diff 逻辑，也不持有 Writer。
/// </summary>
public sealed class M3ObservationMonitoringHostedService(
    DesktopObservationMonitoringRuntime runtime) : IHostedService
{
    private readonly DesktopObservationMonitoringRuntime _runtime =
        runtime ?? throw new ArgumentNullException(nameof(runtime));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _runtime.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        _runtime.StopAsync(cancellationToken);
}
