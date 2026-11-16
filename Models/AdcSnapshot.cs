namespace DPLL_Ultrasonic_DAQ.Models;

/// <summary>
/// Represents a raw ADC waveform snapshot captured synchronously by the STM32F407 front-end.
/// Channel 0: PC4 (Transducer Current Sense from CT burden).
/// Channel 1: PA7 (Drive Reference from PA8 TIM1 PWM via RC).
/// </summary>
public class AdcSnapshot
{
    /// <summary>Channel identifier: 0 = PC4 (Current), 1 = PA7 (Drive Reference).</summary>
    public byte Channel { get; set; }

    /// <summary>Number of samples in the snapshot (e.g. 240 samples).</summary>
    public ushort SampleCount { get; set; }

    /// <summary>ADC sampling rate in Hz (750,000 Hz / 750 kSPS).</summary>
    public uint SampleRateHz { get; set; }

    /// <summary>Active generator drive frequency in Hz at the capture instant.</summary>
    public float DriveFreqHz { get; set; }

    /// <summary>VREF voltage in Volts (typically 1.65 V).</summary>
    public float VRefVolts { get; set; }

    /// <summary>Input attenuator ratio (e.g. 0.3333 for 10k/(20k+10k)).</summary>
    public float FrontEndAtten { get; set; }

    /// <summary>Raw 12-bit ADC counts (0 - 4095).</summary>
    public ushort[] RawSamples { get; set; } = Array.Empty<ushort>();

    /// <summary>Calculated instantaneous pin voltage (0.0 V - 3.3 V).</summary>
    public float[] VoltageVolts { get; set; } = Array.Empty<float>();

    /// <summary>Calculated time in microseconds relative to snapshot start (0.0 us, 1.333 us, ...).</summary>
    public float[] TimeUs { get; set; } = Array.Empty<float>();

    /// <summary>Peak AC voltage amplitude (Vpk = (Vmax - Vmin)/2).</summary>
    public float VPeak { get; set; }

    /// <summary>Average DC offset voltage (Vmid / baseline, ideally ~1.65 V).</summary>
    public float VMid { get; set; }

    /// <summary>Minimum observed pin voltage (Volts).</summary>
    public float VMin { get; set; }

    /// <summary>Maximum observed pin voltage (Volts).</summary>
    public float VMax { get; set; }

    /// <summary>Estimated peak burden voltage before attenuation (Vburden = Vpk / FrontEndAtten).</summary>
    public float VBurdenPk { get; set; }

    /// <summary>Timestamp when packet was received by host.</summary>
    public DateTimeOffset Timestamp { get; set; }
}

