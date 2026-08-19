using System.IO.Ports;
using System.Text;
using DPLL_Ultrasonic_DAQ.Models;
using DPLL_Ultrasonic_DAQ.Protocol;
using Microsoft.Extensions.Options;

namespace DPLL_Ultrasonic_DAQ.Services;

/// <summary>Firmware connection state reported to the UI.</summary>
public enum DeviceConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

/// <summary>
/// Manages the single serial link to the DPLL firmware. One COM port carries
/// BOTH traffic types:
/// <list type="bullet">
/// <item><b>Binary telemetry</b> — opcode packets (host enables the 100 Hz
/// stream with 0x0017, firmware streams 0x0019 status packets).</item>
/// <item><b>ASCII control</b> — command lines (kp/ki/kd/center/target/slew/
/// loop/loss/gain/dac/reset/run/help) and the parseable <c>gain</c> report.</item>
/// </list>
/// Incoming bytes are demultiplexed: binary packets are parsed with
/// <see cref="DpllProtocol"/>, everything else is treated as ASCII text lines.
/// Auto-reconnect is attempted while a port is requested.
/// </summary>
public sealed class SerialDeviceService : IDisposable
{
    private readonly ILogger<SerialDeviceService> _logger;
    private readonly IOptionsMonitor<SerialOptions> _optionsMonitor;
    private SerialOptions _options;
    private readonly IDisposable? _changeSubscription;

    private readonly object _writeLock = new();
    private readonly List<byte> _rxBuffer = new(2048);
    private readonly StringBuilder _asciiLine = new();

    private SerialPort? _port;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private string? _requestedPort;
    private bool _streamEnabled;

    private DeviceConnectionState _state = DeviceConnectionState.Disconnected;
    private DpllTelemetry? _latest;
    private DpllConfiguration? _config;
    private DateTimeOffset _lastStreamAt;
    private volatile bool _disposed;

    public event Action<DpllTelemetry>? TelemetryReceived;
    public event Action<DeviceConnectionState, string?>? ConnectionChanged;
    public event Action<DpllConfiguration>? ConfigurationReceived;

    public SerialDeviceService(IOptionsMonitor<SerialOptions> optionsMonitor, ILogger<SerialDeviceService> logger)
    {
        _optionsMonitor = optionsMonitor;
        _options = optionsMonitor.CurrentValue;
        _logger = logger;
        // Re-apply serial options (e.g. serial.json edited on disk) without a restart:
        // close the old port and reconnect to the newly configured one.
        _changeSubscription = _optionsMonitor.OnChange(OnOptionsChanged);
    }

    private void OnOptionsChanged(SerialOptions next)
    {
        _logger.LogInformation("Serial configuration changed: port={P}, baud={B}", next.PortName, next.BaudRate);
        _options = next;
        ApplyConfiguredPort();
    }

    /// <summary>Start the configured link (called once at application startup).</summary>
    public void Start() => ApplyConfiguredPort();

    /// <summary>Stop the link and unsubscribe from configuration changes.</summary>
    public void Stop()
    {
        _requestedPort = null;
        ClosePort();
        SetState(DeviceConnectionState.Disconnected, null);
    }

    private void ApplyConfiguredPort()
    {
        if (_disposed)
        {
            return;
        }
        var portName = _options.PortName;

        // Re-point the link.
        if (string.Equals(_requestedPort, portName, StringComparison.OrdinalIgnoreCase))
        {
            // same port — leave the connect loop alone
        }
        else
        {
            _requestedPort = portName;
            ClosePort();
            if (!string.IsNullOrWhiteSpace(portName))
            {
                _logger.LogInformation("Auto-connecting to {Port} at {Baud} baud...", portName, _options.BaudRate);
                _ = Task.Run(TryOpenLoop);
            }
        }

        UpdateConnectionState();
    }

    /// <summary>Current overall connection state.</summary>
    public DeviceConnectionState State => _state;

    /// <summary>The COM port in use (or being connected to).</summary>
    public string? PortName => _requestedPort;

    /// <summary>Latest telemetry sample, or null if none received yet.</summary>
    public DpllTelemetry? Latest => _latest;

    /// <summary>Last known firmware configuration (null until first successful read).</summary>
    public DpllConfiguration? Configuration => _config;

    /// <summary>True when the telemetry stream is considered fresh (recent packet received).</summary>
    public bool IsStreamFresh => _latest != null && (DateTimeOffset.UtcNow - _lastStreamAt).TotalMilliseconds < _options.StreamTimeoutMs;

    /// <summary>List detected serial ports.</summary>
    public static IReadOnlyList<string> GetAvailablePorts() => SerialPort.GetPortNames();

    // ------------------------------------------------------------------
    // Connection management
    // ------------------------------------------------------------------

    /// <summary>Open the serial link (binary + ASCII on the same port).</summary>
    public void Connect(string portName)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new ArgumentException("Port name is required.", nameof(portName));
        }

        _requestedPort = portName;
        _logger.LogInformation("Connecting to {Port} at {Baud} baud...", portName, _options.BaudRate);

        ClosePort();
        _ = Task.Run(TryOpenLoop);
    }

    /// <summary>Close the link and stop auto-reconnect.</summary>
    public void Disconnect()
    {
        ThrowIfDisposed();
        _requestedPort = null;
        ClosePort();
        SetState(DeviceConnectionState.Disconnected, null);
    }

    private void TryOpenLoop()
    {
        if (_disposed || string.IsNullOrEmpty(_requestedPort))
        {
            return;
        }

        while (!_disposed && !string.IsNullOrEmpty(_requestedPort))
        {
            string port = _requestedPort;
            try
            {
                SetState(DeviceConnectionState.Connecting, port);

                if (!SerialPort.GetPortNames().Contains(port, StringComparer.OrdinalIgnoreCase))
                {
                    throw new IOException($"Port '{port}' is not available.");
                }

                var portObj = new SerialPort(port, _options.BaudRate, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 1000,
                    WriteTimeout = 1000,
                    DtrEnable = true,
                    RtsEnable = true
                };
                portObj.Open();

                lock (_writeLock)
                {
                    _port = portObj;
                }
                _rxBuffer.Clear();
                _asciiLine.Clear();

                _readCts = new CancellationTokenSource();
                _readTask = Task.Run(() => ReadLoopAsync(portObj, _readCts.Token));

                _streamEnabled = false;
                _logger.LogInformation("Connected to {Port}. Enabling stream...", port);
                UpdateConnectionState();

                // Ask firmware to start the 100 Hz telemetry stream, then pull the
                // current configuration (ASCII "gain" command).
                EnableStream(true);
                RefreshConfiguration();
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to connect to {Port}: {Message}. Retrying in {Delay} ms...",
                    port, ex.Message, _options.ReconnectDelayMs);
                ClosePort();
                SetState(DeviceConnectionState.Error, ex.Message);

                try { Thread.Sleep(_options.ReconnectDelayMs); }
                catch (ThreadInterruptedException) { return; }
            }
        }
    }

    private void ClosePort()
    {
        _readCts?.Cancel();
        _readTask = null;

        lock (_writeLock)
        {
            var p = _port;
            _port = null;
            try { p?.Close(); } catch { /* ignore */ }
            try { p?.Dispose(); } catch { /* ignore */ }
        }
        _readCts?.Dispose();
        _readCts = null;
        _streamEnabled = false;
        _rxBuffer.Clear();
        _asciiLine.Clear();
    }

    private void UpdateConnectionState()
    {
        bool up = !string.IsNullOrEmpty(_requestedPort) && _port is { IsOpen: true };

        DeviceConnectionState next;
        string? detail = null;

        if (!string.IsNullOrEmpty(_requestedPort) && !up)
        {
            next = _port is null ? DeviceConnectionState.Connecting : DeviceConnectionState.Error;
            detail = _requestedPort;
        }
        else if (up)
        {
            next = DeviceConnectionState.Connected;
            detail = _requestedPort;
        }
        else
        {
            next = DeviceConnectionState.Disconnected;
        }

        SetState(next, detail);
    }

    private void SetState(DeviceConnectionState state, string? detail)
    {
        if (_state != state)
        {
            _state = state;
            _logger.LogInformation("Connection state -> {State} ({Detail})", state, detail);
            try { ConnectionChanged?.Invoke(state, detail); }
            catch (Exception ex) { _logger.LogError(ex, "ConnectionChanged handler threw."); }
        }
    }

    // ------------------------------------------------------------------
    // Reading — demultiplexed (binary packets + ASCII lines)
    // ------------------------------------------------------------------

    private async Task ReadLoopAsync(SerialPort port, CancellationToken token)
    {
        byte[] chunk = new byte[512];
        try
        {
            while (!token.IsCancellationRequested && port.IsOpen)
            {
                int n = await port.BaseStream.ReadAsync(chunk.AsMemory(0, chunk.Length), token).ConfigureAwait(false);
                if (n <= 0)
                {
                    await Task.Delay(5, token).ConfigureAwait(false);
                    continue;
                }

                for (int i = 0; i < n; i++)
                {
                    byte b = chunk[i];
                    if (b is (byte)'\n')
                    {
                        // Flush any buffered binary bytes first, then treat the
                        // accumulated characters as one ASCII line.
                        TryParsePackets();

                        string line = _asciiLine.ToString().Trim();
                        _asciiLine.Clear();
                        if (line.Length > 0)
                        {
                            HandleAsciiLine(line);
                        }
                        continue;
                    }
                    if (b is (byte)'\r')
                    {
                        continue;
                    }

                    // Binary frame bytes are outside the printable ASCII range
                    // (0x20–0x7E): 0xAA start marker, opcode/len little-endian
                    // words, float payloads, checksum. Printable bytes start or
                    // continue an ASCII command line.
                    if (b < 0x20 || b > 0x7E)
                    {
                        _rxBuffer.Add(b);
                    }
                    else
                    {
                        _asciiLine.Append((char)b);
                    }
                }

                // Flush partial binary packets.
                TryParsePackets();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            _logger.LogWarning("Read loop ended: {Message}", ex.Message);
            if (!_disposed && !string.IsNullOrEmpty(_requestedPort))
            {
                ClosePort();
                _ = Task.Run(TryOpenLoop);
            }
        }
    }

    private void TryParsePackets()
    {
        var buffer = _rxBuffer.ToArray();
        int offset = 0;
        while (offset < buffer.Length)
        {
            var span = buffer.AsSpan(offset);
            if (!DpllProtocol.TryParsePacket(span, out int consumed, out ushort opcode, out _, out ReadOnlySpan<byte> payload))
            {
                if (consumed < 0)
                {
                    break; // need more data
                }
                offset += consumed;
                continue;
            }

            if (opcode == Opcode.STREAM_DPLL_STATUS)
            {
                var telemetry = DpllProtocol.DecodeStatusPayload(payload, DateTimeOffset.UtcNow);
                _latest = telemetry;
                _lastStreamAt = DateTimeOffset.UtcNow;
                try { TelemetryReceived?.Invoke(telemetry); }
                catch (Exception ex) { _logger.LogError(ex, "TelemetryReceived handler threw."); }
            }
            else
            {
                _logger.LogDebug("Received opcode 0x{Opcode:X4} ({Len} payload bytes)", opcode, payload.Length);
            }

            offset += consumed;
        }

        if (offset > 0)
        {
            _rxBuffer.RemoveRange(0, offset);
        }
    }

    private void HandleAsciiLine(string line)
    {
        // The firmware "gain" command prints a parseable status line:
        //   Kp=0.000002 V/ns | Ki=0.000200 V/ns/s | Kd=0.000000 V/ns/s | center=1.65 V | target=0.0 ns | slew=30.0 V/s | manual=no | loop=20 ms | thr=500 ns | lockedV=1.650 V (default) | loss=0
        if (!line.Contains("Kp=", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("ASCII: {Line}", line);
            return;
        }

        try
        {
            var cfg = new DpllConfiguration();
            foreach (var part in line.Split('|', StringSplitOptions.TrimEntries))
            {
                int eq = part.IndexOf('=');
                if (eq < 0) continue;
                string key = part[..eq].Trim();
                string value = part[(eq + 1)..].Trim();

                switch (key.ToLowerInvariant())
                {
                    case "kp": cfg.Kp = ParseDouble(value, "V/ns"); break;
                    case "ki": cfg.Ki = ParseDouble(value, "V/ns/s"); break;
                    case "kd": cfg.Kd = ParseDouble(value, "V/ns/s"); break;
                    case "center": cfg.CenterVoltage = ParseDouble(value, "V"); break;
                    case "target": cfg.TargetPhase = ParseDouble(value, "ns"); break;
                    case "slew": cfg.MaxSlew = ParseDouble(value, "V/s"); break;
                    case "manual": cfg.ManualMode = value.StartsWith("yes", StringComparison.OrdinalIgnoreCase); break;
                    case "loop": cfg.LoopPeriodMs = (uint)ParseDouble(value, "ms"); break;
                    case "thr": cfg.LockThresholdNs = ParseDouble(value, "ns"); break;
                    case "hold": cfg.LockHoldCycles = (uint)ParseDouble(value, null); break;
                    case "timeout": cfg.LockMemoryTimeoutMs = (uint)ParseDouble(value, "ms"); break;
                    case "stream": cfg.StreamPeriodMs = (uint)ParseDouble(value, "ms"); break;
                    case "lockedv": cfg.LockedCenterV = ParseDouble(value, "V"); cfg.HaveLockedCenter = !value.Contains("default", StringComparison.OrdinalIgnoreCase); break;
                    case "loss": cfg.SignalLossBehavior = (int)ParseDouble(value, null); break;
                }
            }

            _config = cfg;
            _logger.LogDebug("Configuration parsed: Kp={Kp}, Ki={Ki}, center={Center} V, loop={Loop} ms",
                cfg.Kp, cfg.Ki, cfg.CenterVoltage, cfg.LoopPeriodMs);
            try { ConfigurationReceived?.Invoke(cfg); }
            catch (Exception ex) { _logger.LogError(ex, "ConfigurationReceived handler threw."); }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to parse firmware status line: {Line} ({Message})", line, ex.Message);
        }
    }

    private static double ParseDouble(string value, string? unit)
    {
        if (!string.IsNullOrEmpty(unit) && value.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^unit.Length].Trim();
        }
        return double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    // ------------------------------------------------------------------
    // Writing
    // ------------------------------------------------------------------

    /// <summary>Enable or disable the firmware telemetry stream (opcode 0x0017).</summary>
    public void EnableStream(bool enable)
    {
        _streamEnabled = enable;
        SendBinary(Opcode.SET_ALLOW_SEND_STREAM, [enable ? (byte)1 : (byte)0]);
    }

    /// <summary>Send a raw binary packet on the serial port.</summary>
    public void SendBinary(ushort opcode, ReadOnlySpan<byte> payload)
    {
        if (_disposed)
        {
            return;
        }
        var packet = DpllProtocol.BuildPacket(opcode, 0, payload);
        lock (_writeLock)
        {
            var p = _port;
            if (p is null || !p.IsOpen)
            {
                _logger.LogDebug("Binary write dropped: port not open (opcode 0x{Opcode:X4})", opcode);
                return;
            }
            try
            {
                p.Write(packet, 0, packet.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Binary write failed: {Message}", ex.Message);
            }
        }
    }

    /// <summary>Send a raw ASCII debug command line on the serial port.</summary>
    public void SendAscii(string command)
    {
        if (_disposed || string.IsNullOrWhiteSpace(command))
        {
            return;
        }
        byte[] bytes = Encoding.ASCII.GetBytes(command.Trim() + "\r\n");
        lock (_writeLock)
        {
            var p = _port;
            if (p is null || !p.IsOpen)
            {
                _logger.LogDebug("ASCII write dropped: port not open ({Command})", command);
                return;
            }
            try
            {
                p.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("ASCII write failed: {Message}", ex.Message);
            }
        }
    }

    /// <summary>Request a fresh configuration dump from the firmware ("gain" command).</summary>
    public void RefreshConfiguration() => SendAscii("gain");

    /// <summary>
    /// Apply a set of configuration values.
    /// <para>
    /// Settings with a firmware ASCII command (Kp/Ki/Kd/center/target/slew/loop/
    /// loss/manual/timeout) are sent as ASCII command lines. Settings that only
    /// exist as binary opcodes in the firmware <c>Opcode.h</c> (lock threshold,
    /// lock hold cycles, stream period) are sent as binary SET packets.
    /// </para>
    /// Afterwards the config is refreshed via the <c>gain</c> report.
    /// </summary>
    public void ApplyConfiguration(DpllConfigurationPatch patch)
    {
        if (patch.Kp.HasValue) SendAscii($"kp {FormatFloat(patch.Kp.Value)}");
        if (patch.Ki.HasValue) SendAscii($"ki {FormatFloat(patch.Ki.Value)}");
        if (patch.Kd.HasValue) SendAscii($"kd {FormatFloat(patch.Kd.Value)}");
        if (patch.CenterVoltage.HasValue) SendAscii($"center {FormatFloat(patch.CenterVoltage.Value)}");
        if (patch.TargetPhase.HasValue) SendAscii($"target {FormatFloat(patch.TargetPhase.Value)}");
        if (patch.MaxSlew.HasValue) SendAscii($"slew {FormatFloat(patch.MaxSlew.Value)}");
        if (patch.LoopPeriodMs.HasValue) SendAscii($"loop {patch.LoopPeriodMs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (patch.LockMemoryTimeoutMs.HasValue) SendAscii($"timeout {patch.LockMemoryTimeoutMs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (patch.SignalLossBehavior.HasValue) SendAscii($"loss {(int)patch.SignalLossBehavior.Value}");
        if (patch.ManualMode.HasValue)
        {
            SendAscii(patch.ManualMode.Value ? "dac 1.65" : "run");
        }

        // Settings without an ASCII command — send as binary SET opcodes
        // (handled by the firmware commandProccessor() switch in main.cpp).
        if (patch.LockThresholdNs.HasValue)
            SendBinary(Opcode.SET_LOCK_THRESHOLD, DpllProtocol.EncodeFloat((float)patch.LockThresholdNs.Value));
        if (patch.LockHoldCycles.HasValue)
            SendBinary(Opcode.SET_LOCK_HOLD_CYCLES, DpllProtocol.EncodeUInt32(patch.LockHoldCycles.Value));
        if (patch.StreamPeriodMs.HasValue)
            SendBinary(Opcode.SET_STREAM_PERIOD, DpllProtocol.EncodeUInt32(patch.StreamPeriodMs.Value));

        RefreshConfiguration();
    }

    private static string FormatFloat(double value) =>
        value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Reset the loop: clear integrator, restart from center, enable.</summary>
    public void ResetLoop() => SendAscii("reset");

    /// <summary>Shutdown the loop: DAC to 0 V, loop disabled.</summary>
    public void ShutdownLoop() => SendAscii("dac 0.0");

    /// <summary>Manual DAC voltage (disables the loop).</summary>
    public void SetManualVoltage(double volts) => SendAscii($"dac {FormatFloat(volts)}");

    /// <summary>Re-enable automatic control (manual mode off).</summary>
    public void RunLoop() => SendAscii("run");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _changeSubscription?.Dispose();
        _requestedPort = null;
        ClosePort();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
