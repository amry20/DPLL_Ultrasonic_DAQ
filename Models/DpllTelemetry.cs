namespace DPLL_Ultrasonic_DAQ.Models;

/// <summary>
/// Mirror of the firmware <c>dpllStatusData</c> struct (16 bytes, packed).
/// Decoded from <see cref="Opcode.OPCODE_STREAM_DPLL_STATUS"/> packets.
/// </summary>
public sealed class DpllTelemetry
{
    /// <summary>Measured reference frequency in Hz.</summary>
    public double ReferenceFrequencyHz { get; set; }

    /// <summary>Phase difference ZCD vs REF in nanoseconds (last-valid value when <see cref="PhaseStale"/> = 1).</summary>
    public double PhaseErrorNs { get; set; }

    /// <summary>Current DAC output voltage in volts (0.0 – 3.3).</summary>
    public double DACVoltage_V { get; set; }

    /// <summary>0=NO_REF, 1=WAIT_ZCD, 2=TRACK, 3=LOCK.</summary>
    public int LockStatus { get; set; }

    /// <summary>0 = fresh measurement, 1 = ZCD absent, value is a held last-valid phase.</summary>
    public int PhaseStale { get; set; }

    /// <summary>Seconds since the Unix epoch (UTC) when this sample was captured by the host.</summary>
    public double Timestamp { get; set; }

    /// <summary>Human-readable lock state name.</summary>
    public string State => LockStatus switch
    {
        0 => "NO REF",
        1 => "WAIT ZCD",
        2 => "TRACK",
        3 => "LOCK",
        _ => "UNKNOWN"
    };

    /// <summary>True when the current lock status is LOCK.</summary>
    public bool IsLocked => LockStatus == 3;
}
