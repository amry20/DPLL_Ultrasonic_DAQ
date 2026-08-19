# DPLL Ultrasonic DAQ

Web-based data-acquisition and control UI for the **DPLL ultrasonic frequency-tracking** firmware running on an STM32F407 (see `D:\National Seismic Instrument\Release\Firmware\Source\DPLL_Ultrasonic_Frequency_Tracking`).

The application exposes a browser dashboard over **SignalR (WebSocket)** that shows live DPLL telemetry (reference frequency, phase error, DAC voltage, lock state), plots real-time trend charts, and lets you tune the digital PLL loop (Kp / Ki / Kd, center voltage, target phase, slew rate, loop period, signal-loss behavior) plus drive the DAC manually.

> **Architecture note — one serial interface.** The firmware exposes a single serial console (USB CDC `SerialUSB`) that carries **both** traffic types:
>
> | Traffic | Protocol | Use |
> |---|---|---|
> | **Binary telemetry** | Opcode packets | Incoming 100 Hz `DPLL_STATUS` stream + enable/disable stream (`0x0017`) |
> | **ASCII control** | Text command lines (`\r\n`) | All tuning commands (`kp`, `ki`, `center`, `dac`, …) and the `gain` report |
>
> The app manages **one serial port**. Incoming bytes are demultiplexed: printable ASCII bytes are accumulated into command lines, everything else (binary frame bytes — `0xAA` start marker, opcodes, float payloads, checksum) is fed to the packet parser. `\n` flushes pending packets and then processes the accumulated ASCII line.

---

## Quick start

```powershell
# 1. Edit serial.json to point at your device's COM ports (see below)
# 2. Run
dotnet run --launch-profile http
```

Open <http://localhost:5280> (HTTPS: <https://localhost:7264>).

The app **auto-connects** to the port listed in `serial.json` as soon as it starts — there are no Connect/Disconnect buttons. It enables the 100 Hz telemetry stream and reads the current firmware configuration automatically.

The connection badge shows `ONLINE · COM9` when the link is up. The port reconnects on its own with a 2 s retry while the app runs.

---

## Configuration (`serial.json`)

The serial port is configured in **`serial.json`** (in the project root), which is hot-reloaded: **edit and save the file while the app is running and it reconnects to the new port without a restart**.

```json
{
  "Serial": {
    "PortName": "COM9",          // Serial port — binary stream + ASCII commands (e.g. COM9)
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

Host→firmware: `0x0017 SET_ALLOW_SEND_STREAM` (payload `1` = start streaming, `0` = stop).

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

### ASCII control

Every line is terminated with `\r\n` and shares the same port as the binary stream. The app sends:

| Command | Argument | Description |
|---|---|---|
| `kp <v>` | float | Proportional gain, V/ns |
| `ki <v>` | float | Integral gain, V/ns/s |
| `kd <v>` | float | Derivative gain, V/ns/s |
| `center <v>` | float (0–3.3) | Center DAC voltage, V |
| `target <ns>` | float | Target phase error, ns |
| `slew <v/s>` | float | Max DAC slew rate, V/s |
| `loop <ms>` | uint 1–1000 | Control loop period, ms |
| `loss <n>` | 0 / 1 / 2 | Signal-loss behavior: 0=freeze, 1=center, 2=zero |
| `gain` | — | Request configuration report (the app parses this to populate the form) |
| `dac <v>` | float (0–3.3) | Manual DAC voltage — **disengages the loop** |
| `reset` | — | Clear integrator, restart from center voltage, re-enable loop |
| `run` | — | Re-enable automatic loop control |
| `timeout <ms>` | uint | Lock-memory timeout |
| `help` | — | List commands |

> Note: lock threshold (`thr`), lock hold cycles (`hold`) and stream period (`stream`) have **no ASCII command** — the app sends them as binary SET opcodes (`0x001C`, `0x0020`, `0x0026`).

`gain` response example:

```
Kp=0.000002 V/ns | Ki=0.000200 V/ns/s | Kd=0.000000 V/ns/s | center=1.65 V | target=0.0 ns | slew=30.0 V/s | manual=no | loop=20 ms | thr=500 ns | hold=10 | timeout=5000 ms | stream=100 ms | lockedV=1.650 V | loss=0
```

Default baseline: Kp=2e-6, Ki=2e-4, Kd=0, center=1.65 V, target=0 ns, slew=30 V/s, loop=20 ms, clamp 0–3.3 V, lock threshold 500 ns, lock hold cycles 10, lock-memory timeout 5000 ms, stream period 100 ms.

---

## Web UI

- **Live Status** — frequency (Hz), phase error (ns), DAC voltage (V), loop period, lock badge (NO REF / WAIT ZCD / TRACK / LOCK), stale-phase and manual-mode flags.
- **Trends** — three scrolling Chart.js line charts (300 points each ≈ 3 s at 100 Hz), live sample-rate chip, pause/clear.
- **Controller** — edit Kp/Ki/Kd/center/target/slew/loop/lock threshold/lock hold cycles/lock memory timeout/stream period/signal-loss behavior, **Apply configuration**, **Reset loop**, **Run loop**, **Shutdown (0 V)**, plus a manual DAC slider.
- **Event Log** — timestamped INFO/OK/WARN/ERROR/DATA entries (capped at 500 lines).

The SignalR client bundle is vendored locally at `wwwroot/js/signalr.min.js` (`@microsoft/signalr` 8.0.7), so the app needs no CDN except Chart.js.

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
  DpllConfiguration.cs          # Parsed "gain" report
  DpllConfigurationPatch.cs     # Partial-update DTO from the UI
Protocol/
  DpllProtocol.cs               # Packet build/parse, checksum, opcodes, status decode
Services/
  SerialDeviceService.cs        # Single-port manager (demuxed binary telemetry + ASCII control)
Hubs/
  DpllHub.cs                    # SignalR hub: /hubs/dpll
wwwroot/
  index.html                    # Dashboard
  css/site.css
  js/app.js                     # SignalR client + charts + controls
  js/signalr.min.js             # Vendored @microsoft/signalr 8.0.7
```

## SignalR hub (`/hubs/dpll`)

| Method (client→server) | Description |
|---|---|
| `SetStreamEnabled(bool)` | Enable/disable the 100 Hz stream |
| `ApplyConfiguration(patch)` | Send tuning parameters over ASCII |
| `RefreshConfiguration()` | Request `gain` report |
| `ResetLoop()` / `RunLoop()` / `ShutdownLoop()` | ASCII `reset` / `run` / `dac 0.0` |
| `SetManualVoltage(v)` | ASCII `dac <v>` |

Events (server→client): `Telemetry` (100 Hz), `Configuration`, `ConnectionState(code, port)`. Connection is managed entirely server-side from `serial.json` — the UI never opens/closes ports.

## License

See [LICENSE](LICENSE).
