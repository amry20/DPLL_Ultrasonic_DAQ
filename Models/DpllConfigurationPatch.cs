namespace DPLL_Ultrasonic_DAQ.Models;

/// <summary>
/// Partial configuration patch sent from the UI. Only the fields the user
/// changed are applied; firmware confirms via a fresh <c>gain</c> readback.
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
    public bool? ManualMode { get; set; }
    public int? SignalLossBehavior { get; set; }
}
