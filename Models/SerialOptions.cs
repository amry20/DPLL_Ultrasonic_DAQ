namespace DPLL_Ultrasonic_DAQ.Models;

/// <summary>Serial-port options bound from the <c>Serial</c> section of appsettings.json.</summary>
public sealed class SerialOptions
{
    public const string SectionName = "Serial";

    /// <summary>
    /// Serial COM port name (e.g. COM9) — the port the firmware uses for BOTH
    /// the binary telemetry stream and ASCII tuning commands. Empty = disabled.
    /// </summary>
    public string? PortName { get; set; }

    /// <summary>Baud rate, defaults to the firmware's 115200.</summary>
    public int BaudRate { get; set; } = 115200;

    /// <summary>Host reconnect delay between connection attempts, ms.</summary>
    public int ReconnectDelayMs { get; set; } = 2000;

    /// <summary>Mark telemetry stale after this many ms without a stream packet.</summary>
    public int StreamTimeoutMs { get; set; } = 1000;
}
