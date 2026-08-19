# DPLL Ultrasonic DAQ

Web-based data-acquisition and control UI for the **DPLL ultrasonic frequency-tracking** firmware running on an STM32F407 (see `D:\National Seismic Instrument\Release\Firmware\Source\DPLL_Ultrasonic_Frequency_Tracking`).

The application exposes a browser dashboard over **SignalR (WebSocket)** that shows live DPLL telemetry (reference frequency, phase error, DAC voltage, lock state), plots real-time trend charts, and lets you tune the digital PLL loop (Kp / Ki / Kd, center voltage, target phase, slew rate, loop period, signal-loss behavior) plus drive the DAC manually.

> **Architecture note — two serial interfaces.** The firmware deliberately splits its I/O across two physical UARTs:
>
> | Interface | Firmware peripheral | Protocol | Use |
> |---|---|---|---|
> | **Telemetry port** | `SerialUSB` (USB CDC) | Binary opcode packets | Incoming stream (100 Hz `DPLL_STATUS`) + enable/disable stream |
> | **Control port** | `DebugPort` (PA10=RX, PA9=TX) | ASCII text commands | All tuning commands (`kp`, `ki`, `center`, `dac`, …) and the `gain` report |
>
> The binary USB interface **cannot** receive tuning commands — every parameter write goes over the ASCII control port. This app therefore manages **two independent serial ports** and reconnects each one independently.

---

## Quick start

```powershell
# 1. Edit serial.json to point at your device's COM ports (see below)
# 2. Run
dotnet run --launch-profile http
```

Open <http://localhost:5280> (HTTPS: <https://localhost:7264>).

The app **auto-connects** to the ports listed in `serial.json` as soon as it starts — there are no Connect/Disconnect buttons. It enables the 100 Hz telemetry stream and reads the current firmware configuration automatically.

The connection badge shows `ONLINE · T:COM9 C:COM10` when both links are up. Each port reconnects on its own with a 2 s retry while the app runs.

---

## Configuration (`serial.json`)

The serial ports are configured in **`serial.json`** (in the project root), which is hot-reloaded: **edit and save the file while the app is running and it reconnects to the new ports without a restart**.

```json
{
  "Serial": {
    "PortName": "COM9",          // Telemetry port — USB CDC, binary stream (e.g. COM9)
    "ControlPortName": "COM10",  // Control port — DebugPort UART, ASCII commands (e.g. COM10)
    "BaudRate": 115200,          // 8N1 on both ports
    "ReconnectDelayMs": 2000,    // auto-reconnect interval
    "StreamTimeoutMs": 1000      // stream freshness timeout
  }
}
```

Leave a `PortName` empty (`""`) to disable that link. If the file is missing or has no `Serial` section, the app falls back to the defaults in `appsettings.json` (both empty → no auto-connect, UI shows OFFLINE).

---

## Serial protocol (from firmware)

### Binary telemetry — USB CDC (`SerialUSB`)

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

Firmware→host: `0x0019 STREAM_DPLL_STATUS` at **100 Hz** (`s_streamPeriodMs = 10`). The 16-byte packed payload:

| Offset | Type | Field |
|---|---|---|
| 0 | `float` | `ReferenceFrequencyHz` |
| 4 | `float` | `PhaseError_ns` |
| 8 | `float` | `DACVoltage_V` |
| 12 | `uint8` | `LockStatus` — 0=NO_REF, 1=WAIT_ZCD, 2=TRACK, 3=LOCK |
| 13 | `uint8` | `PhaseStale` — 0=fresh, 1=ZCD absent (holding last valid value) |
| 14–15 | pad | — |

### ASCII control — DebugPort UART

Every line is terminated with `\r\n`. The app sends:

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

`gain` response example:

```
Kp=0.000002 V/ns | Ki=0.000200 V/ns/s | Kd=0.000000 V/ns/s | center=1.65 V | target=0.0 ns | slew=30.0 V/s | manual=no | loop=20 ms | thr=500 ns | lockedV=1.650 V | loss=0
```

Default baseline: Kp=2e-6, Ki=2e-4, Kd=0, center=1.65 V, target=0 ns, slew=30 V/s, loop=20 ms, clamp 0–3.3 V, lock threshold 500 ns, lock-memory timeout 5000 ms.

---

## Web UI

- **Live Status** — frequency (Hz), phase error (ns), DAC voltage (V), loop period, lock badge (NO REF / WAIT ZCD / TRACK / LOCK), stale-phase and manual-mode flags.
- **Trends** — three scrolling Chart.js line charts (300 points each ≈ 3 s at 100 Hz), live sample-rate chip, pause/clear.
- **Controller** — edit Kp/Ki/Kd/center/target/slew/loop/loss, **Apply configuration**, **Reset loop**, **Run loop**, **Shutdown (0 V)**, plus a manual DAC slider.
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
  SerialDeviceService.cs        # Dual-port manager (binary telemetry + ASCII control)
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

Events (server→client): `Telemetry` (100 Hz), `Configuration`, `ConnectionState(code, telemetryPort, controlPort)`. Connection is managed entirely server-side from `serial.json` — the UI never opens/closes ports.

## License

See [LICENSE](LICENSE).
