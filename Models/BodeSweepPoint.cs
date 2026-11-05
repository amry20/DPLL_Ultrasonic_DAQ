namespace DPLL_Ultrasonic_DAQ.Models;

/// <summary>
/// Single measurement point / status frame emitted by the firmware-driven
/// Phase Response (Bode Plot) sweep state machine (OPCODE_BODE_SWEEP_POINT = 0x0045).
/// </summary>
public sealed class BodeSweepPoint
{
    /// <summary>0 = COMPLETED/IDLE, 1 = RUNNING, 2 = ABORTED</summary>
    public byte State { get; set; }

    /// <summary>1 = valid ZCD phase captured, 0 = no ZCD</summary>
    public byte Valid { get; set; }

    /// <summary>1-based step index</summary>
    public ushort StepIndex { get; set; }

    /// <summary>Total steps in the sweep</summary>
    public ushort TotalSteps { get; set; }

    /// <summary>Number of accepted ZCD samples in the measurement window</summary>
    public ushort SampleCount { get; set; }

    /// <summary>Commanded generator frequency in Hz</summary>
    public float TargetFreqHz { get; set; }

    /// <summary>Measured reference frequency in Hz</summary>
    public float MeasuredFreqHz { get; set; }

    /// <summary>Measured phase difference (REF vs ZCD) in nanoseconds</summary>
    public float PhaseDiffNs { get; set; }

    /// <summary>Measured phase difference (REF vs ZCD) in degrees (-180..+180)</summary>
    public float PhaseDegrees { get; set; }
}

