namespace DPLL_Ultrasonic_DAQ.Models;

/// <summary>
/// Mirror of the firmware <c>dpllStatusData</c> struct (16 bytes, packed).
/// Decoded from the 0x0019 STREAM_DPLL_STATUS packet.
/// </summary>
public sealed class DpllTelemetry
{
    /// <summary>Measured reference frequency in Hz.</summary>
    public double ReferenceFrequencyHz { get; set; }

    /// <summary>Phase difference ZCD vs REF in nanoseconds (last-valid value when <see cref="PhaseStale"/> = 1).</summary>
    public double PhaseErrorNs { get; set; }

    /// <summary>Current DAC output voltage in volts (0.0 – 3.3).</summary>
    public double DACVoltage_V { get; set; }

    /// <summary>Controller state: 0=NO_REF, 1=WAIT_ZCD, 2=TRACK, 3=LOCK, 4=MANUAL.</summary>
    public int LockStatus { get; set; }

    /// <summary>0 = fresh measurement, 1 = ZCD absent, value is a held last-valid phase.</summary>
    public int PhaseStale { get; set; }

    /// <summary>0 = Disconnected/Error, 1 = Connected & OK.</summary>
    public int EepromStatus { get; set; }

    /// <summary>True when external EEPROM 24LC16B is detected and responding on I2C.</summary>
    public bool IsEepromOk => EepromStatus == 1;

    /// <summary>Bits 0–5: hardware status (0=error, 1=ready, 2=signal verified); bit 6=inverted; bit 7=DDS active.</summary>
    public int DdsStatus { get; set; }

    public int DdsHardwareStatus => DdsStatus & 0x3F;

    /// <summary>True when AD9833 DDS is initialized and ready.</summary>
    public bool IsDdsOk => DdsHardwareStatus is 1 or 2;

    /// <summary>True when AD9833 output signal is physically verified on PA0.</summary>
    public bool IsDdsSignalVerified => DdsHardwareStatus == 2;

    /// <summary>Tracking-direction flag (bit 6); older firmware may omit this flag.</summary>
    public bool IsTrackingInverted => (DdsStatus & 0x40) != 0;

    /// <summary>True when DDS AD9833 is selected as active DPLL generator source (bit 7).</summary>
    public bool IsDdsActiveGenerator => (DdsStatus & 0x80) != 0;

    /// <summary>Seconds since the Unix epoch (UTC) when this sample was captured by the host.</summary>
    public double Timestamp { get; set; }

    /// <summary>Human-readable lock state name.</summary>
    public string State => LockStatus switch
    {
        0 => "NO REF",
        1 => "WAIT ZCD",
        2 => "TRACK",
        3 => "LOCK",
        4 => "MANUAL",
        _ => "UNKNOWN"
    };

    /// <summary>True when the current lock status is LOCK.</summary>
    public bool IsLocked => LockStatus == 3;
}
