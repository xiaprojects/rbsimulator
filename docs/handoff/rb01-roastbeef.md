# RB-01 (roastbeef): simulator hub, implementation instructions

**For:** whoever implements the simulator input in `roastbeef`, the maintainer or an agent
session working in that repo.

**From:** RB Simulator (`rbsimulator`), 2026-10-28.

**Plan tasks:** T3.1 → T3.2 → T3.3, in [../plan.md](../plan.md).

**Contract:** [../protocol.md](../protocol.md) §4.1, §4.2, §4.4 and §5. The requirement IDs
are `RB-SIM-*`, listed in protocol §8.

The file:line references below are from `xiaprojects/roastbeef` at **ecfbb20**
(2026-10-27). Re-check them before editing, because the working tree may be ahead.

---

## 1. Goal

A **simulator build** of RB-01 (`make stratuxrun-sim`, Go build tag `rbsim`) does five
things:

1. It accepts the simulator's state over HTTP: `POST /sim/state` at 10–20 Hz,
   `POST /sim/traffic` at 1 Hz, and `GET /sim/status`.
2. It feeds that state through RB-01's **real** paths: the GPS NMEA parser, the AHRS and
   baro fields of `mySituation`, the EMS store, and the traffic store. The HMI, autopilot,
   alerts, datalog, GDL90 and FLARM outputs then behave as they do in the aircraft.
3. It feeds the RB-02/04/05/07 instruments on its existing NMEA outputs. It adds
   `$RBATT` (20 Hz) and `$RBADC` (10 Hz) to the `$GPRMC`, `$PFLAA` and `$RBEMS` it already
   sends.
4. It returns RB-01's autopilot intents in the HTTP response. That part lands with M5;
   for now, leave the hook in place.
5. It never lets a stale value look valid, and it never falls back to the real sensors.

The flight build must not change: it has no `/sim/*` handlers (404), no new sentences, and
no new behaviour.

## 2. Hard rules (read before writing code)

From this repo's CLAUDE.md, the RB suite rules, and roastbeef's own AGENTS.md:

- **Compile-time gating only.** All simulator code goes in new files with
  `//go:build rbsim`. A runtime switch alone is never enough (RB-SIM-SAF-010).
- **Every touch to an existing `.go` file needs the maintainer's approval (STOP).** Parts
  of `main/` are certified core (`docs/certification/`). The design below needs exactly
  **one** such touch (§4.2), so propose it and wait.
- **Identification.** A sim build carries:
  - the version suffix `-SIM`;
  - a permanent **SIM** annunciation in the HMI;
  - its own OTA component name and RB-Cloud device IDs, so sim and aircraft packages and
    logs never mix.
- **`make check` stays green**, as do `gofmt` on the leaf packages, `go vet` on every
  package, and `scripts/trace.py --check`. **Don't** add `RB-SIM-*` IDs to `requirements/`.
  Reference them in test comments instead (`// RB-SIM-RB1-050`), so the traceability check
  is untouched.
- **Line endings:** Go is LF; `web/RB-01/**/*.js` is CRLF.
- **Never `git add -A`.** The tree carries the maintainer's WIP. Stage files by name, and
  commit only when asked.
- Use `GOWORK=off` to reproduce what CI sees.

## 3. What the code looks like today (evidence)

| Topic | Finding | Where |
|---|---|---|
| HTTP mux | `http.HandleFunc` on the **default mux**, served by `http.ListenAndServe(addr, nil)` | `main/managementinterface.go:2856`, `:3034` |
| → consequence | an rbsim file can register `/sim/state`, `/sim/traffic` and `/sim/status` in its own `init()`, without editing `managementinterface.go` | |
| Settings load | `readSettings()` runs in `main()`, **after** every `init()` | `main/gen_gdl90.go:1903` |
| Sensor start | `initI2CSensors()` then `go pollSensors()` and `go sensorAttitudeSender()`. `pollSensors` checks `globalSettings.BMP_Sensor_Enabled` / `IMU_Sensor_Enabled` on each poll before starting a sender | `main/gen_gdl90.go:~1979`; `main/sensors.go:100–140` |
| Pitot | `pollAirspeedSensor()` starts `airspeedSender()` when an MS4525DO answers (`MS4525DO_Enabled`) | `main/airspeed.go:55–66` |
| Setting names | `GPS_Enabled`, `BMP_Sensor_Enabled`, `IMU_Sensor_Enabled`, `MS4525DO_Enabled` | `main/gen_gdl90.go:1203–1206` |
| Config file | `configLocation = "/boot/firmware/stratux.conf"` (package variable) | `main/gen_gdl90.go:123` |
| GPS in | `processNMEALine(l string)` → `processNMEALineLow`, under `mySituation.muGPS` | `main/gps.go:1121` |
| Network GPS type | `handleNmeaInConnection` sets `globalStatus.GPS_detected_type = GPS_TYPE_NETWORK` | `main/network.go:274–283` |
| AHRS fields | `mySituation.muAttitude`, `AHRSLastAttitudeTime`, `AHRSStatus`, `AHRSGLoadMin/Max`; invalid is `ahrs.Invalid` (see `isAHRSInvalidValue`) | `main/gps.go:108–119`; `main/sensors.go:667–674`, `:1160–1177` |
| Baro fields | `mySituation.muBaro`, `BaroLastMeasurementTime`, `BaroSourceType`. Values `BARO_TYPE_NONE` 0, `BMP280` 1, `OGNTRACKER` 2, `NMEA` 3, `ADSBESTIMATE` 4 | `main/gps.go:45–49`, `:97–105` |
| Airspeed | `mySituation.IndicatedAirSpeed` in kt; **0 = no airspeed source**, and the HMI then shows ground speed | `main/gps.go:141`; `main/airspeed.go` |
| EMS in | `handleEMSSetRequest` updates `ems.emsData` under `ems.emsDataMutex`, logs with `logEMS`, and tracks min/max. The logic is inline in the handler, not a callable function | `main/managementinterface.go:1600–1640` |
| EMS out | `makeRBEMSString` / `makeRBCYLString`, sent every 200 ms / about 2 s through `sendNetFLARM` | `main/ems.go:82–130` |
| Traffic in | `parseFlarmPFLAA` builds a `TrafficInfo` (relative position, `Last_source = TRAFFIC_SOURCE_OGN`, `Alt, AltIsGNSS = relativeGpsAltToBaro(...)`) and hands it to `registerTrafficUpdate(ti)` | `main/flarm-nmea.go:600–700`; `main/traffic.go:545` |
| NMEA out | `sendNetFLARM(msg, maxAge, priority)` → `sendMsg(..., NETWORK_FLARM_NMEA, ...)`. The first 6 characters key the loop-back guard (`$RBATT` and `$RBADC` are distinct keys) | `main/network.go:624`; `:422–429` |
| FLARM / GPS out | `makeFlarmPFLAAString`, `makeFlarmPFLAUString`, `makeGPRMCString`, `makeGPGGAString`. Emit order per cycle: every `$PFLAA`, then one `$PFLAU` | `main/flarm-nmea.go:46–416`; `main/traffic.go:398–441` |
| Version | `stratuxVersion` (from the git tag) | `main/gen_gdl90.go:129`, `:1844` |
| Name clash to avoid | `test/display/rbserver.py` / `simulator.py` already have a `/sim/` control namespace (HMI test backend only, not the daemon) | `test/display/` |

The `stratuxrun-sim` / `check-sim` Makefile targets exist only in the maintainer's local
tree (2026-10-26); they are **not on GitHub yet**. Commit them with T3.2.

## 4. Design

### 4.1 New files

| File | Build constraint | Content |
|---|---|---|
| `main/siminput.go` | `//go:build rbsim` | handlers, validation, ownership, staleness, fan-out, `$RBATT`/`$RBADC` emitters |
| `main/siminput_test.go` | `//go:build rbsim` | tests (§6) |
| `main/siminput_hook.go` | `//go:build rbsim` | `func simInputAfterSettings()`: suppresses sensors, starts the sim goroutines |
| `main/siminput_stub.go` | `//go:build !rbsim` | `func simInputAfterSettings() {}` |

### 4.2 The one touch to an existing file (STOP for approval)

In `main()` (`main/gen_gdl90.go`), immediately after `readSettings()` (line 1903):

```go
	readSettings()
	simInputAfterSettings() // no-op in flight builds (siminput_stub.go)
```

**Why it can't be avoided:** the sensors must be suppressed **after** the settings are read
and **before** `initI2CSensors()` / `pollAirspeedSensor()` start. `init()` runs too early,
and a goroutine that polls for "settings loaded" would be a race. The flight build compiles
the empty stub, so its behaviour is unchanged. Present this line, with its stub, to the
maintainer.

### 4.3 `simInputAfterSettings()` (rbsim only)

1. In memory only, never saved, force `globalSettings.IMU_Sensor_Enabled`,
   `BMP_Sensor_Enabled` and `MS4525DO_Enabled` to `false` (RB-SIM-RB1-060). Hardware
   sensors then never write `mySituation`. **No fallback to the board IMU or baro**
   (RB-SIM-SAF-050): a bench unit sitting still would draw a convincing level attitude.
2. Decide what to do about `GPS_Enabled` and a physically attached serial GPS. It must not
   feed `processNMEALine` alongside the sim. Check whether `GPS_Enabled=false` still lets
   the rbsim file call `processNMEALine` (T3.1 question 3), and document the answer.
3. **Settings persistence:** a later `/setSettings` save would write the forced values.
   Proposal: the sim build sets `configLocation` to its own file
   (`/boot/firmware/stratux-sim.conf`) in `init()`, so a sim unit can never leave sensor
   settings in an aircraft config. This is the maintainer's decision.
4. Start the staleness watchdog (§4.6) and the `$RBATT`/`$RBADC` emitters (§4.7).

### 4.4 Endpoints (RB-SIM-IF-010…080)

Register them in `init()` of `siminput.go`, with `http.HandleFunc`.

- **`POST /sim/state`**: the body is protocol §4.1.
  - Decode with `DisallowUnknownFields` semantics.
  - Any missing required key, non-numeric value or out-of-range value → **400**
    `{"v":1,"error":"<group>.<field>: <reason>"}`, with **nothing applied** (IF-040).
  - `gps`, `ahrs`, `baro` and `airspeed` are required in every message. An object means
    valid and fresh; `null` means invalid (IF-030).
- **`POST /sim/traffic`**: the full target set, nearest first, at most 50 (§4.1).
- **`GET /sim/status`**: `{"v":1,"hub":"rb01","build":"<ver>-SIM","src":...,"peer":...,"rate_hz":{...},"fresh":{...},"paused":...,"last_error":...}`.
- **Single source** (IF-050): the first sender owns the hub until it has been silent for
  2 s. Another sender gets **409** `{"v":1,"error":"hub owned by <src>@<ip>"}`.
- **Response 200:** `{"v":1,"seq":<echo>}`. The `cmd` object (autopilot intents, §5) comes
  in M5. Leave a function stub returning `nil`.
- Keep handlers cheap: 20 Hz keep-alive POSTs must not block on locks that the sensor or
  traffic loops hold for long.

### 4.5 Fan-out (RB-SIM-RB1-080), protocol §4.2

| Group | Into | Notes |
|---|---|---|
| `gps` | synthesise `$GPRMC` and `$GPGGA` with `makeGPRMCString`'s widths, set `GPS_detected_type` as `handleNmeaInConnection` does, and call `processNMEALine` | RB-01's own GPS validity logic runs. `null` → send nothing, so the fix is lost through the normal path |
| `ahrs` | write `AHRSPitch`, `AHRSRoll`, `AHRSGyroHeading`, `AHRSMagHeading`, `AHRSTurnRate`, `AHRSSlipSkid` and `AHRSGLoad` under `muAttitude`; stamp `AHRSLastAttitudeTime`; update `AHRSGLoadMin/Max`; set the valid bits in `AHRSStatus` | `null`, or nothing for **500 ms**, → all fields `ahrs.Invalid`, as `sensors.go:667` does |
| `baro` | `BaroPressureAltitude` (ft) and `BaroVerticalSpeed` (ft/min) under `muBaro`; stamp `BaroLastMeasurementTime`; set `BaroSourceType` to a **new** `BARO_TYPE_SIM` constant, declared in the rbsim file (proposal: 5) | stale after **1 s** → invalid as for a lost sensor. Check every `BaroSourceType` comparison (`gen_gdl90.go:830`, `gps.go:1782`, `:1846`, `:1935`) treats SIM like BMP280 |
| `airspeed` | `IndicatedAirSpeed` (kt) | stale or `null` → **0** (the HMI's "no airspeed source") |
| `ems` | the same effect as `handleEMSSetRequest`: `emsData`, min/max, `logEMS`. Copy the loop into the rbsim file rather than editing the handler | stale after 3 s |
| traffic | build a `TrafficInfo` with absolute `Lat`/`Lng`/`Alt` and the `Icao_addr` the sender gives, and call `registerTrafficUpdate(ti)` | normal traffic ageing removes targets missing from later sets. The pseudo-address range for sim objects is decided in T3.1; the C# wrapper uses `0xF00000 \| hash` provisionally |

Values arrive in RB-01's own units and field names (protocol §4.1). The wrapper has
already converted them: ft, kt, ft/s for `GPSVerticalSpeed`, ft/min for
`BaroVerticalSpeed`, and degrees with pitch + nose up and roll + right wing down.

### 4.6 Staleness and pause

- Group timeouts: `ahrs` 0.5 s, `baro` and `airspeed` 1 s, `gps` 2 s, `ems` 3 s, and a
  traffic target 10 s (protocol §2.3).
- When `paused: true`, the sender keeps sending the last values with fresh send times. The
  hub keeps them valid ("frozen", RB-SIM-MOD-040) and `/sim/status` shows it.
- When the sender disappears, every group goes invalid within its timeout, and the
  instruments show INOP. Never resend the last value as if it were fresh.

### 4.7 Device output (RB-SIM-RB1-090), protocol §4.4

Emit these through `sendNetFLARM` (serial, USB, BLE and TCP :2000), next to the existing
`$GPRMC`, `$PFLAA` and `$RBEMS`:

```
$RBATT,<pitch>,<roll>,<hdg_true>,<turn>,<slip>,<g>,<status>*hh     20 Hz
$RBADC,<ias_kt>,<static_hPa>,<vs_fpm>,<oat_c>,<status>*hh          10 Hz
$RBATT,,,,,,,0*4D        invalid attitude
$RBADC,,,,,0*4A          invalid air data
```

- Decimals: pitch, roll, heading, turn and slip 1; g 2; IAS 1; static 1; VS 0; OAT 1.
  Every numeric field must fit an int16 once the decimal point is removed.
- **Static pressure is derived from `BaroPressureAltitude`** with the exact ISA relation.
  RB-02 computes altitude itself from that pressure and its own QNH knob.
- Use `appendNmeaChecksum`.
- Flight builds never emit these sentences.
- Reference output to compare against: the C# wrapper produces byte-identical sentences
  (`rbsim-msfs --source synthetic --serial file:ref.nmea=rb02`), and its unit tests carry
  the golden lines.

## 5. Open questions to answer in T3.1 (with file:line)

1. **`AHRSSlipSkid` and `AHRSTurnRate`:** sign and units as `sensors.go` produces them. The
   contract assumes deg/s + right and ball degrees + right.
2. **The `AHRSStatus` bits** a sim build should set, and where the HMI reads them.
3. **`GPS_Enabled` versus `processNMEALine`** (§4.3 point 2).
4. **Traffic:** which `Last_source` and `Addr_type` to use for sim targets; whether `Alt`
   is pressure or GNSS (`AltIsGNSS`); and the pseudo-address range.
5. **SIM annunciation:** whether it ships as a `web/RB-01/addons/` addon installed only by
   the sim package, or another way.
6. **Packaging:** the sim package's name, OTA component, and RB-Cloud device IDs.

Write the answers into `rbsimulator/docs/rb-knowledge-base.md`, and the design decision
into `rbsimulator/docs/decisions.md`.

## 6. Tests (in `main/siminput_test.go`, rbsim tag)

Write them requirements-style: one behaviour per test, each with a `// RB-SIM-…` comment.

- Golden contract messages: read `tests/fixtures/contract/` from an rbsimulator checkout
  named by an environment variable. Skip when the variable is absent, so both hubs are
  tested against the same files.
- Validation: missing key, non-number, out of range, unknown name → 400 naming the field;
  state unchanged.
- Ownership: a second sender gets 409, then wins after 2 s of silence.
- Staleness: `ahrs` invalid at 0.5 s, `baro`/`airspeed` at 1 s; IAS returns to 0; nothing
  stays "valid but old".
- Pause: values stay valid while `paused: true`.
- GPS: the synthesised RMC/GGA reach `mySituation` through `processNMEALine`; `gps: null`
  loses the fix.
- Device lines: `$RBATT`/`$RBADC` content, checksum and rate (±10 %), plus the invalid
  forms.
- Sensors: after `simInputAfterSettings()`, the IMU, BMP and pitot senders never start, even
  with a fake I2C device present (reuse the `inChild` pattern from `sensors_test.go`).
- **Flight build:** a test, or `make check` plus a symbol check, proving that the default
  `stratuxrun` has no `siminput` symbols and answers 404 on `/sim/state`.

## 7. Done when

- `GOWORK=off make check` is green, so the flight build is unaffected.
- `make check-sim` is green.
- `go tool nm stratuxrun | grep -i siminput` finds nothing.
- `make stratuxrun-sim` reports `<ver>-SIM`.
- T3.3 end-to-end, run in rbsimulator: a sender drives the real daemon. `GET /getSituation`
  follows it; `$RBATT`/`$RBADC` appear on TCP :2000 at their rates; groups go invalid when
  the sender stops; a plain `stratuxrun` answers 404.

## 8. Test senders available now

- **Golden messages:** post them with `curl -X POST --data @msg.json http://<rb01>/sim/state`.
- **The C# wrapper** (`rbsimulator/wrapper-cs`, Windows, MSFS or `--source synthetic`):
  `GET http://<pc>:8090/api/state` returns exactly the body to POST.
  - The wrapper's own `POST /sim/state` client (20 Hz, persistent connection, 500 ms
    timeout, latest-wins, stop on 404) is the next wrapper task, in rbsimulator.
  - Until it lands, a 10-line relay script (GET from the wrapper, POST to RB-01) is enough
    for the bench.

## 9. Cannot be verified off the bench

These are bench item L4 ([../plan.md](../plan.md), test-machine session):

- that sensors are suppressed on a real Pi with the AHRS board fitted;
- BLE throughput and latency for `$RBATT` at 20 Hz;
- the HMI's SIM annunciation on the real display.
