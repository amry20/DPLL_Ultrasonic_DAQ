namespace DPLL_Ultrasonic_DAQ.Models;

/// <summary>
/// DPLL configuration as reported by the firmware. Mirrors the values
/// printed by the debug <c>gain</c> command and the opcode getters.
/// </summary>
public sealed class DpllConfiguration
{
    /// <summary>Proportional gain, V/ns.</summary>
    public double Kp { get; set; }

    /// <summary>Integral gain, V/ns/s.</summary>
    public double Ki { get; set; }

    /// <summary>Derivative gain, V/ns/s (0 = disabled).</summary>
    public double Kd { get; set; }

    /// <summary>Center voltage, volts.</summary>
    public double CenterVoltage { get; set; }

    /// <summary>Target phase setpoint, ns.</summary>
    public double TargetPhase { get; set; }

    /// <summary>Max DAC slew rate, V/s.</summary>
    public double MaxSlew { get; set; }

    /// <summary>Manual mode flag (loop disengaged, DAC held by user).</summary>
    public bool ManualMode { get; set; }

    /// <summary>Control loop period, ms.</summary>
    public uint LoopPeriodMs { get; set; }

    /// <summary>Lock threshold, ns.</summary>
    public double LockThresholdNs { get; set; }

    /// <summary>Consecutive LOCK cycles required before committing the lock-point voltage. Default 10.</summary>
    public uint LockHoldCycles { get; set; }

    /// <summary>Lock memory expiry in ms (0 = never). Default 5000.</summary>
    public uint LockMemoryTimeoutMs { get; set; }

    /// <summary>Monitor stream period in ms (1–65535). Default 100 ms = 10 Hz.</summary>
    public uint StreamPeriodMs { get; set; }

    /// <summary>Last committed lock-point voltage, volts.</summary>
    public double LockedCenterV { get; set; }

    /// <summary>True if a stable lock-point has been committed.</summary>
    public bool HaveLockedCenter { get; set; }

    /// <summary>Signal-loss DAC behaviour: 0=freeze, 1=center, 2=zero.</summary>
    public int SignalLossBehavior { get; set; }

    /// <summary>Human-readable signal-loss behaviour name.</summary>
    public string SignalLossName => SignalLossBehavior switch
    {
        0 => "Freeze",
        1 => "Center",
        2 => "Zero",
        _ => "Unknown"
    };
}
