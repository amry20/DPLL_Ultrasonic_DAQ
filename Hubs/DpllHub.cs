using DPLL_Ultrasonic_DAQ.Models;
using DPLL_Ultrasonic_DAQ.Services;
using Microsoft.AspNetCore.SignalR;

namespace DPLL_Ultrasonic_DAQ.Hubs;

/// <summary>
/// SignalR hub exposing the live DPLL telemetry stream and control commands
/// to the web UI. All firmware traffic is funneled through
/// <see cref="SerialDeviceService"/> so a single connection owns the COM port.
/// </summary>
public class DpllHub : Hub
{
    private readonly SerialDeviceService _device;
    private readonly CsvLoggerService _logger;

    public DpllHub(SerialDeviceService device, CsvLoggerService logger)
    {
        _device = device;
        _logger = logger;
    }

    /// <summary>
    /// Begin CSV logging. Returns the absolute path of the created file, or
    /// null if a recording is already in progress.
    /// </summary>
    public string? StartLogging()
    {
        if (_device.State != DeviceConnectionState.Connected) return null;
        return _logger.Start() ? _logger.FilePath : null;
    }

    /// <summary>
    /// Stop CSV logging. Returns the absolute path of the closed file, or
    /// null if no recording was active.
    /// </summary>
    public string? StopLogging()
    {
        var path = _logger.Stop();
        return path;
    }

    /// <summary>Current logging state (active flag, file path, row count).</summary>
    public object GetLoggingStatus() => new
    {
        Active = _logger.IsLogging,
        File = _logger.FilePath,
        Rows = _logger.RowCount,
        StartedAt = _logger.StartedAt.ToString("O")
    };

    public override Task OnConnectedAsync()
    {
        // Push the current snapshot to the new client immediately.
        if (_device.Latest is { } t)
        {
            Clients.Caller.SendAsync("Telemetry", t);
        }
        if (_device.Configuration is { } c)
        {
            Clients.Caller.SendAsync("Configuration", c);
        }
        Clients.Caller.SendAsync("ConnectionState", (int)_device.State, _device.PortName);
        return base.OnConnectedAsync();
    }

    /// <summary>Enable/disable the 100 Hz firmware telemetry stream.</summary>
    public void SetStreamEnabled(bool enabled) => _device.EnableStream(enabled);

    /// <summary>Apply configuration changes (non-null fields only).</summary>
    public void ApplyConfiguration(DpllConfigurationPatch patch) => _device.ApplyConfiguration(patch);

    /// <summary>Reset the control loop (clear integrator, restart from center).</summary>
    public void ResetLoop() => _device.ResetLoop();

    /// <summary>Shut down the control loop (DAC to 0 V).</summary>
    public void ShutdownLoop() => _device.ShutdownLoop();

    /// <summary>Set manual DAC voltage (disables the loop).</summary>
    public void SetManualVoltage(double volts) => _device.SetManualVoltage(volts);

    /// <summary>Re-enable automatic control (manual mode off).</summary>
    public void RunLoop() => _device.RunLoop();

    /// <summary>Ask the firmware to re-dump its configuration.</summary>
    public void RefreshConfiguration() => _device.RefreshConfiguration();
}
