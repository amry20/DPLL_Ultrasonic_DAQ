using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.IO.Ports;
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
/// Manages the single serial link to the DPLL firmware. The COM port carries
/// ONLY binary opcode packets:
/// <list type="bullet">
/// <item><b>Outbound</b> — SET/GET opcodes for every parameter, plus the
/// stream-enable command (0x0017) that turns on the telemetry stream.</item>
/// <item><b>Inbound</b> — the 0x0019 status stream and GET responses.</item>
/// </list>
/// There is no ASCII traffic on this port: the firmware's ASCII debug console
/// runs on a separate hardware UART (DebugPort), so all host control must use
/// binary opcodes. Incoming bytes are parsed with <see cref="DpllProtocol"/>.
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

    // Paced GET sequence: the firmware parses one frame per loop cycle, so we
    // send ONE request and wait for its reply before sending the next. This
    // keeps each burst tiny (never overflows the USB CDC RX buffer) and leaves
    // the firmware control loop free to keep tracking.
    private readonly object _pendingLock = new();
    private readonly Dictionary<ushort, TaskCompletionSource> _pendingGets = new();
    private readonly Dictionary<ushort, TaskCompletionSource<byte[]>> _rawPending = new();
    private readonly SemaphoreSlim _rawRequestLock = new(1, 1);
    private readonly SemaphoreSlim _configurationLock = new(1, 1);

    private static readonly ushort[] ConfigGetOpcodeOrder =
    {
        Opcode.GET_VERSION, Opcode.GET_GENERATOR_SOURCE,
        Opcode.GET_KP, Opcode.GET_KI, Opcode.GET_KD,
        Opcode.GET_CENTER_VOLTAGE, Opcode.GET_TARGET_PHASE, Opcode.GET_MAX_SLEW,
        Opcode.GET_LOOP_PERIOD, Opcode.GET_LOCK_THRESHOLD,
        Opcode.GET_LOCK_HOLD_CYCLES, Opcode.GET_LOCK_MEMORY_TIMEOUT,
        Opcode.GET_STREAM_PERIOD, Opcode.GET_SIGNAL_LOSS_BEHAVIOR,
        Opcode.GET_MANUAL_MODE,
        Opcode.GET_DDS_FREQ_BOUNDS, Opcode.GET_DDS_CENTER_FREQ,
        Opcode.GET_DDS_MCO_CLOCK,
    };

    public event Action<DpllTelemetry>? TelemetryReceived;
    public event Action<DeviceConnectionState, string?>? ConnectionChanged;
    public event Action<DpllConfiguration>? ConfigurationReceived;
    public event Action<string, bool>? EepromActionCompleted; // (action: "save" | "load" | "reset", success)

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
    public DpllConfiguration? Configuration => _config?.Copy();

    /// <summary>True when the telemetry stream is considered fresh (recent packet received).</summary>
    public bool IsStreamFresh => _latest != null && (DateTimeOffset.UtcNow - _lastStreamAt).TotalMilliseconds < _options.StreamTimeoutMs;

    /// <summary>List detected serial ports.</summary>
    public static IReadOnlyList<string> GetAvailablePorts() => SerialPort.GetPortNames();

    // ------------------------------------------------------------------
    // Connection management
    // ------------------------------------------------------------------

    /// <summary>Open the serial link (binary opcode traffic only).</summary>
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

                _readCts = new CancellationTokenSource();
                _readTask = Task.Run(() => ReadLoopAsync(portObj, _readCts.Token));

                _streamEnabled = false;
                _logger.LogInformation("Connected to {Port}. Running startup sequence: version probe → config refresh → stream enable.", port);
                UpdateConnectionState();

                // Startup order: 1) GET_VERSION probe first to confirm the binary
                // link is alive, 2) pull configuration via PACED GET requests
                // (retry the whole set if any reply is lost), 3) only at the very
                // end enable the telemetry stream so firmware is idle during the
                // queries and every reply is the direct answer to a request.
                _ = RunStartupSequenceAsync();
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
        // Never reuse another connection's version, gain bank, or optional capabilities.
        _config = null;
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
    // Reading — binary packets only (the ASCII debug console runs on a
    // separate firmware UART; this port never carries ASCII traffic).
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

                _logger.LogDebug("RX {N} bytes: {Hex}", n, Convert.ToHexString(chunk, 0, n));

                for (int i = 0; i < n; i++)
                {
                    _rxBuffer.Add(chunk[i]);

                    // Safety bound: if we never form a valid frame, drop the
                    // buffer so it cannot grow without limit.
                    if (_rxBuffer.Count > DpllProtocol.MaxPacketSize)
                    {
                        _logger.LogWarning("Binary buffer overflowed ({Count} bytes) — dropping as invalid frame", _rxBuffer.Count);
                        _rxBuffer.Clear();
                    }
                }

                // Parse any complete packets now available.
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
                if (consumed <= 0)
                {
                    break; // need more data (consumed == 0) or nothing useful
                }
                offset += consumed;
                continue;
            }

            _logger.LogDebug("Parsed opcode 0x{Op:X4} payload {Len} bytes", opcode, payload.Length);

            if (opcode is Opcode.ARM_RAW_WINDOW or Opcode.GET_RAW_WINDOW_META or Opcode.GET_RAW_WINDOW_PAGE)
            {
                // Snapshot every response; span storage is owned by the RX parser.
                TaskCompletionSource<byte[]>? pending;
                lock (_pendingLock)
                {
                    _rawPending.Remove(opcode, out pending);
                }
                pending?.TrySetResult(payload.ToArray());
                offset += consumed;
                continue;
            }
            if (IsConfigGet(opcode) && !HasPendingGet(opcode))
            {
                // A late optional reply must not turn an unanswered capability
                // into a default or mutate a snapshot after it was published.
                _logger.LogDebug("Ignoring unsolicited or expired GET reply 0x{Op:X4}.", opcode);
                offset += consumed;
                continue;
            }
            if (!IsValidConfigReply(opcode, payload))
            {
                _logger.LogWarning("Ignoring malformed configuration reply 0x{Op:X4}.", opcode);
                offset += consumed;
                continue;
            }

            switch (opcode)
            {
                case Opcode.STREAM_DPLL_STATUS:
                {
                    var telemetry = DpllProtocol.DecodeStatusPayload(payload, DateTimeOffset.UtcNow);
                    _latest = telemetry;
                    _lastStreamAt = DateTimeOffset.UtcNow;
                    try { TelemetryReceived?.Invoke(telemetry); }
                    catch (Exception ex) { _logger.LogError(ex, "TelemetryReceived handler threw."); }
                    break;
                }

                case Opcode.GET_VERSION:
                {
                    // The firmware answers with the version string as a
                    // null-terminated ASCII payload (e.g. "DPLL v1.0\0").
                    // Read up to the '\0' so trailing garbage is ignored.
                    int len = payload.IndexOf((byte)0);
                    if (len < 0) len = payload.Length;
                    var version = Encoding.ASCII.GetString(payload.Slice(0, len));
                    _logger.LogInformation("GET_VERSION reply received: '{Version}'", version);
                    SetConfigValue(c => c.FirmwareVersion = version);
                    break;
                }

                // --- GET responses: fold each value into the config snapshot ---
                // (Read the span into locals first — a ref struct cannot be
                // captured by the SetConfigValue lambda.)
                case Opcode.GET_KP:
                { var v = ReadFloat(payload); SetConfigValue(c => c.Kp = v); break; }
                case Opcode.GET_KI:
                { var v = ReadFloat(payload); SetConfigValue(c => c.Ki = v); break; }
                case Opcode.GET_KD:
                { var v = ReadFloat(payload); SetConfigValue(c => c.Kd = v); break; }
                case Opcode.GET_CENTER_VOLTAGE:
                { var v = ReadFloat(payload); SetConfigValue(c => c.CenterVoltage = v); break; }
                case Opcode.GET_TARGET_PHASE:
                { var v = ReadFloat(payload); SetConfigValue(c => c.TargetPhase = v); break; }
                case Opcode.GET_MAX_SLEW:
                { var v = ReadFloat(payload); SetConfigValue(c => c.MaxSlew = v); break; }
                case Opcode.GET_LOOP_PERIOD:
                { var v = ReadUInt32(payload); SetConfigValue(c => c.LoopPeriodMs = v); break; }
                case Opcode.GET_LOCK_THRESHOLD:
                { var v = ReadFloat(payload); SetConfigValue(c => c.LockThresholdNs = v); break; }
                case Opcode.GET_LOCK_HOLD_CYCLES:
                { var v = ReadUInt32(payload); SetConfigValue(c => c.LockHoldCycles = v); break; }
                case Opcode.GET_LOCK_MEMORY_TIMEOUT:
                { var v = ReadUInt32(payload); SetConfigValue(c => c.LockMemoryTimeoutMs = v); break; }
                case Opcode.GET_STREAM_PERIOD:
                { var v = ReadUInt32(payload); SetConfigValue(c => c.StreamPeriodMs = v); break; }
                case Opcode.GET_SIGNAL_LOSS_BEHAVIOR:
                { var v = payload.Length > 0 ? payload[0] : 0; SetConfigValue(c => c.SignalLossBehavior = v); break; }
                case Opcode.GET_LOOP_ENABLE:
                case Opcode.GET_MANUAL_MODE:
                {
                    bool v = payload.Length > 0 && payload[0] != 0;
                    if (opcode == Opcode.GET_MANUAL_MODE)
                    {
                        SetConfigValue(c => c.ManualMode = v);
                    }
                    break;
                }
                case Opcode.GET_GENERATOR_SOURCE:
                {
                    int src = payload.Length > 0 ? payload[0] : 0;
                    SetConfigValue(c => c.GeneratorSource = src);
                    break;
                }
                case Opcode.GET_DDS_FREQ_BOUNDS:
                {
                    if (payload.Length >= 8)
                    {
                        var minF = ReadFloat(payload.Slice(0, 4));
                        var maxF = ReadFloat(payload.Slice(4, 4));
                        SetConfigValue(c => { c.DdsMinFreq = minF; c.DdsMaxFreq = maxF; });
                    }
                    break;
                }
                case Opcode.GET_DDS_CENTER_FREQ:
                {
                    var v = ReadFloat(payload);
                    SetConfigValue(c => c.DdsCenterFreq = v);
                    break;
                }
                case Opcode.GET_DDS_MCO_CLOCK:
                {
                    int clk = payload.Length > 0 ? payload[0] : 0;
                    SetConfigValue(c => c.DdsMcoClock = clk);
                    break;
                }
                case Opcode.GET_CAPTURE_EDGE:
                {
                    int refEdge = payload[0];
                    int zcdEdge = payload[1];
                    SetConfigValue(c => { c.ReferenceCaptureEdge = refEdge; c.ZcdCaptureEdge = zcdEdge; });
                    break;
                }
                case Opcode.GET_TRACKING_DIR:
                {
                    int direction = payload[0];
                    SetConfigValue(c => c.TrackingDirection = direction);
                    break;
                }
                case Opcode.GET_VOLTAGE:
                    // Not part of the config snapshot (telemetry covers DAC V).
                    break;

                case Opcode.GET_ALLOW_SEND_STREAM:
                {
                    bool streamOn = payload.Length > 0 && payload[0] != 0;
                    _logger.LogInformation("Stream ACK from firmware: allowed={StreamOn}", streamOn);
                    CompletePendingGet(opcode);
                    break;
                }

                case Opcode.GET_EEPROM_STATUS:
                {
                    bool eepromOk = payload.Length > 0 && payload[0] != 0;
                    _logger.LogInformation("EEPROM status from firmware: {Status}", eepromOk ? "Connected & OK" : "Not responding");
                    CompletePendingGet(opcode);
                    break;
                }

                case Opcode.SAVE_CONFIG_EEPROM:
                {
                    bool ok = payload.Length > 0 && payload[0] != 0;
                    _logger.LogInformation("SAVE_CONFIG_EEPROM reply from firmware: {Result}", ok ? "SUCCESS" : "FAILED");
                    CompletePendingGet(opcode);
                    try { EepromActionCompleted?.Invoke("save", ok); }
                    catch (Exception ex) { _logger.LogError(ex, "EepromActionCompleted handler threw."); }
                    break;
                }

                case Opcode.LOAD_CONFIG_EEPROM:
                {
                    bool ok = payload.Length > 0 && payload[0] != 0;
                    _logger.LogInformation("LOAD_CONFIG_EEPROM reply from firmware: {Result}", ok ? "SUCCESS" : "FAILED");
                    CompletePendingGet(opcode);
                    try { EepromActionCompleted?.Invoke("load", ok); }
                    catch (Exception ex) { _logger.LogError(ex, "EepromActionCompleted handler threw."); }
                    RefreshConfiguration();
                    break;
                }

                case Opcode.RESET_CONFIG_EEPROM:
                {
                    bool ok = payload.Length > 0 && payload[0] != 0;
                    _logger.LogInformation("RESET_CONFIG_EEPROM reply from firmware: {Result}", ok ? "SUCCESS" : "FAILED");
                    CompletePendingGet(opcode);
                    try { EepromActionCompleted?.Invoke("reset", ok); }
                    catch (Exception ex) { _logger.LogError(ex, "EepromActionCompleted handler threw."); }
                    RefreshConfiguration();
                    break;
                }

                default:
                    _logger.LogInformation("RX binary opcode 0x{Opcode:X4} ({Len} payload bytes)", opcode, payload.Length);
                    break;
            }

            // A paced GET/SET-ACK waiter may be blocked on this reply — release it.
            // GET_ALLOW_SEND_STREAM handled its own CompletePendingGet in its case above.
            if (IsConfigGet(opcode))
            {
                CompletePendingGet(opcode);
            }

            offset += consumed;
        }

        if (offset > 0)
        {
            _rxBuffer.RemoveRange(0, offset);
        }
    }

    private void SetConfigValue(Action<DpllConfiguration> apply)
    {
        _config ??= new DpllConfiguration();
        apply(_config);
    }

    private void PublishConfiguration()
    {
        var config = _config;
        if (config is null) return;
        // Publish one coherent readback, rather than combining new source with old gains.
        try { ConfigurationReceived?.Invoke(config.Copy()); }
        catch (Exception ex) { _logger.LogError(ex, "ConfigurationReceived handler threw."); }
    }

    private static bool IsValidConfigReply(ushort opcode, ReadOnlySpan<byte> payload) => opcode switch
    {
        Opcode.GET_VERSION => payload.Length > 0,
        Opcode.GET_CAPTURE_EDGE => payload.Length == 2 && payload[0] <= 2 && payload[1] <= 2,
        Opcode.GET_TRACKING_DIR => payload.Length == 1 && payload[0] <= 1,
        Opcode.GET_GENERATOR_SOURCE => payload.Length == 1 && payload[0] <= 2,
        Opcode.GET_DDS_MCO_CLOCK => payload.Length == 1 && payload[0] <= 6,
        Opcode.GET_SIGNAL_LOSS_BEHAVIOR => payload.Length == 1 && payload[0] <= 2,
        Opcode.GET_MANUAL_MODE => payload.Length == 1 && payload[0] <= 1,
        Opcode.GET_DDS_FREQ_BOUNDS => payload.Length == 8,
        Opcode.GET_KP or Opcode.GET_KI or Opcode.GET_KD or Opcode.GET_CENTER_VOLTAGE or
        Opcode.GET_TARGET_PHASE or Opcode.GET_MAX_SLEW or Opcode.GET_LOOP_PERIOD or
        Opcode.GET_LOCK_THRESHOLD or Opcode.GET_LOCK_HOLD_CYCLES or Opcode.GET_LOCK_MEMORY_TIMEOUT or
        Opcode.GET_STREAM_PERIOD or Opcode.GET_DDS_CENTER_FREQ => payload.Length == 4,
        _ => true
    };

    private static bool IsConfigGet(ushort opcode) =>
        Array.IndexOf(ConfigGetOpcodeOrder, opcode) >= 0 ||
        opcode is Opcode.GET_CAPTURE_EDGE or Opcode.GET_TRACKING_DIR;

    private bool HasPendingGet(ushort opcode)
    {
        lock (_pendingLock) { return _pendingGets.ContainsKey(opcode); }
    }

    private static float ReadFloat(ReadOnlySpan<byte> payload) =>
        payload.Length >= 4 ? BinaryPrimitives.ReadSingleLittleEndian(payload) : 0f;

    private static uint ReadUInt32(ReadOnlySpan<byte> payload) =>
        payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0u;

    // ------------------------------------------------------------------
    // Opt-in raw-capture diagnostic. No continuous 35 kHz USB streaming:
    // firmware freezes one complete paired REF/ZCD window and pages it out
    // AFTER the pulse. These requests NEVER change controller gains or timing.
    // ------------------------------------------------------------------
    private async Task<byte[]> RequestRawAsync(ushort opcode, byte[] payload)
    {
        await _rawRequestLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_port is not { IsOpen: true })
                throw new InvalidOperationException("DPLL serial connection is not open.");
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingLock) { _rawPending[opcode] = tcs; }
            try
            {
                SendBinary(opcode, payload);
                return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(2000)).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new InvalidOperationException(
                    "No raw-capture reply: flash the matching diagnostic firmware, or check the serial link.", ex);
            }
            finally
            {
                lock (_pendingLock) { _rawPending.Remove(opcode); }
            }
        }
        finally { _rawRequestLock.Release(); }
    }

    public async Task<bool> ArmRawWindowAsync()
    {
        var reply = await RequestRawAsync(Opcode.ARM_RAW_WINDOW, Array.Empty<byte>());
        return reply.Length == 1 && reply[0] == 1;
    }

    public async Task<RawWindowInfo> GetRawWindowInfoAsync()
    {
        var b = await RequestRawAsync(Opcode.GET_RAW_WINDOW_META, Array.Empty<byte>());
        if (b.Length != 45 || b[0] > 2)
            throw new InvalidDataException("Malformed raw capture metadata (expected 45 bytes).");
        return new RawWindowInfo
        {
            State = b[0],
            WindowId = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(1, 4)),
            SampleCount = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(5, 2)),
            AcceptedCount = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(7, 2)),
            CaptureMillis = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(9, 4)),
            LoopPeriodMs = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(13, 2)),
            Flags = b[15], LockStatus = b[16],
            FilteredPhaseNs = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(17, 4)),
            NoiseMadDeg = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(21, 4)),
            OutlierRatio = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(25, 4)),
            SlopeDegPerMs = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(29, 4)),
            TargetPhaseNs = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(33, 4)),
            DacBefore = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(37, 4)),
            DacAfter = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(41, 4)),
        };
    }

    private async Task<byte[]> GetRawPageAsync(uint windowId, ushort offset)
    {
        var request = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(0, 4), windowId);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4, 2), offset);
        var reply = await RequestRawAsync(Opcode.GET_RAW_WINDOW_PAGE, request);
        if (reply.Length < 7 ||
            BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(0, 4)) != windowId ||
            BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(4, 2)) != offset ||
            reply[6] == 0 || reply[6] > 32 || reply.Length != 7 + reply[6] * 12)
            throw new InvalidDataException("Raw capture page missing, malformed, or overwritten.");
        return reply;
    }

    private static string CsvField(string label)
    {
        // Escape both CSV syntax and spreadsheet-formula injection.
        label = (label ?? "").Trim();
        if (label.Length > 80) label = label[..80];
        if (label.Length > 0 && "=+-@".Contains(label[0])) label = "'" + label;
        return "\"" + label.Replace("\"", "\"\"") + "\"";
    }

    public async Task<string> ExportRawWindowCsvAsync(string label)
    {
        var meta = await GetRawWindowInfoAsync();
        if (meta.State != 2 || meta.SampleCount < 2)
            throw new InvalidOperationException("No capture ready. Arm the NEXT tracking window, run a pulse, then retry.");
        if (meta.SampleCount > 1024)
            throw new InvalidDataException("Firmware reported more than 1024 raw samples.");

        var csv = new StringBuilder(180000);
        csv.AppendLine("Condition,WindowId,SampleIndex,RelativeUs,ZcdTimerTicks,PeriodTicks,PhaseTicks,RawPhaseNs,RawPhaseDegrees,ReferenceFrequencyHz,WindowSampleCount,AcceptedCount,Overflow,Trustworthy,PhaseComputed,LargeShift,PidApplied,FilteredWindowPhaseNs,NoiseMadDeg,OutlierRatio,SlopeDegPerMs,LockStatusAfterWindow,TargetPhaseNs,DacBeforeV,DacAfterV,CaptureMillis,LoopPeriodMs");
        var condition = CsvField(label);
        uint firstTick = 0;
        bool haveFirst = false;
        for (ushort offset = 0; offset < meta.SampleCount;)
        {
            var page = await GetRawPageAsync(meta.WindowId, offset);
            int count = page[6];
            if (offset + count > meta.SampleCount)
                throw new InvalidDataException("Raw page exceeds declared window sample count.");
            for (int i = 0; i < count; i++)
            {
                var sample = page.AsSpan(7 + i * 12, 12);
                int phaseTicks = BinaryPrimitives.ReadInt32LittleEndian(sample.Slice(0, 4));
                uint period = BinaryPrimitives.ReadUInt32LittleEndian(sample.Slice(4, 4));
                uint tick = BinaryPrimitives.ReadUInt32LittleEndian(sample.Slice(8, 4));
                if (!haveFirst) { firstTick = tick; haveFirst = true; }
                double relativeUs = unchecked(tick - firstTick) / 84.0;
                double phaseNs = phaseTicks * (1e9 / 84000000.0);
                double phaseDeg = period > 0 ? phaseTicks * 360.0 / period : 0;
                double freqHz = period > 0 ? 84000000.0 / period : 0;
                // Invariant format for scientific CSV portability.
                csv.Append(condition).Append(',').Append(meta.WindowId).Append(',')
                   .Append(offset + i).Append(',')
                   .Append(relativeUs.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                   .Append(tick).Append(',').Append(period).Append(',').Append(phaseTicks).Append(',')
                   .Append(phaseNs.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                   .Append(phaseDeg.ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                   .Append(freqHz.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                   .Append(meta.SampleCount).Append(',').Append(meta.AcceptedCount).Append(',')
                   .Append(meta.Overflow ? 1 : 0).Append(',').Append(meta.Trustworthy ? 1 : 0).Append(',')
                   .Append(meta.PhaseComputed ? 1 : 0).Append(',').Append(meta.LargeShift ? 1 : 0).Append(',')
                   .Append(meta.PidApplied ? 1 : 0).Append(',')
                   .Append(meta.FilteredPhaseNs.ToString("G9", CultureInfo.InvariantCulture)).Append(',')
                   .Append(meta.NoiseMadDeg.ToString("G9", CultureInfo.InvariantCulture)).Append(',')
                   .Append(meta.OutlierRatio.ToString("G9", CultureInfo.InvariantCulture)).Append(',')
                   .Append(meta.SlopeDegPerMs.ToString("G9", CultureInfo.InvariantCulture)).Append(',')
                   .Append(meta.LockStatus).Append(',')
                   .Append(meta.TargetPhaseNs.ToString("G9", CultureInfo.InvariantCulture)).Append(',')
                   .Append(meta.DacBefore.ToString("G9", CultureInfo.InvariantCulture)).Append(',')
                   .Append(meta.DacAfter.ToString("G9", CultureInfo.InvariantCulture)).Append(',')
                   .Append(meta.CaptureMillis).Append(',').Append(meta.LoopPeriodMs).AppendLine();
            }
            offset = checked((ushort)(offset + count));
        }
        // Fail closed if an arm operation overwrote the snapshot during paging.
        var after = await GetRawWindowInfoAsync();
        if (after.State != 2 || after.WindowId != meta.WindowId || after.SampleCount != meta.SampleCount)
            throw new InvalidOperationException("Raw snapshot changed during export. Re-capture the window.");
        return csv.ToString();
    }

    // ------------------------------------------------------------------
    // Writing
    // ------------------------------------------------------------------

    /// <summary>Enable or disable the firmware telemetry stream (opcode 0x0017).
    /// Returns true when firmware confirms the state change via GET_ALLOW_SEND_STREAM reply.</summary>
    public void EnableStream(bool enable) => _ = EnableStreamAsync(enable);

    private async Task EnableStreamAsync(bool enable)
    {
        _streamEnabled = enable;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock) { _pendingGets[Opcode.GET_ALLOW_SEND_STREAM] = tcs; }
        SendBinary(Opcode.SET_ALLOW_SEND_STREAM, [enable ? (byte)1 : (byte)0]);
        try
        {
            await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (_pendingLock) { _pendingGets.Remove(Opcode.GET_ALLOW_SEND_STREAM); }
            _logger.LogWarning("SET_ALLOW_SEND_STREAM: no ACK from firmware within 500 ms.");
        }
    }

    /// <summary>
    /// Ask the firmware for a fresh copy of every setting. Requests are PACED:
    /// each GET is sent only after the previous reply arrived, so the USB CDC
    /// RX buffer never has to hold a multi-packet burst.
    /// Returns true when every GET was answered.
    /// </summary>
    public void RefreshConfiguration() => _ = RefreshConfigurationAsync();

    private async Task<bool> RefreshConfigurationAsync()
    {
        await _configurationLock.WaitAsync().ConfigureAwait(false);
        try { return await RefreshConfigurationCoreAsync().ConfigureAwait(false); }
        finally { _configurationLock.Release(); }
    }

    // Caller holds _configurationLock. Optional GETs do not cause startup retries
    // on older firmware whose GET_VERSION may still be the ambiguous v2.0.0.
    private async Task<bool> RefreshConfigurationCoreAsync()
    {
        if (_disposed || _port is not { IsOpen: true }) return false;
        var sessionPort = _port;
        _config ??= new DpllConfiguration();
        _config.ReadbackComplete = false;
        PublishConfiguration();
        bool allOk = true;
        foreach (var opcode in ConfigGetOpcodeOrder)
        {
            if (!ReferenceEquals(sessionPort, _port)) { allOk = false; break; }
            if (!await RequestGetAsync(opcode).ConfigureAwait(false)) allOk = false;
            if (_disposed || _port is not { IsOpen: true }) break;
        }
        if (_config is null) return false;
        _config.ReferenceCaptureEdge = null;
        _config.ZcdCaptureEdge = null;
        _config.TrackingDirection = null;
        bool edgesRead = await RequestGetAsync(Opcode.GET_CAPTURE_EDGE, optional: true).ConfigureAwait(false);
        if (_config is not null && !edgesRead)
        {
            _config.ReferenceCaptureEdge = null;
            _config.ZcdCaptureEdge = null;
        }
        bool directionRead = await RequestGetAsync(Opcode.GET_TRACKING_DIR, optional: true).ConfigureAwait(false);
        if (_config is not null && !directionRead) _config.TrackingDirection = null;
        if (_config is null) return false;
        _config.ReadbackComplete = allOk && !_disposed && ReferenceEquals(sessionPort, _port) && _port is { IsOpen: true };
        PublishConfiguration();
        return _config.ReadbackComplete;
    }

    private async Task<bool> RequestGetAsync(ushort opcode, bool optional = false, int timeoutMs = 500)
    {
        var sessionPort = _port;
        if (_disposed || sessionPort is not { IsOpen: true }) return false;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock) { _pendingGets[opcode] = tcs; }
        SendBinary(opcode, default);
        try
        {
            await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)).ConfigureAwait(false);
            return ReferenceEquals(sessionPort, _port) && sessionPort.IsOpen;
        }
        catch (TimeoutException)
        {
            if (optional)
                _logger.LogInformation("Optional GET 0x{Opcode:X4} unanswered; setting remains unknown.", opcode);
            else
                _logger.LogWarning("GET 0x{Opcode:X4}: no reply within {Timeout} ms.", opcode, timeoutMs);
            return false;
        }
        finally
        {
            lock (_pendingLock)
            {
                if (_pendingGets.TryGetValue(opcode, out var pending) && ReferenceEquals(pending, tcs))
                    _pendingGets.Remove(opcode);
            }
        }
    }

    /// <summary>Send a GET_VERSION probe. Returns true when the firmware replied.</summary>
    public void ProbeFirmware() => _ = ProbeFirmwareAsync();

    private async Task<bool> ProbeFirmwareAsync()
    {
        await _configurationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var ok = await RequestGetAsync(Opcode.GET_VERSION, timeoutMs: 3000).ConfigureAwait(false);
            PublishConfiguration();
            return ok;
        }
        finally { _configurationLock.Release(); }
    }

    /// <summary>Release any paced GET waiter blocked on this opcode's reply.</summary>
    private void CompletePendingGet(ushort opcode)
    {
        lock (_pendingLock)
        {
            if (_pendingGets.TryGetValue(opcode, out var tcs))
            {
                _pendingGets.Remove(opcode);
                tcs.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Connect-time startup sequence, in this order:
    ///   1. GET_VERSION probe — confirms the binary link is alive.
    ///   2. PACED config refresh — retried from scratch if any GET reply is lost.
    ///   3. Enable telemetry stream — last, only after config is complete.
    /// </summary>
    private async Task RunStartupSequenceAsync()
    {
        try
        {
            // 1) Version probe first. If the firmware never answers, the link is
            //    not usable — do not request config or start the stream.
            if (!await ProbeFirmwareAsync().ConfigureAwait(false))
            {
                _logger.LogWarning("Startup aborted: firmware did not answer GET_VERSION.");
                return;
            }

            // 2) Config refresh with full retry when a reply is lost.
            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (await RefreshConfigurationAsync().ConfigureAwait(false))
                {
                    break;
                }
                if (attempt < maxAttempts)
                {
                    _logger.LogWarning("Startup: some config GETs were lost (attempt {Attempt}/{Max}) — refreshing again.", attempt, maxAttempts);
                }
                else
                {
                    _logger.LogWarning("Startup: config refresh still incomplete after {Max} attempts — continuing with partial config.", maxAttempts);
                }
            }

            // 3) Telemetry stream — last, so firmware stays idle during the queries.
            if (!_disposed)
            {
                await EnableStreamAsync(true).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!_disposed)
        {
            _logger.LogWarning("Startup sequence failed: {Message}", ex.Message);
        }
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

    /// <summary>
    /// Apply a set of configuration values. Every setting is sent as a binary
    /// SET opcode — the firmware's ASCII console lives on a separate debug
    /// UART, so the host must not write ASCII to this port.
    /// Afterwards the config is refreshed via paced GET opcodes.
    /// </summary>
    public async Task<bool> ApplyConfigurationAsync(DpllConfigurationPatch patch)
    {
        await _configurationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_port is not { IsOpen: true } || _config is not { ReadbackComplete: true })
                throw new InvalidOperationException("Read a complete configuration before applying changes.");
            ValidatePatch(patch, _config);
            bool hasGainEdits = patch.Kp.HasValue || patch.Ki.HasValue || patch.Kd.HasValue || patch.MaxSlew.HasValue;
            int readSource = _config.GeneratorSource;
            if (!await RequestGetAsync(Opcode.GET_GENERATOR_SOURCE).ConfigureAwait(false))
                throw new InvalidOperationException("Cannot confirm the active generator source; no settings were written.");
            if (_config is null || _config.GeneratorSource != readSource ||
                (patch.ExpectedGeneratorSource.HasValue && patch.ExpectedGeneratorSource != _config.GeneratorSource))
            {
                await RefreshConfigurationCoreAsync().ConfigureAwait(false);
                throw new InvalidOperationException("Generator source changed. Read its settings and edit the active gain bank again.");
            }
            if (hasGainEdits && patch.ExpectedGeneratorSource != _config.GeneratorSource)
                throw new InvalidOperationException("Gain edits must identify the generator source read before editing.");
            bool sourceChanged = patch.GeneratorSource.HasValue && patch.GeneratorSource != _config.GeneratorSource;
            if (sourceChanged && hasGainEdits)
                throw new InvalidOperationException("Apply the source change alone, read its gains, then edit Kp/Ki/Kd/MaxSlew.");

            _config.ReadbackComplete = false;
            PublishConfiguration();
            // The source must change and be read back BEFORE any active-bank setters.
            if (sourceChanged)
                await SetAndReadAsync(Opcode.SET_GENERATOR_SOURCE, [(byte)patch.GeneratorSource!.Value],
                    Opcode.GET_GENERATOR_SOURCE, () => _config?.GeneratorSource == patch.GeneratorSource).ConfigureAwait(false);

            await SetFloatIfChangedAsync(patch.Kp, () => _config!.Kp, Opcode.SET_KP, Opcode.GET_KP).ConfigureAwait(false);
            await SetFloatIfChangedAsync(patch.Ki, () => _config!.Ki, Opcode.SET_KI, Opcode.GET_KI).ConfigureAwait(false);
            await SetFloatIfChangedAsync(patch.Kd, () => _config!.Kd, Opcode.SET_KD, Opcode.GET_KD).ConfigureAwait(false);
            await SetFloatIfChangedAsync(patch.CenterVoltage, () => _config!.CenterVoltage, Opcode.SET_CENTER_VOLTAGE, Opcode.GET_CENTER_VOLTAGE).ConfigureAwait(false);
            await SetFloatIfChangedAsync(patch.TargetPhase, () => _config!.TargetPhase, Opcode.SET_TARGET_PHASE, Opcode.GET_TARGET_PHASE).ConfigureAwait(false);
            await SetFloatIfChangedAsync(patch.MaxSlew, () => _config!.MaxSlew, Opcode.SET_MAX_SLEW, Opcode.GET_MAX_SLEW).ConfigureAwait(false);
            await SetUIntIfChangedAsync(patch.LoopPeriodMs, () => _config!.LoopPeriodMs, Opcode.SET_LOOP_PERIOD, Opcode.GET_LOOP_PERIOD).ConfigureAwait(false);
            await SetFloatIfChangedAsync(patch.LockThresholdNs, () => _config!.LockThresholdNs, Opcode.SET_LOCK_THRESHOLD, Opcode.GET_LOCK_THRESHOLD).ConfigureAwait(false);
            await SetUIntIfChangedAsync(patch.LockHoldCycles, () => _config!.LockHoldCycles, Opcode.SET_LOCK_HOLD_CYCLES, Opcode.GET_LOCK_HOLD_CYCLES).ConfigureAwait(false);
            await SetUIntIfChangedAsync(patch.LockMemoryTimeoutMs, () => _config!.LockMemoryTimeoutMs, Opcode.SET_LOCK_MEMORY_TIMEOUT, Opcode.GET_LOCK_MEMORY_TIMEOUT).ConfigureAwait(false);
            await SetUIntIfChangedAsync(patch.StreamPeriodMs, () => _config!.StreamPeriodMs, Opcode.SET_STREAM_PERIOD, Opcode.GET_STREAM_PERIOD).ConfigureAwait(false);
            if (patch.SignalLossBehavior.HasValue && patch.SignalLossBehavior != _config!.SignalLossBehavior)
                await SetAndReadAsync(Opcode.SET_SIGNAL_LOSS_BEHAVIOR, [(byte)patch.SignalLossBehavior.Value], Opcode.GET_SIGNAL_LOSS_BEHAVIOR,
                    () => _config?.SignalLossBehavior == patch.SignalLossBehavior).ConfigureAwait(false);
            if (patch.ManualMode.HasValue && patch.ManualMode != _config!.ManualMode)
                await SetAndReadAsync(Opcode.SET_MANUAL_MODE, [patch.ManualMode.Value ? (byte)1 : (byte)0], Opcode.GET_MANUAL_MODE,
                    () => _config?.ManualMode == patch.ManualMode).ConfigureAwait(false);
            // Bounds first: the firmware may clamp its center when new bounds are applied.
            if ((patch.DdsMinFreq.HasValue && !SameFloat(patch.DdsMinFreq.Value, _config!.DdsMinFreq)) ||
                (patch.DdsMaxFreq.HasValue && !SameFloat(patch.DdsMaxFreq.Value, _config!.DdsMaxFreq)))
            {
                float minF = (float)(patch.DdsMinFreq ?? _config!.DdsMinFreq);
                float maxF = (float)(patch.DdsMaxFreq ?? _config!.DdsMaxFreq);
                byte[] bounds = new byte[8];
                BinaryPrimitives.WriteSingleLittleEndian(bounds.AsSpan(0, 4), minF);
                BinaryPrimitives.WriteSingleLittleEndian(bounds.AsSpan(4, 4), maxF);
                await SetAndReadAsync(Opcode.SET_DDS_FREQ_BOUNDS, bounds, Opcode.GET_DDS_FREQ_BOUNDS,
                    () => _config is not null && SameFloat(_config.DdsMinFreq, minF) && SameFloat(_config.DdsMaxFreq, maxF)).ConfigureAwait(false);
            }
            await SetFloatIfChangedAsync(patch.DdsCenterFreq, () => _config!.DdsCenterFreq, Opcode.SET_DDS_CENTER_FREQ, Opcode.GET_DDS_CENTER_FREQ).ConfigureAwait(false);
            if (patch.DdsMcoClock.HasValue && patch.DdsMcoClock != _config!.DdsMcoClock)
                await SetAndReadAsync(Opcode.SET_DDS_MCO_CLOCK, [(byte)patch.DdsMcoClock.Value], Opcode.GET_DDS_MCO_CLOCK,
                    () => _config?.DdsMcoClock == patch.DdsMcoClock).ConfigureAwait(false);
            if (patch.ReferenceCaptureEdge.HasValue || patch.ZcdCaptureEdge.HasValue)
            {
                int refEdge = patch.ReferenceCaptureEdge ?? _config!.ReferenceCaptureEdge!.Value;
                int zcdEdge = patch.ZcdCaptureEdge ?? _config!.ZcdCaptureEdge!.Value;
                if (refEdge != _config!.ReferenceCaptureEdge || zcdEdge != _config.ZcdCaptureEdge)
                    await SetAndReadAsync(Opcode.SET_CAPTURE_EDGE, [(byte)refEdge, (byte)zcdEdge], Opcode.GET_CAPTURE_EDGE,
                        () => _config?.ReferenceCaptureEdge == refEdge && _config?.ZcdCaptureEdge == zcdEdge).ConfigureAwait(false);
            }
            if (patch.TrackingDirection.HasValue && patch.TrackingDirection != _config!.TrackingDirection)
                await SetAndReadAsync(Opcode.SET_TRACKING_DIR, [(byte)patch.TrackingDirection.Value], Opcode.GET_TRACKING_DIR,
                    () => _config?.TrackingDirection == patch.TrackingDirection).ConfigureAwait(false);

            if (!await RefreshConfigurationCoreAsync().ConfigureAwait(false))
                throw new InvalidOperationException("Settings were sent, but readback is incomplete. Read configuration before continuing.");
            return true;
        }
        finally
        {
            // A timeout after any SET leaves the UI disabled until a fresh coherent readback.
            PublishConfiguration();
            _configurationLock.Release();
        }
    }

    private static bool SameFloat(double a, double b) => (float)a == (float)b;

    private async Task SetAndReadAsync(ushort setter, byte[] payload, ushort getter, Func<bool> matches)
    {
        SendBinary(setter, payload);
        if (!await RequestGetAsync(getter).ConfigureAwait(false) || !matches())
            throw new InvalidOperationException($"Setting 0x{setter:X4} was not confirmed. Read configuration before retrying.");
    }

    private async Task SetFloatIfChangedAsync(double? value, Func<double> current, ushort setter, ushort getter)
    {
        if (value.HasValue && !SameFloat(value.Value, current()))
            await SetAndReadAsync(setter, DpllProtocol.EncodeFloat((float)value.Value), getter,
                () => _config is not null && SameFloat(value.Value, current())).ConfigureAwait(false);
    }

    private async Task SetUIntIfChangedAsync(uint? value, Func<uint> current, ushort setter, ushort getter)
    {
        if (value.HasValue && value.Value != current())
            await SetAndReadAsync(setter, DpllProtocol.EncodeUInt32(value.Value), getter,
                () => _config is not null && value.Value == current()).ConfigureAwait(false);
    }

    private static void ValidatePatch(DpllConfigurationPatch p, DpllConfiguration c)
    {
        static void Range(double? value, double min, double max, string name)
        {
            if (value.HasValue && (!double.IsFinite(value.Value) || value.Value < min || value.Value > max))
                throw new ArgumentException($"{name} must be between {min} and {max}.");
        }
        Range(p.Kp, 0, float.MaxValue, nameof(p.Kp));
        Range(p.Ki, 0, float.MaxValue, nameof(p.Ki));
        Range(p.Kd, 0, float.MaxValue, nameof(p.Kd));
        Range(p.CenterVoltage, 0, 3.3, nameof(p.CenterVoltage));
        Range(p.TargetPhase, -float.MaxValue, float.MaxValue, nameof(p.TargetPhase));
        Range(p.MaxSlew, float.Epsilon, float.MaxValue, nameof(p.MaxSlew));
        Range(p.LockThresholdNs, float.Epsilon, float.MaxValue, nameof(p.LockThresholdNs));
        Range(p.LoopPeriodMs, 1, 1000, nameof(p.LoopPeriodMs));
        Range(p.LockHoldCycles, 1, uint.MaxValue, nameof(p.LockHoldCycles));
        Range(p.StreamPeriodMs, 1, 65535, nameof(p.StreamPeriodMs));
        Range(p.SignalLossBehavior, 0, 2, nameof(p.SignalLossBehavior));
        Range(p.GeneratorSource, 0, 2, nameof(p.GeneratorSource));
        Range(p.ExpectedGeneratorSource, 0, 2, nameof(p.ExpectedGeneratorSource));
        Range(p.DdsMcoClock, 0, 6, nameof(p.DdsMcoClock));
        Range(p.ReferenceCaptureEdge, 0, 2, nameof(p.ReferenceCaptureEdge));
        Range(p.ZcdCaptureEdge, 0, 2, nameof(p.ZcdCaptureEdge));
        Range(p.TrackingDirection, 0, 1, nameof(p.TrackingDirection));
        Range(p.DdsMinFreq, 1000, 500000, nameof(p.DdsMinFreq));
        Range(p.DdsMaxFreq, 1000, 500000, nameof(p.DdsMaxFreq));
        Range(p.DdsCenterFreq, 1000, 500000, nameof(p.DdsCenterFreq));
        if ((p.DdsMinFreq ?? c.DdsMinFreq) >= (p.DdsMaxFreq ?? c.DdsMaxFreq))
            throw new ArgumentException("DDS minimum must be below its maximum.");
        if (p.DdsCenterFreq.HasValue && (p.DdsCenterFreq.Value < (p.DdsMinFreq ?? c.DdsMinFreq) || p.DdsCenterFreq.Value > (p.DdsMaxFreq ?? c.DdsMaxFreq)))
            throw new ArgumentException("DDS center must be within the configured bounds.");
        if ((p.ReferenceCaptureEdge.HasValue || p.ZcdCaptureEdge.HasValue) &&
            (!c.ReferenceCaptureEdge.HasValue || !c.ZcdCaptureEdge.HasValue))
            throw new InvalidOperationException("Capture-edge readback is unknown or unsupported. No default edge will be written.");
        if (p.TrackingDirection.HasValue && !c.TrackingDirection.HasValue)
            throw new InvalidOperationException("Tracking-direction readback is unknown or unsupported. No default direction will be written.");
    }

    /// <summary>Reset the loop: clear integrator, restart from center, enable.</summary>
    public void ResetLoop() => SendBinary(Opcode.RESET_LOOP, default);

    /// <summary>Shutdown the loop: DAC to 0 V, loop disabled.</summary>
    public void ShutdownLoop() => SendBinary(Opcode.SHUTDOWN_LOOP, default);

    /// <summary>Manual DAC voltage (disables the loop).</summary>
    public void SetManualVoltage(double volts) => SendBinary(Opcode.SET_VOLTAGE, DpllProtocol.EncodeFloat((float)volts));

    /// <summary>Manual DDS output frequency (disables the loop).</summary>
    public void SetManualFrequency(double freqHz) => SendBinary(Opcode.SET_DDS_MANUAL_FREQ, DpllProtocol.EncodeFloat((float)freqHz));

    /// <summary>Set DDS MCO clock preset (0-6).</summary>
    public void SetDdsMcoClock(int clockIndex) => SendBinary(Opcode.SET_DDS_MCO_CLOCK, [(byte)clockIndex]);

    /// <summary>Re-enable automatic control (manual mode off).</summary>
    public void RunLoop() => SendBinary(Opcode.SET_ENABLE_LOOP, [1]);

    /// <summary>Save current firmware configuration to 24LC16B EEPROM.</summary>
    public void SaveConfigurationToEeprom() => SendBinary(Opcode.SAVE_CONFIG_EEPROM, default);

    /// <summary>Load saved configuration from 24LC16B EEPROM and apply it.</summary>
    public void LoadConfigurationFromEeprom() => SendBinary(Opcode.LOAD_CONFIG_EEPROM, default);

    /// <summary>Reset configuration in 24LC16B EEPROM and revert controller to factory defaults.</summary>
    public void ResetEepromConfiguration() => SendBinary(Opcode.RESET_CONFIG_EEPROM, default);

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
