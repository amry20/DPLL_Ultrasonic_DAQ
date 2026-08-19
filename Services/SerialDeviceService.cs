using System.Buffers.Binary;
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

    private static readonly ushort[] ConfigGetOpcodeOrder =
    {
        Opcode.GET_KP, Opcode.GET_KI, Opcode.GET_KD,
        Opcode.GET_CENTER_VOLTAGE, Opcode.GET_TARGET_PHASE, Opcode.GET_MAX_SLEW,
        Opcode.GET_LOOP_PERIOD, Opcode.GET_LOCK_THRESHOLD,
        Opcode.GET_LOCK_HOLD_CYCLES, Opcode.GET_LOCK_MEMORY_TIMEOUT,
        Opcode.GET_STREAM_PERIOD, Opcode.GET_SIGNAL_LOSS_BEHAVIOR,
        Opcode.GET_MANUAL_MODE,
    };

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

                for (int i = 0; i < n; i++)
                {
                    _rxBuffer.Add(chunk[i]);

                    // Safety bound: if we never form a valid frame, drop the
                    // buffer so it cannot grow without limit.
                    if (_rxBuffer.Count > DpllProtocol.MaxPacketSize)
                    {
                        _logger.LogDebug("Binary buffer overflowed ({Count} bytes) — dropping as invalid frame", _rxBuffer.Count);
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
                if (consumed < 0)
                {
                    break; // need more data
                }
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
                case Opcode.GET_VOLTAGE:
                    // Not part of the config snapshot (telemetry covers DAC V).
                    break;

                default:
                    _logger.LogInformation("RX binary opcode 0x{Opcode:X4} ({Len} payload bytes)", opcode, payload.Length);
                    break;
            }

            // A paced GET waiter may be blocked on this reply — release it so
            // the next request in the sequence is sent right away.
            if (opcode == Opcode.GET_VERSION || Array.IndexOf(ConfigGetOpcodeOrder, opcode) >= 0)
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
        try { ConfigurationReceived?.Invoke(_config); }
        catch (Exception ex) { _logger.LogError(ex, "ConfigurationReceived handler threw."); }
    }

    private static float ReadFloat(ReadOnlySpan<byte> payload) =>
        payload.Length >= 4 ? BinaryPrimitives.ReadSingleLittleEndian(payload) : 0f;

    private static uint ReadUInt32(ReadOnlySpan<byte> payload) =>
        payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0u;

    // ------------------------------------------------------------------
    // Writing
    // ------------------------------------------------------------------

    /// <summary>Enable or disable the firmware telemetry stream (opcode 0x0017).</summary>
    public void EnableStream(bool enable)
    {
        _streamEnabled = enable;
        SendBinary(Opcode.SET_ALLOW_SEND_STREAM, [enable ? (byte)1 : (byte)0]);
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
        bool allOk = true;
        foreach (var opcode in ConfigGetOpcodeOrder)
        {
            if (_disposed)
            {
                break;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingLock) { _pendingGets[opcode] = tcs; }

            SendBinary(opcode, default);
            try
            {
                await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                lock (_pendingLock) { _pendingGets.Remove(opcode); }
                _logger.LogWarning("GET 0x{Opcode:X4}: no reply within 500 ms — skipping.", opcode);
                allOk = false;
            }
        }
        return allOk;
    }

    /// <summary>Send a GET_VERSION probe. Returns true when the firmware replied.</summary>
    public void ProbeFirmware() => _ = ProbeFirmwareAsync();

    private async Task<bool> ProbeFirmwareAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock) { _pendingGets[Opcode.GET_VERSION] = tcs; }

        SendBinary(Opcode.GET_VERSION, default);
        try
        {
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            lock (_pendingLock) { _pendingGets.Remove(Opcode.GET_VERSION); }
            _logger.LogWarning("GET_VERSION: no reply within 3000 ms — firmware did not respond on the binary link.");
            return false;
        }
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
                EnableStream(true);
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
    public void ApplyConfiguration(DpllConfigurationPatch patch)
    {
        if (patch.Kp.HasValue)
            SendBinary(Opcode.SET_KP, DpllProtocol.EncodeFloat((float)patch.Kp.Value));
        if (patch.Ki.HasValue)
            SendBinary(Opcode.SET_KI, DpllProtocol.EncodeFloat((float)patch.Ki.Value));
        if (patch.Kd.HasValue)
            SendBinary(Opcode.SET_KD, DpllProtocol.EncodeFloat((float)patch.Kd.Value));
        if (patch.CenterVoltage.HasValue)
            SendBinary(Opcode.SET_CENTER_VOLTAGE, DpllProtocol.EncodeFloat((float)patch.CenterVoltage.Value));
        if (patch.TargetPhase.HasValue)
            SendBinary(Opcode.SET_TARGET_PHASE, DpllProtocol.EncodeFloat((float)patch.TargetPhase.Value));
        if (patch.MaxSlew.HasValue)
            SendBinary(Opcode.SET_MAX_SLEW, DpllProtocol.EncodeFloat((float)patch.MaxSlew.Value));
        if (patch.LoopPeriodMs.HasValue)
            SendBinary(Opcode.SET_LOOP_PERIOD, DpllProtocol.EncodeUInt32(patch.LoopPeriodMs.Value));
        if (patch.LockThresholdNs.HasValue)
            SendBinary(Opcode.SET_LOCK_THRESHOLD, DpllProtocol.EncodeFloat((float)patch.LockThresholdNs.Value));
        if (patch.LockHoldCycles.HasValue)
            SendBinary(Opcode.SET_LOCK_HOLD_CYCLES, DpllProtocol.EncodeUInt32(patch.LockHoldCycles.Value));
        if (patch.LockMemoryTimeoutMs.HasValue)
            SendBinary(Opcode.SET_LOCK_MEMORY_TIMEOUT, DpllProtocol.EncodeUInt32(patch.LockMemoryTimeoutMs.Value));
        if (patch.StreamPeriodMs.HasValue)
            SendBinary(Opcode.SET_STREAM_PERIOD, DpllProtocol.EncodeUInt32(patch.StreamPeriodMs.Value));
        if (patch.SignalLossBehavior.HasValue)
            SendBinary(Opcode.SET_SIGNAL_LOSS_BEHAVIOR, [(byte)patch.SignalLossBehavior.Value]);
        if (patch.ManualMode.HasValue)
            SendBinary(Opcode.SET_MANUAL_MODE, [patch.ManualMode.Value ? (byte)1 : (byte)0]);

        RefreshConfiguration();
    }

    /// <summary>Reset the loop: clear integrator, restart from center, enable.</summary>
    public void ResetLoop() => SendBinary(Opcode.RESET_LOOP, default);

    /// <summary>Shutdown the loop: DAC to 0 V, loop disabled.</summary>
    public void ShutdownLoop() => SendBinary(Opcode.SHUTDOWN_LOOP, default);

    /// <summary>Manual DAC voltage (disables the loop).</summary>
    public void SetManualVoltage(double volts) => SendBinary(Opcode.SET_VOLTAGE, DpllProtocol.EncodeFloat((float)volts));

    /// <summary>Re-enable automatic control (manual mode off).</summary>
    public void RunLoop() => SendBinary(Opcode.SET_ENABLE_LOOP, [1]);

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
