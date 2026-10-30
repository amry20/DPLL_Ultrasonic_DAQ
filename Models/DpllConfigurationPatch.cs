namespace DPLL_Ultrasonic_DAQ.Models;

/// <summary>
/// Partial configuration patch sent from the UI. Only the fields the user
/// changed are applied; firmware confirms via a paced binary GET readback.
/// </summary>
public sealed class DpllConfigurationPatch
{
    public double? Kp { get; set; }
    public double? Ki { get; set; }
    public double? Kd { get; set; }
    public double? CenterVoltage { get; set; }
    public double? TargetPhase { get; set; }
    public double? MaxSlew { get; set; }
    public uint? LoopPeriodMs { get; set; }
    public double? LockThresholdNs { get; set; }
    public uint? LockHoldCycles { get; set; }
    public uint? LockMemoryTimeoutMs { get; set; }
    public uint? StreamPeriodMs { get; set; }
    public bool? ManualMode { get; set; }
    public int? SignalLossBehavior { get; set; }
    public int? GeneratorSource { get; set; }
    public double? DdsCenterFreq { get; set; }
    public double? DdsMinFreq { get; set; }
    public double? DdsMaxFreq { get; set; }
    public int? DdsMcoClock { get; set; }
    public int? ReferenceCaptureEdge { get; set; }
    public int? ZcdCaptureEdge { get; set; }
    public int? TrackingDirection { get; set; }

    /// <summary>Source whose Kp/Ki/Kd/MaxSlew the user read before editing.
    /// Required for gain edits; prevents a second client from changing the active bank underneath them.</summary>
    public int? ExpectedGeneratorSource { get; set; }
}
