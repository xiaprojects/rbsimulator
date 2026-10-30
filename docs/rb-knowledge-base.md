# RB knowledge base for the simulator

Snapshot: **2026-10-26**. Line numbers are as read on that date. Re-check them before
editing, because both sibling repos carry uncommitted maintainer work. The wider RB suite
is described in `../rbavionicswalkthrough/docs/` (projects, integration map, Claude docs
index).

## Product line (what "RB-01 and RB-02" covers here)

| Product | What | Hardware | Code |
|---|---|---|---|
| RB-01 | PFD: synthetic vision, autopilot, ADS-B/FLARM traffic, EMS, radio, map | Raspberry Pi 5 + display | `../roastbeef` (+ `../goflying`) |
| RB-03 | RB-01 software, remote displays only | Pi Zero 2 W | `../roastbeef` |
| RB-02 | round attitude and altimeter "six-pack" | ESP32-S3 Waveshare 2.1"/2.8" | `../rb-esp` |
| RB-04 | EMS display (ESP32 side) | ESP32-S3 | `../rb-esp` (EMS-USB templates) |
| RB-05 | round traffic display | ESP32-S3 | `../rb-esp` (TRAFFIC / WIRED-USB) |
| RB-07 | RB-02 + RB-05 in one box | ESP32-S3 | `../rb-esp` (BT / USB) |
| RB-09 | remote display: an RB-02 driven by another device's attitude and air data over USB | ESP32-S3 | `../rb-esp` (`RB-09-*-USB-*` templates) |

## RB-01 — roastbeef (Go daemon `stratuxrun` + AngularJS HMI)

### Inputs the simulator reuses unchanged

| Asset | Where | Use here | Gotcha |
|---|---|---|---|
| NMEA in over TCP `:30011` | `main/network.go:258` `tcpNMEAInListener`, `handleNmeaInConnection` | inside the rbsim hub only: the model for feeding `gps` to `processNMEALine` with the network GPS type (D16) | sets `GPS_TYPE_NETWORK`; one client at a time is the sane assumption |
| NMEA line processor | `main/gps.go:1121` `processNMEALine` | parses our `$GPRMC`/`$GPGGA` | `$PGRMZ` baro is used **only** for serial and SoftRF types (`gps.go:1832`), so no baro over the network |
| FLARM input | `main/gps.go:1857` → `main/flarm-nmea.go:472` `parseFlarmNmeaMessage` | traffic as `$PFLAA`/`$PFLAU` | alarm-level handling to be checked (T3.1, item 4) |
| EMS store | `POST /setEMS`, `main/ems.go` | inside the rbsim hub: the code path for the contract's `ems` group (D16) | body is `map[string]float32`: one non-numeric value drops the whole POST |
| Bridge float | `main/managementinterface.go:1463` `handleBridgeFloatSetRequest` | later: `vor*` keys from the sim's NAV1; IAS now comes through the contract | `ias_mps` drives `IndicatedAirSpeed` only while no pitot is connected (`:1499`) |
| HTTP handler registration | `main/managementinterface.go:2982` `http.HandleFunc(...)` | suggests `DefaultServeMux`, so an rbsim file could register `/sim/*` in `init()` | confirm in T3.1 |
| Cursor-on-Target in, UDP `:8087` | `main/cot-in.go` | alternative traffic path, absolute lat/lon | not planned for v1 |
| UDP NMEA in (EasyVFR passthrough, radio `$PGRMC`) | `main/autopilotudp.go` | maybe for R2 radio sync | prefix whitelist |

### Flight logs on the aircraft (import for student mission review)

| Fact | Where | Consequence |
|---|---|---|
| Logging is off by default; the `ReplayLog` setting turns it on. The file is created once the GPS has a fix | roastbeef `test/display/README.md`; `main/datalog.go` | the school setup checklist must turn it on |
| Logs live in `ReplayLogPath`, default `/var/log` | `main/gen_gdl90.go:1448` | — |
| `GET /flightlogs` lists `[{Name, Size, ModTime, Path}]` with `Path` under `/logs/` | `main/managementinterface.go` (around :1129, handler `handleFlightLogsGet`) | the import listing |
| `GET /logs/<x>.sqlite` is served from `ReplayLogPath`; anything else under `/logs/` comes from `/var/log` | `main/managementinterface.go:2622` `viewLogs`, `:2628` | `<x>.sqlite-wal` is reachable only with the default path (Q11) |
| A missing file under `/logs/` answers **HTTP 200** with the text "Failed to open …" | `viewLogs` | validate downloads by magic bytes: `SQLite format 3\0`; WAL `377f0682`/`377f0683` (checked 2026-10-26) |
| Logs are WAL-mode SQLite | `main/datalog.go:481` | a flight ended by a power cut (the master switch) keeps its rows in `-wal`. The roastbeef memory shows a 4 KB main file with 2 MB `-wal` |
| `flightlog.py` opens logs with `mode=ro&immutable=1`, which **ignores the WAL** | `test/display/flightlog.py:192` | tested 2026-10-26: a power-cut copy reads "no such table". Consolidate a local copy with `wal_checkpoint(TRUNCATE)` first. This affects roastbeef's own display tooling too: a power-cut log copied with its `-wal` replays as empty there |
| RB-Cloud lists flights and exports CSV per table and KML, but has **no whole-database download** | `../rb-cloud-php/docs/API.md` | cloud import needs a new route (Q12) |

### Outputs the simulator consumes

| Asset | Where | Use here |
|---|---|---|
| FLARM/NMEA out TCP `:2000` (fixed port, client dials in) | `main/network.go`, `main/flarm-nmea.go` | reverse channel: `$GPAPB`, `$GPRMB`, `$GPXTE`, `$PGRMH` while the autopilot is active |
| Nav sentence spec and golden vectors | `docs/integration/nmea-navigation-output.md`, `pgrmh-format.md`, `main/autopilot_test.go` | the reverse-channel parser's tests |
| `$RBEMS` / `$RBCYL` generator | `main/ems.go:82` / `:98` | the field order our RB-04 sink must match |
| ForeFlight-sim output (`XGPS`/`XATT`/`XTRAFFIC`, UDP `:49002`) | `main/xplane.go` | **output only.** The name "xplane" is misleading: it is the format X-Plane *emits*. Oracle for the T5 adapter |
| GDL90 UDP `:4000`, BLE, serial | `main/gen_gdl90.go`, `main/network.go` | free in Level D: an EFB tablet works with the sim |

### Situation model (the wire vocabulary of RB-SIM-IF/1 and of hub-lite's sockets)

`SituationData` in `main/gps.go` (struct around lines 66–147). Units that matter:

| Field | Unit |
|---|---|
| `GPSLatitude`, `GPSLongitude` | deg |
| `GPSAltitudeMSL`, `GPSHeightAboveEllipsoid` | ft |
| `GPSGeoidSep` | ft |
| `GPSVerticalSpeed` | **ft/s** |
| `GPSGroundSpeed` | kt |
| `GPSTrueCourse` | deg |
| `BaroPressureAltitude` | ft |
| `BaroVerticalSpeed` | **ft/min** (5 s EWMA, `sensors.go:274`) |
| `BaroTemperature` | °C |
| `AHRSPitch`, `AHRSRoll`, `AHRSGyroHeading`, `AHRSMagHeading` | deg (`AHRSMagHeading` magnetic since 2026-10-25) |
| `AHRSSlipSkid`, `AHRSTurnRate` | sign and unit to verify (T3.1) |
| `AHRSGLoad`, `AHRSGLoadMin`, `AHRSGLoadMax` | g |
| `AHRSStatus` | bits, to verify |
| `IndicatedAirSpeed` | **kt**; 0 means no airspeed source, and the HMI then shows ground speed |
| `PitotPressure` | Pa |

Attitude engine: `main/sensors.go:370` `sensorAttitudeSender`, a 20 Hz loop that runs
while `IMU_Sensor_Enabled && IMUConnected`. `newAHRS` at `:790` returns goflying Simple
(Kalman is commented out pending flight test). Baro: `tempAndPressureSender` at `:200`.
Pitot: `main/airspeed.go`.

### HMI (`web/RB-01/`)

- AngularJS 1.x, static, no build step. Plates in `plates/*.html` with controllers in
  `plates/js/*.js`.
- Each service in `services/*.js` owns one WebSocket and re-dispatches DOM events:

  | Socket | Carries |
  |---|---|
  | `/situation` | GPS fix, AHRS attitude, baro |
  | `/traffic` | fused targets |
  | `/radar` | radar/traffic display data |
  | `/alerts` | alerts, with audio |
  | `/autopilot` | autopilot status and modes |
  | `/ems` | engine telemetry |
  | `/status` | system health |
  | `/keypad` | hardware knob and keypad input |
  | `/radioStatus` | radio device status |
  | `/bridge/float/ws` | addon and probe numeric bridge |

- The HMI rate-limits by wall clock: `/situation` 100 ms, `/ems` 300 ms, `/status` 1 s.
- Addons: `GET /addons` lists JS+HTML under `web/RB-01/addons/` and `main.js` injects them.
  This is the vehicle for the SIM banner.
- **The JS is CRLF.** Python text-mode edits flip line endings; use binary mode.

### Test and simulation tooling to reuse (`test/display/`)

| File | Reuse |
|---|---|
| `rbserver.py` `FakeStratux` | hub-lite's display backend: serves `web/`, fakes REST and every service socket; `push(path, frame)`; `/sim/situation` merges `SituationData` fields |
| `simulator.py` | reference CLI: `--idle`, a replay by log name, `--device URL` to proxy a live RB-01 with writes blocked |
| `flightlog.py` | T1 replay source: reads datalog SQLite as socket frames; `list`, `info`, `extract`, `path`, `expect` |
| `plates.py`, `cdp.py` | drive Chromium headless for HMI tests and screenshots; stdlib only |
| `capture.py`, `fixtures/rb01-device.json` | **real aircraft snapshot. Never copy it here.** Use its structure to build the neutral fixture |
| `test/integration/replay.py` | the pattern for building and running `stratuxrun` non-root with a scratch HOME on a free port |

### Rules from roastbeef that bind us

- `make check` is the CI gate (gofmt on leaf packages, vet, test, provenance, the
  requirements trace). Keep it green. Use `GOWORK=off` for CI's view, because a local
  `go.work` points at `../goflying`.
- The certified-core scope and the requirements standard are in `docs/certification/`. Its
  IDs (`RB-SYS-*`, `RB-HLR-*`, `RB-ICD-*`) belong to roastbeef; ours are `RB-SIM-*` and
  live here.
- Network: the RB-01 is an access point at `192.168.10.1`. HTTP listens on `:80` as root
  and on `:8000` (or `-port`) as a non-root user.
- The live aircraft is reachable over HTTP only (SSH is refused). `capture.py` is GET-only
  by design.

## RB-02 family — rb-esp (ESP-IDF 5.5, LVGL 8.4, C)

### Inputs the simulator reuses unchanged

| Asset | Where | Use here | Gotcha |
|---|---|---|---|
| USB console line reader | `main/RB/RB_Usb.c:51` `usb_console_input_thread` | every USB sentence | 256-byte buffer including NUL; printable 0x20–0x7E only; dispatches each line to the FLARM, NMEA and EMS parsers by compile flag |
| GPS NMEA | `main/RB/RB02.c:1115` `NMEA_ParseBuffer` | `$GPRMC`/`$GPGGA` (or `GN`) | needs `RB_ENABLE_USB_NMEA`; hand-rolled scanners; the hemisphere trick relies on a non-digit path (see rb-esp CLAUDE.md) |
| FLARM traffic | `main/RB/RB05_FlarmParser.c` | `$PFLAU`/`$PFLAA` | needs `RB_ENABLE_USB_FLARM`; store holds **10** targets; `FlarmObstacle` fields are `int16`; `z` is metres |
| EMS | `main/RB/RB04_NMEA_Parser.c` | `$RBEMS`/`$RBCYL` | `parse_int_token` returns **`int16`**; integers only |
| RB-09 remote display | `main/RB/RB09_Slave.c` `RB09_Slave_ParseData` | `$RBATT`/`$RBAIR`, profile `rb09` (D24) | needs `RB_ENABLE_SLAVE_DEVICE`; its `$RBATT` is the 17-field one an RB-02 prints with NMEA output on, **not** RB-SIM-ESP-020's; checksum mandatory; float fields; INOP after 1 s (attitude) and 3 s (air) |

**USB-Serial-JTAG control lines** (checked on an RB-09, D24): the device receives with DTR off
or on. On Linux, opening the port with both lines off resets the chip once. Linux raises
both lines on open, and pyserial and .NET then drop DTR before RTS, so RTS is briefly high
with DTR low, which is the chip-reset state. Open it with DTR high and RTS low (wrapper:
`"dtr": true`). Not checked on Windows.

### Where sim data must enter (the new `RB_ENABLE_SIM` work)

| Asset | Where | Note |
|---|---|---|
| Driver loops | `main/RB/RB02_DriverFactory.c:202` `Driver_Attitude_Loop` (QMI8658 / BMI270), `:219` `Driver_Barometer_Loop` (BMP280 / BME680) | natural seam: in SIM builds, write sim values instead of reading chips |
| Attitude globals | `main/RB/RB02_DriverFactory.h`: `AttitudePitch`, `AttitudeRoll`, `AttitudeYaw`, `GFactor`, `GFactorMax/Min`, `AttitudeYawDegreePerSecond`, filtered accel/gyro | find the exact consumers per screen in T4.2 |
| Attitude filter | `main/RB/madgwick.c` | bypassed in SIM builds (state-level injection) |
| QNH | `RB02.c:313` `float QNH` | stays the pilot's setting on the device |
| IAS | `main/MS4525DO/ms4525do_rb.h` (km/h internally) | IAS on `RB_ENABLE_IAS` builds |
| INOP display | `main/RB/InopWarning.c` | used for stale sim data |
| USB templates | `BuildMachine-Template-RB-02-TOUCH-30hz-USB-2.{1,8}.h` (`RB_ENABLE_USB_NMEA`); `RB-04-*-EMS-USB-*`; `RB-05-*-TRAFFIC-WIRED-USB-*`; `RB-07-*-USB-*` | base for the `*-SIM-*` templates |

### Rules from rb-esp that bind us

- **Everything under `main/` is CRLF.** Edit in binary mode and check with `file`.
- Never `git add -A`. The tree carries maintainer WIP and untracked directories.
- `main/RB/RB02.h` ends with a deliberate `#error` under `#ifndef RB_HOST_TEST`. Leave it.
- Back up `main/RB/BuildMachine.h` before any template build, and restore it from the
  backup, never via git.
- **No firmware build here** (no ESP-IDF). Host tests run:
  `cmake -S test/host -B build/host-tests && cmake --build build/host-tests && ctest --test-dir build/host-tests`.
  Pin unfixed defects as `test_known_issue_*`.
- No `strtol`, `atoi`, `atof` or `sscanf` in `main/`; integer scanners only.
- Feature flags are the architecture, and the `RB02_TAB_*` enum values shift between
  builds, so never persist tab indexes.
- On most builds the QMI8658 is 6-axis, so there is no absolute heading: yaw is anchored to
  GPS track only with `RB_ENABLE_TRK`.
- Commit style: `<Description> #<issue>`.

## Free extras once RB-01 is fed (Level D)

| Component | What works without extra code | Care |
|---|---|---|
| rbvoice (RB-08) | voice read-outs and commands against the RB-01 API | — |
| rb-cloud-upload + RB-Cloud | flight logs uploaded and reviewable | **use SIM device IDs** or sim flights pollute the aircraft logbook and engine analysis |
| GDL90 / FLARM outputs | ForeFlight, SkyDemon or an EFIS on the sim network | — |
| RB-04 / RB-05 / RB-07 via RB-01 | as in the aircraft (RB-01 re-emits `$RBEMS`, `$PFLAA`) | — |
| Hobbs meter | counts sim minutes above 50 km/h | sim image only; never on an aircraft unit |

## Aircraft data facts relevant to replay (I-RVDX test aircraft)

From the roastbeef Claude memories, 2026-10:

- **Flight logs:** in `~/Desktop/win10/`, `~/Desktop/<YYYYMMDD>/` and
  `../roastbeef/test-data/`. `test-data/flightlog-sample.sqlite` is 3 minutes: take-off,
  climb, a 250° turn, ~20 targets and EMS.
- **The aircraft flies aerobatics.** Extreme attitudes in its logs are real, not faults.
- **`bridgefloat` holds an external air-data / AHRS unit** (`pitch_deg`, `ias_mps`,
  `oat_c`…). The RB internal AHRS reads about 4–5° nose-low against it in cruise. A replay
  shows what RB displayed, not the truth.
- **Datalogs are 1 Hz.** Each row is one instantaneous sample per second, not an average.
- **The Pi clock is unreliable without a GPS fix.** Never trust file names or log times
  for ordering without the GPS time.
