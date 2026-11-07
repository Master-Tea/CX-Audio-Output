using System.Net.NetworkInformation;

namespace CXAudioOutput;

public sealed class HardwareMonitorService : BackgroundService
{
    private readonly NetworkSelectionService _network;
    private readonly ILogger<HardwareMonitorService> _logger;
    private readonly NetworkAddressChangedEventHandler _addressChanged;
    private readonly NetworkAvailabilityChangedEventHandler _availabilityChanged;

    public HardwareMonitorService(NetworkSelectionService network, ILogger<HardwareMonitorService> logger)
    {
        _network = network;
        _logger = logger;
        _addressChanged = (_, _) => RefreshFromEvent();
        _availabilityChanged = (_, _) => RefreshFromEvent();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        NetworkChange.NetworkAddressChanged += _addressChanged;
        NetworkChange.NetworkAvailabilityChanged += _availabilityChanged;

        try
        {
            _network.Refresh();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _network.Refresh();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hardware monitor stopped unexpectedly");
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= _addressChanged;
            NetworkChange.NetworkAvailabilityChanged -= _availabilityChanged;
        }
    }

    private void RefreshFromEvent()
    {
        try
        {
            _network.Refresh();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Network change refresh failed");
        }
    }

    public override void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= _addressChanged;
        NetworkChange.NetworkAvailabilityChanged -= _availabilityChanged;
        base.Dispose();
    }
}
