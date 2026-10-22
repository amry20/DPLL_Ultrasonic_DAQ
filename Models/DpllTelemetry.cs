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

    /// <summary>0 = Disconnected/Error, 1 = Connected & OK.</summary>
    public int EepromStatus { get; set; }

    /// <summary>True when external EEPROM 24LC16B is detected and responding on I2C.</summary>
    public bool IsEepromOk => EepromStatus == 1;

    /// <summary>0 = Error/Uninitialized, 1 = Ready/OK (Clock & SPI running), 2 = Signal Verified on PA0.</summary>
    public int DdsStatus { get; set; }

    /// <summary>True when AD9833 DDS is initialized and ready.</summary>
    public bool IsDdsOk => (DdsStatus & 0x3F) >= 1;

    /// <summary>True when AD9833 output signal is physically verified on PA0.</summary>
    public bool IsDdsSignalVerified => (DdsStatus & 0x3F) == 2;

    /// <summary>True when DDS AD9833 is selected as active DPLL generator source (bit 7).</summary>
    public bool IsDdsActiveGenerator => (DdsStatus & 0x80) != 0;

    /// <summary>True when firmware tracking direction is INVERTED (bit 6, diagnostic).</summary>
    public bool IsTrackingInverted => (DdsStatus & 0x40) != 0;

    /// <summary>Seconds since the Unix epoch (UTC) when this sample was captured by the host.</summary>
    public double Timestamp { get; set; }

    /// <summary>Human-readable lock state name.</summary>
    public string State => LockStatus switch
    {
        0 => "NO REF",
        1 => "WAIT ZCD",
        2 => "TRACK",
        3 => "LOCK",
        4 => "MANUAL TEST",
        _ => "UNKNOWN"
    };

    /// <summary>True when the current lock status is LOCK.</summary>
    public bool IsLocked => LockStatus == 3;
}
