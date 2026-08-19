namespace DPLL_Ultrasonic_DAQ.Models;

/// <summary>Serial-port options bound from the <c>Serial</c> section of appsettings.json.</summary>
public sealed class SerialOptions
{
    public const string SectionName = "Serial";

    /// <summary>
    /// Telemetry COM port name (e.g. COM9) — the USB CDC virtual port the
    /// firmware uses for the binary opcode stream. Empty/auto = let the UI
    /// pick from detected ports.
    /// </summary>
    public string? PortName { get; set; }

    /// <summary>
    /// Control COM port name — the DebugPort (PA10/PA9) hardware UART the
    /// firmware uses for ASCII tuning commands. Empty/auto = let the UI pick.
    /// </summary>
    public string? ControlPortName { get; set; }

    /// <summary>Baud rate, defaults to the firmware's 115200 (both ports).</summary>
    public int BaudRate { get; set; } = 115200;

    /// <summary>Host reconnect delay between connection attempts, ms.</summary>
    public int ReconnectDelayMs { get; set; } = 2000;

    /// <summary>Mark telemetry stale after this many ms without a stream packet.</summary>
    public int StreamTimeoutMs { get; set; } = 1000;
}
