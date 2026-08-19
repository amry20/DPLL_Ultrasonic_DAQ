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

    public DpllHub(SerialDeviceService device)
    {
        _device = device;
    }

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
        Clients.Caller.SendAsync("ConnectionState", (int)_device.State, _device.PortName, _device.ControlPortName);
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
