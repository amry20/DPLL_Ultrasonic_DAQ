namespace DPLL_Ultrasonic_DAQ.Models;

/// <summary>
/// Status of the firmware hardware timer pulse sonication sequence.
/// </summary>
public sealed class SonicationStatus
{
    /// <summary>0 = IDLE, 1 = ON, 2 = REST</summary>
    public byte State { get; set; }

    /// <summary>1-based current cycle count, or 0 if IDLE</summary>
    public uint CurrentCycle { get; set; }

    /// <summary>Total target cycles (0 = continuous pulsing)</summary>
    public uint TargetCycles { get; set; }

    /// <summary>Remaining time in milliseconds for the active phase</summary>
    public uint RemainingMs { get; set; }

    /// <summary>Total elapsed milliseconds since the sequence started</summary>
    public uint ElapsedMs { get; set; }

    /// <summary>Human-readable state string</summary>
    public string StateText => State switch
    {
        1 => "ON",
        2 => "REST",
        _ => "IDLE"
    };

    /// <summary>True when pulsing is active (ON or REST phase)</summary>
    public bool IsActive => State is 1 or 2;
}

