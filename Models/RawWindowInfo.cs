namespace DPLL_Ultrasonic_DAQ.Models;

/// <summary>A one-shot, full-rate REF/ZCD capture for ONE control window.</summary>
public sealed class RawWindowInfo
{
    public int State { get; set; } // 0 idle, 1 armed, 2 ready
    public uint WindowId { get; set; }
    public ushort SampleCount { get; set; }
    public ushort AcceptedCount { get; set; }
    public uint CaptureMillis { get; set; }
    public ushort LoopPeriodMs { get; set; }
    public int Flags { get; set; }
    public int LockStatus { get; set; }
    public float FilteredPhaseNs { get; set; }
    public float NoiseMadDeg { get; set; }
    public float OutlierRatio { get; set; }
    public float SlopeDegPerMs { get; set; }
    public float TargetPhaseNs { get; set; }
    public float DacBefore { get; set; }
    public float DacAfter { get; set; }
    public bool Overflow => (Flags & 1) != 0;
    public bool Trustworthy => (Flags & 2) != 0;
    public bool PhaseComputed => (Flags & 4) != 0;
    public bool LargeShift => (Flags & 8) != 0;
    public bool PidApplied => (Flags & 16) != 0;
}
