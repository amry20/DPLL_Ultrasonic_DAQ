# DPLL Ultrasonic DAQ

Web-based data-acquisition and control UI for the **DPLL ultrasonic frequency-tracking** firmware running on an STM32F407 (see `D:\National Seismic Instrument\Release\Firmware\Source\DPLL_Ultrasonic_Frequency_Tracking`).

The application exposes a browser dashboard over **SignalR (WebSocket)** that shows live DPLL telemetry (reference frequency, phase error, DAC voltage, lock state), plots real-time trend charts, lets you tune the digital PLL loop (Kp / Ki / Kd, center voltage, target phase, slew rate, loop period, signal-loss behavior) plus drive the DAC manually, and records telemetry samples to CSV files for post-analysis.

> **100% offline.** All client libraries (ECharts, Inter font, SignalR) are vendored locally under `wwwroot/` — the dashboard works without internet access.

> **Architecture note — one binary serial interface.** The firmware exposes a single USB CDC serial port (`SerialUSB`) that carries **only binary opcode packets**:
>
> | Direction | Protocol | Use |
> |---|---|---|
> | Host → firmware | SET/GET opcodes | Tuning (`kp`/`ki`/`kd`/`center`/`target`/`slew`/`loop`/`loss`/`timeout`/`hold`/`thr`/`stream`), stream enable (`0x0017`), loop control |
> | Firmware → host | GET responses + `0x0019` status | Configuration readback + 100 Hz `DPLL_STATUS` telemetry stream |
>
> The firmware's ASCII debug console runs on a **separate hardware UART** (`DebugPort`), so the host must never write ASCII to `SerialUSB` — every command and every readback is a binary opcode packet.

---

## Quick start

```powershell
# 1. Edit serial.json to point at your device's COM ports (see below)
# 2. Run
dotnet run --launch-profile http
```

Open <http://localhost:5280> (HTTPS: <https://localhost:7264>).

The app **auto-connects** to the port listed in `serial.json` as soon as it starts — there are no Connect/Disconnect buttons. It enables the telemetry stream and reads the current firmware configuration automatically (via binary GET opcodes).

The connection badge shows `ONLINE · COM9` when the link is up. The port reconnects on its own with a 2 s retry while the app runs.

---

## Configuration (`serial.json`)

The serial port is configured in **`serial.json`** (in the project root), which is hot-reloaded: **edit and save the file while the app is running and it reconnects to the new port without a restart**.

```json
{
  "Serial": {
    "PortName": "COM9",          // Serial port — binary opcode stream (e.g. COM9)
    "BaudRate": 115200,          // 8N1
    "ReconnectDelayMs": 2000,    // auto-reconnect interval
    "StreamTimeoutMs": 1000      // stream freshness timeout
  }
}
```

Leave `PortName` empty (`""`) to disable the link. If the file is missing or has no `Serial` section, the app falls back to the defaults in `appsettings.json` (empty → no auto-connect, UI shows OFFLINE).

---

## Serial protocol (from firmware)

### Binary telemetry

Frame layout (little-endian):

| Byte | Field |
|---|---|
| 0 | `0xAA` start marker |
| 1–2 | opcode `uint16` |
| 3–4 | address `uint16` |
| 5–6 | length `uint16` = payload length + 1 (checksum) |
| 7 | `0xBB` end marker |
| 8.. | payload (≤ 512 bytes) |
| last | checksum — two's complement of payload bytes: `(byte)((sum ^ 0xFF) + 1)` |

Host→firmware: `0x0017 SET_ALLOW_SEND_STREAM` (payload `1` = start streaming, `0` = stop). All configuration is sent with SET opcodes; readback is requested with GET opcodes.

Firmware→host: `0x0019 STREAM_DPLL_STATUS` at the configured rate (`s_streamPeriodMs`, default 10 Hz). The 16-byte packed payload:

| Offset | Type | Field |
|---|---|---|
| 0 | `float` | `ReferenceFrequencyHz` |
| 4 | `float` | `PhaseError_ns` |
| 8 | `float` | `DACVoltage_V` |
| 12 | `uint8` | `LockStatus` — 0=NO_REF, 1=WAIT_ZCD, 2=TRACK, 3=LOCK |
| 13 | `uint8` | `PhaseStale` — 0=fresh, 1=ZCD absent (holding last valid value) |
| 14–15 | pad | — |

### Opcode map

All opcodes are `uint16_t` little-endian. Values must match the firmware `include/Opcode.h` enum:

| Opcode | Name | Payload (host→fw / fw→host) |
|---|---|---|
| `0x0001` | `GET_VERSION` | — / ASCII version string |
| `0x0002`/`0x0003` | `SET_KP` / `GET_KP` | float LE |
| `0x0004`/`0x0005` | `SET_KI` / `GET_KI` | float LE |
| `0x0006`/`0x0007` | `SET_KD` / `GET_KD` | float LE |
| `0x0008`/`0x0009` | `SET_CENTER_VOLTAGE` / `GET_CENTER_VOLTAGE` | float LE |
| `0x000A`/`0x000B` | `SET_TARGET_PHASE` / `GET_TARGET_PHASE` | float LE |
| `0x000C`/`0x000D` | `SET_OUTPUT_LIMITS` / `GET_OUTPUT_LIMITS` | 2× float LE |
| `0x000E`/`0x000F` | `SET_MAX_SLEW` / `GET_MAX_SLEW` | float LE |
| `0x0010`/`0x0011` | `SET_ENABLE_LOOP` / `GET_LOOP_ENABLE` | uint8 bool |
| `0x0012` | `RESET_LOOP` | — |
| `0x0013` | `SHUTDOWN_LOOP` | — |
| `0x0014`/`0x0015` | `SET_VOLTAGE` / `GET_VOLTAGE` | float LE |
| `0x0017`/`0x0018` | `SET_ALLOW_SEND_STREAM` / `GET_ALLOW_SEND_STREAM` | uint8 bool |
| `0x0019` | `STREAM_DPLL_STATUS` | 16-byte status struct |
| `0x001A`/`0x001B` | `SET_LOOP_PERIOD` / `GET_LOOP_PERIOD` | uint32 LE (1–1000 ms) |
| `0x001C`/`0x001D` | `SET_LOCK_THRESHOLD` / `GET_LOCK_THRESHOLD` | float LE (ns) |
| `0x001E`/`0x001F` | `SET_MANUAL_MODE` / `GET_MANUAL_MODE` | uint8 bool |
| `0x0020`/`0x0021` | `SET_LOCK_HOLD_CYCLES` / `GET_LOCK_HOLD_CYCLES` | uint32 LE |
| `0x0022`/`0x0023` | `SET_LOCK_MEMORY_TIMEOUT` / `GET_LOCK_MEMORY_TIMEOUT` | uint32 LE (ms, 0 = never) |
| `0x0024`/`0x0025` | `SET_SIGNAL_LOSS_BEHAVIOR` / `GET_SIGNAL_LOSS_BEHAVIOR` | uint8 (0/1/2) |
| `0x0026`/`0x0027` | `SET_STREAM_PERIOD` / `GET_STREAM_PERIOD` | uint32 LE (ms) |

Host→firmware SET payloads use little-endian primitives:
- `float` — 4 bytes LE (e.g. Kp, center voltage, lock threshold)
- `uint32` — 4 bytes LE (e.g. loop period, hold cycles, timeout, stream period)
- `uint8` — 1 byte (bool / signal-loss behavior)

GET responses (firmware→host) mirror the same payloads; the app folds them into the configuration snapshot after `RefreshConfiguration()`.

---

## Web UI

- **Live Status** — frequency (Hz), phase error (ns), DAC voltage (V), loop period, lock badge (NO REF / WAIT ZCD / TRACK / LOCK), stale-phase and manual-mode flags.
- **Trends** — three scrolling ECharts line charts (600 points each ≈ 6 s at 100 Hz, LTTB downsampled), live sample-rate chip, pause/clear, plus **Start logging / Stop logging** buttons that record raw telemetry to CSV.
- **Controller** — edit Kp/Ki/Kd/center/target/slew/loop/lock threshold/lock hold cycles/lock memory timeout/stream period/signal-loss behavior, **Apply configuration**, **Reset loop**, **Run loop**, **Shutdown (0 V)**, plus a manual DAC slider.
- **Event Log** — timestamped INFO/OK/WARN/ERROR/DATA entries (capped at 500 lines).

### CSV data logger

Clicking **Start logging** in the Trends panel creates `data/<yyyyMMdd_HHmmss>.csv` next to the executable (`bin/Debug/net10.0/data/` when running `dotnet run`, `App/data/` in the published folder) and appends **every** telemetry frame while active. The chip in the Trends header shows the live row count; **Stop logging** closes the file and shows its path in the event log.

CSV columns:

| Column | Meaning |
|---|---|
| `Timestamp_ms` | Host-clock Unix epoch milliseconds (UTC) when the sample arrived |
| `ReferenceFrequencyHz` | Reference frequency (Hz) |
| `PhaseErrorNs` | Phase error (ns) |
| `DACVoltage_V` | DAC output (V) |
| `LockStatus` | 0=NO_REF, 1=WAIT_ZCD, 2=TRACK, 3=LOCK |
| `LockState` | Human-readable lock state |
| `PhaseStale` | 1 when ZCD/REF absent (holding last value) |
| `IsLocked` | 1 when locked |

The file is written with `AutoFlush` — safe to copy/read while recording. Logging is a singleton server-side service (`CsvLoggerService`), so a session survives browser refreshes.

---

## Client assets (offline)

All browser dependencies are served from `wwwroot/` — no CDN, no Google Fonts:

| Asset | Location | Version |
|---|---|---|
| ECharts | `wwwroot/assets/libs/echart/echarts.min.js` | 6.0.0 |
| Inter font | `wwwroot/assets/fonts/inter2/inter2.css` (+ woff2) | 400–700 |
| SignalR client | `wwwroot/js/signalr.js` / `signalr.min.js` | `@microsoft/signalr` 8.0.7 |
| App code | `wwwroot/js/app.js`, `wwwroot/css/site.css` | — |

`Program.cs` also sets `Cache-Control: no-store` on every static response so a browser refresh always picks up the latest UI files.

---

## Project layout

```
DPLL_Ultrasonic_Freq_Tracking_DAQ.csproj
Program.cs                      # Hosting + SignalR + auto-connect on startup
serial.json                     # Serial port configuration (hot-reloaded)
appsettings.json                # Fallback defaults for the Serial section
Models/
  SerialOptions.cs              # Serial config binding
  DpllTelemetry.cs              # Decoded telemetry record
  DpllConfiguration.cs          # Firmware config snapshot (folded from GET responses)
  DpllConfigurationPatch.cs     # Partial-update DTO from the UI
Protocol/
  DpllProtocol.cs               # Packet build/parse, checksum, opcodes, status decode
Services/
  SerialDeviceService.cs        # Single-port manager (binary opcode traffic)
  CsvLoggerService.cs           # Background CSV telemetry logger (Start/Stop/Log)
Hubs/
  DpllHub.cs                    # SignalR hub: /hubs/dpll
wwwroot/
  index.html                    # Dashboard
  css/site.css
  js/app.js                     # SignalR client + ECharts + controls
  js/signalr.min.js             # Vendored @microsoft/signalr 8.0.7
  assets/                       # Offline client libs (ECharts, Inter font)
```

## SignalR hub (`/hubs/dpll`)

| Method (client→server) | Description |
|---|---|
| `SetStreamEnabled(bool)` | Enable/disable the telemetry stream (`0x0017`) |
| `ApplyConfiguration(patch)` | Send tuning parameters as binary SET opcodes |
| `RefreshConfiguration()` | Request a config snapshot via binary GET opcodes |
| `ResetLoop()` / `RunLoop()` / `ShutdownLoop()` | Binary `RESET_LOOP` / `SET_ENABLE_LOOP` / `SHUTDOWN_LOOP` |
| `SetManualVoltage(v)` | Binary `SET_VOLTAGE` (`0x0014`) |
| `StartLogging()` | Begin CSV recording; returns the created file path |
| `StopLogging()` | Stop CSV recording; returns the closed file path |
| `GetLoggingStatus()` | Current logging state (active, file, row count, started at) |

Events (server→client): `Telemetry` (100 Hz), `Configuration`, `ConnectionState(code, port)`. Connection is managed entirely server-side from `serial.json` — the UI never opens/closes ports.

## License

See [LICENSE](LICENSE).
