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
/// Manages the two serial links to the DPLL firmware:
/// <list type="bullet">
/// <item><b>Telemetry port</b> — the USB CDC virtual COM port (SerialUSB).
/// Binary opcode protocol: the host enables the 100 Hz stream with opcode
/// 0x0017 and receives 0x0019 status packets.</item>
/// <item><b>Control port</b> — the DebugPort (PA10/PA9) hardware UART.
/// ASCII command line protocol (kp/ki/kd/center/target/slew/loop/loss/gain/
/// dac/reset/run/help). This is the ONLY channel that can change tuning
/// parameters — the firmware's binary interface only handles 0x0017.</item>
/// </list>
/// Auto-reconnect is attempted on each link while the user has requested it
/// to stay open. Overall connection state is the AND of both links.
/// </summary>
public sealed class SerialDeviceService : IDisposable
{
    private readonly ILogger<SerialDeviceService> _logger;
    private readonly IOptionsMonitor<SerialOptions> _optionsMonitor;
    private SerialOptions _options;
    private readonly IDisposable? _changeSubscription;

    private readonly object _telemetryWriteLock = new();
    private readonly object _controlWriteLock = new();
    private readonly List<byte> _rxBuffer = new(2048);
    private readonly StringBuilder _asciiLine = new();

    private SerialPort? _telemetryPort;
    private SerialPort? _controlPort;
    private CancellationTokenSource? _telemetryReadCts;
    private CancellationTokenSource? _controlReadCts;
    private Task? _telemetryReadTask;
    private Task? _controlReadTask;
    private string? _requestedTelemetryPort;
    private string? _requestedControlPort;
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
        // close the old ports and reconnect to the newly configured ones.
        _changeSubscription = _optionsMonitor.OnChange(OnOptionsChanged);
    }

    private void OnOptionsChanged(SerialOptions next)
    {
        _logger.LogInformation("Serial configuration changed: telemetry={T}, control={C}, baud={B}",
            next.PortName, next.ControlPortName, next.BaudRate);
        _options = next;
        ApplyConfiguredPorts();
    }

    /// <summary>Start the configured links (called once at application startup).</summary>
    public void Start() => ApplyConfiguredPorts();

    /// <summary>Stop both links and unsubscribe from configuration changes.</summary>
    public void Stop()
    {
        _requestedTelemetryPort = null;
        _requestedControlPort = null;
        CloseTelemetryPort();
        CloseControlPort();
        SetState(DeviceConnectionState.Disconnected, null);
    }

    private void ApplyConfiguredPorts()
    {
        if (_disposed)
        {
            return;
        }
        var telemetry = _options.PortName;
        var control = _options.ControlPortName;

        // Re-point telemetry link.
        if (string.Equals(_requestedTelemetryPort, telemetry, StringComparison.OrdinalIgnoreCase))
        {
            // same port — leave the connect loop alone
        }
        else
        {
            _requestedTelemetryPort = telemetry;
            CloseTelemetryPort();
            if (!string.IsNullOrWhiteSpace(telemetry))
            {
                _logger.LogInformation("Auto-connecting telemetry link to {Port} at {Baud} baud...", telemetry, _options.BaudRate);
                _ = Task.Run(TryOpenTelemetryLoop);
            }
        }

        // Re-point control link.
        if (string.Equals(_requestedControlPort, control, StringComparison.OrdinalIgnoreCase))
        {
            // same port — leave the connect loop alone
        }
        else
        {
            _requestedControlPort = control;
            CloseControlPort();
            if (!string.IsNullOrWhiteSpace(control))
            {
                _logger.LogInformation("Auto-connecting control link to {Port} at {Baud} baud...", control, _options.BaudRate);
                _ = Task.Run(TryOpenControlLoop);
            }
        }

        UpdateConnectionState();
    }

    /// <summary>Current overall connection state (AND of both links).</summary>
    public DeviceConnectionState State => _state;

    /// <summary>The telemetry COM port in use (or being connected to).</summary>
    public string? PortName => _requestedTelemetryPort;

    /// <summary>The control COM port in use (or being connected to).</summary>
    public string? ControlPortName => _requestedControlPort;

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

    /// <summary>Open the telemetry link (USB CDC binary stream).</summary>
    public void Connect(string portName) => ConnectTelemetry(portName);

    /// <summary>Open the telemetry link (USB CDC binary stream).</summary>
    public void ConnectTelemetry(string portName)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new ArgumentException("Port name is required.", nameof(portName));
        }

        _requestedTelemetryPort = portName;
        _logger.LogInformation("Connecting telemetry link to {Port} at {Baud} baud...", portName, _options.BaudRate);

        CloseTelemetryPort();
        _ = Task.Run(TryOpenTelemetryLoop);
    }

    /// <summary>Open the control link (DebugPort ASCII UART).</summary>
    public void ConnectControl(string portName)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new ArgumentException("Port name is required.", nameof(portName));
        }

        _requestedControlPort = portName;
        _logger.LogInformation("Connecting control link to {Port} at {Baud} baud...", portName, _options.BaudRate);

        CloseControlPort();
        _ = Task.Run(TryOpenControlLoop);
    }

    /// <summary>Close both links and stop auto-reconnect.</summary>
    public void Disconnect()
    {
        ThrowIfDisposed();
        _requestedTelemetryPort = null;
        _requestedControlPort = null;
        CloseTelemetryPort();
        CloseControlPort();
        SetState(DeviceConnectionState.Disconnected, null);
    }

    private void TryOpenTelemetryLoop()
    {
        if (_disposed || string.IsNullOrEmpty(_requestedTelemetryPort))
        {
            return;
        }

        while (!_disposed && !string.IsNullOrEmpty(_requestedTelemetryPort))
        {
            string port = _requestedTelemetryPort;
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

                lock (_telemetryWriteLock)
                {
                    _telemetryPort = portObj;
                }
                _rxBuffer.Clear();

                _telemetryReadCts = new CancellationTokenSource();
                _telemetryReadTask = Task.Run(() => ReadTelemetryLoopAsync(portObj, _telemetryReadCts.Token));

                _streamEnabled = false;
                _logger.LogInformation("Telemetry link connected to {Port}. Enabling stream...", port);
                UpdateConnectionState();

                // Ask firmware to start the 100 Hz telemetry stream.
                EnableStream(true);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to connect telemetry link to {Port}: {Message}. Retrying in {Delay} ms...",
                    port, ex.Message, _options.ReconnectDelayMs);
                CloseTelemetryPort();
                SetState(DeviceConnectionState.Error, ex.Message);

                try { Thread.Sleep(_options.ReconnectDelayMs); }
                catch (ThreadInterruptedException) { return; }
            }
        }
    }

    private void TryOpenControlLoop()
    {
        if (_disposed || string.IsNullOrEmpty(_requestedControlPort))
        {
            return;
        }

        while (!_disposed && !string.IsNullOrEmpty(_requestedControlPort))
        {
            string port = _requestedControlPort;
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

                lock (_controlWriteLock)
                {
                    _controlPort = portObj;
                }
                _asciiLine.Clear();

                _controlReadCts = new CancellationTokenSource();
                _controlReadTask = Task.Run(() => ReadControlLoopAsync(portObj, _controlReadCts.Token));

                _logger.LogInformation("Control link connected to {Port}.", port);
                UpdateConnectionState();

                // Pull the current configuration (ASCII "gain" command).
                RefreshConfiguration();
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to connect control link to {Port}: {Message}. Retrying in {Delay} ms...",
                    port, ex.Message, _options.ReconnectDelayMs);
                CloseControlPort();
                SetState(DeviceConnectionState.Error, ex.Message);

                try { Thread.Sleep(_options.ReconnectDelayMs); }
                catch (ThreadInterruptedException) { return; }
            }
        }
    }

    private void CloseTelemetryPort()
    {
        _telemetryReadCts?.Cancel();
        _telemetryReadTask = null;

        lock (_telemetryWriteLock)
        {
            var p = _telemetryPort;
            _telemetryPort = null;
            try { p?.Close(); } catch { /* ignore */ }
            try { p?.Dispose(); } catch { /* ignore */ }
        }
        _telemetryReadCts?.Dispose();
        _telemetryReadCts = null;
        _streamEnabled = false;
        _rxBuffer.Clear();
    }

    private void CloseControlPort()
    {
        _controlReadCts?.Cancel();
        _controlReadTask = null;

        lock (_controlWriteLock)
        {
            var p = _controlPort;
            _controlPort = null;
            try { p?.Close(); } catch { /* ignore */ }
            try { p?.Dispose(); } catch { /* ignore */ }
        }
        _controlReadCts?.Dispose();
        _controlReadCts = null;
        _asciiLine.Clear();
    }

    private void UpdateConnectionState()
    {
        bool telemetryUp = !string.IsNullOrEmpty(_requestedTelemetryPort) && _telemetryPort is { IsOpen: true };
        bool controlUp = !string.IsNullOrEmpty(_requestedControlPort) && _controlPort is { IsOpen: true };

        DeviceConnectionState next;
        string? detail = null;

        if (!string.IsNullOrEmpty(_requestedTelemetryPort) && !telemetryUp)
        {
            next = _telemetryPort is null ? DeviceConnectionState.Connecting : DeviceConnectionState.Error;
            detail = _requestedTelemetryPort;
        }
        else if (!string.IsNullOrEmpty(_requestedControlPort) && !controlUp)
        {
            next = _controlPort is null ? DeviceConnectionState.Connecting : DeviceConnectionState.Error;
            detail = _requestedControlPort;
        }
        else if (telemetryUp || controlUp)
        {
            next = DeviceConnectionState.Connected;
            detail = string.Join(", ", new[]
            {
                telemetryUp ? $"T:{_requestedTelemetryPort}" : null,
                controlUp ? $"C:{_requestedControlPort}" : null
            }.Where(s => s is not null));
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
    // Reading — telemetry (binary)
    // ------------------------------------------------------------------

    private async Task ReadTelemetryLoopAsync(SerialPort port, CancellationToken token)
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
                    _rxBuffer.Add(chunk[i]);
                }
                TryParsePackets();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            _logger.LogWarning("Telemetry read loop ended: {Message}", ex.Message);
            if (!_disposed && !string.IsNullOrEmpty(_requestedTelemetryPort))
            {
                CloseTelemetryPort();
                _ = Task.Run(TryOpenTelemetryLoop);
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

    // ------------------------------------------------------------------
    // Reading — control (ASCII)
    // ------------------------------------------------------------------

    private async Task ReadControlLoopAsync(SerialPort port, CancellationToken token)
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
                    if (b is (byte)'\r')
                    {
                        continue;
                    }
                    if (b is (byte)'\n')
                    {
                        string line = _asciiLine.ToString().Trim();
                        _asciiLine.Clear();
                        if (line.Length > 0)
                        {
                            HandleAsciiLine(line);
                        }
                        continue;
                    }
                    _asciiLine.Append((char)b);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            _logger.LogWarning("Control read loop ended: {Message}", ex.Message);
            if (!_disposed && !string.IsNullOrEmpty(_requestedControlPort))
            {
                CloseControlPort();
                _ = Task.Run(TryOpenControlLoop);
            }
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

    /// <summary>Send a raw binary packet (telemetry port only).</summary>
    public void SendBinary(ushort opcode, ReadOnlySpan<byte> payload)
    {
        if (_disposed)
        {
            return;
        }
        var packet = DpllProtocol.BuildPacket(opcode, 0, payload);
        lock (_telemetryWriteLock)
        {
            var p = _telemetryPort;
            if (p is null || !p.IsOpen)
            {
                _logger.LogDebug("Telemetry write dropped: port not open (opcode 0x{Opcode:X4})", opcode);
                return;
            }
            try
            {
                p.Write(packet, 0, packet.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Telemetry write failed: {Message}", ex.Message);
            }
        }
    }

    /// <summary>Send a raw ASCII debug command line on the control port.</summary>
    public void SendAscii(string command)
    {
        if (_disposed || string.IsNullOrWhiteSpace(command))
        {
            return;
        }
        byte[] bytes = Encoding.ASCII.GetBytes(command.Trim() + "\r\n");
        lock (_controlWriteLock)
        {
            var p = _controlPort;
            if (p is null || !p.IsOpen)
            {
                _logger.LogDebug("Control write dropped: port not open ({Command})", command);
                return;
            }
            try
            {
                p.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Control write failed: {Message}", ex.Message);
            }
        }
    }

    /// <summary>Request a fresh configuration dump from the firmware ("gain" command).</summary>
    public void RefreshConfiguration() => SendAscii("gain");

    /// <summary>
    /// Apply a set of configuration values. Tuning parameters are sent as
    /// ASCII commands on the control port (the firmware's binary interface
    /// only handles 0x0017). Afterwards the config is refreshed.
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
        if (patch.SignalLossBehavior.HasValue) SendAscii($"loss {(int)patch.SignalLossBehavior.Value}");
        if (patch.ManualMode.HasValue)
        {
            SendAscii(patch.ManualMode.Value ? "dac 1.65" : "run");
        }

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
        _requestedTelemetryPort = null;
        _requestedControlPort = null;
        CloseTelemetryPort();
        CloseControlPort();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
