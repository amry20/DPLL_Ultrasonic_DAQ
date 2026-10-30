namespace DPLL_Ultrasonic_DAQ.Models;

/// <summary>
/// DPLL configuration as reported by the firmware. Mirrors the values
/// printed by the debug <c>gain</c> command and the opcode getters.
/// </summary>
public sealed class DpllConfiguration
{
    public DpllConfiguration Copy() => (DpllConfiguration)MemberwiseClone();

    /// <summary>Exact GET_VERSION string, including firmware build identity when supplied.</summary>
    public string? FirmwareVersion { get; set; }

    /// <summary>True only after all required GETs in one paced readback have completed.</summary>
    public bool ReadbackComplete { get; set; }

    /// <summary>Capture edge: 0=rising, 1=falling, 2=both; null when GET is unsupported or unanswered.</summary>
    public int? ReferenceCaptureEdge { get; set; }
    public int? ZcdCaptureEdge { get; set; }

    /// <summary>0=normal, 1=inverted; null when GET is unsupported or unanswered.</summary>
    public int? TrackingDirection { get; set; }

    /// <summary>Proportional gain, V/ns (DAC) or Hz/ns (DDS).</summary>
    public double Kp { get; set; }

    /// <summary>Integral gain, V/(ns·s) (DAC) or Hz/(ns·s) (DDS).</summary>
    public double Ki { get; set; }

    /// <summary>Derivative gain, V·s/ns (DAC) or Hz·s/ns (DDS); 0 = disabled.</summary>
    public double Kd { get; set; }

    /// <summary>Center voltage, volts.</summary>
    public double CenterVoltage { get; set; }

    /// <summary>Target phase setpoint, ns.</summary>
    public double TargetPhase { get; set; }

    /// <summary>Max slew rate, V/s (DAC) or Hz/s (DDS).</summary>
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

    /// <summary>Generator source: 0 = DAC (CD4046 VCO), 1 = DDS (AD9833), 2 = PWM Dither (STM32 TIM1 PA8).</summary>
    public int GeneratorSource { get; set; } = 2;

    /// <summary>True when frequency mode (DDS AD9833 or PWM Dither) is selected as generator source.</summary>
    public bool IsDdsMode => GeneratorSource is 1 or 2;

    /// <summary>DDS center nominal frequency in Hz.</summary>
    public double DdsCenterFreq { get; set; } = 28000.0;

    /// <summary>DDS minimum allowable frequency in Hz.</summary>
    public double DdsMinFreq { get; set; } = 20000.0;

    /// <summary>DDS maximum allowable frequency in Hz.</summary>
    public double DdsMaxFreq { get; set; } = 40000.0;

    /// <summary>Active DDS MCO clock preset: 0=8MHz HSE, 1=4MHz HSE, 2=2MHz HSE, 3=1.6MHz HSE, 4=16MHz HSI, 5=8MHz HSI, 6=4MHz HSI.</summary>
    public int DdsMcoClock { get; set; } = 0;

    /// <summary>Human-readable name of active MCO clock.</summary>
    public string DdsMcoClockName => DdsMcoClock switch
    {
        0 => "HSE 8 MHz (/1)",
        1 => "HSE 4 MHz (/2)",
        2 => "HSE 2 MHz (/4)",
        3 => "HSE 1.6 MHz (/5)",
        4 => "HSI 16 MHz (/1)",
        5 => "HSI 8 MHz (/2)",
        6 => "HSI 4 MHz (/4)",
        _ => "Unknown"
    };

    /// <summary>DDS frequency resolution in Hz with active MCO clock.</summary>
    public double DdsResolutionHz => DdsMcoClock switch
    {
        0 => 8000000.0 / 268435456.0,
        1 => 4000000.0 / 268435456.0,
        2 => 2000000.0 / 268435456.0,
        3 => 1600000.0 / 268435456.0,
        4 => 16000000.0 / 268435456.0,
        5 => 8000000.0 / 268435456.0,
        6 => 4000000.0 / 268435456.0,
        _ => 8000000.0 / 268435456.0
    };
}
