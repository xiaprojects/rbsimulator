# Step 2 — Minimal features

Snapshot: **2026-10-26**. Priorities:

- **MVP**: Release 1. Without it the product does not make sense.
- **R2**: the next release.
- **Later**: wanted, but not planned yet.

Requirement IDs (`RB-SIM-…`) point to [protocol.md](protocol.md) §8.

## Principle

The MVP is **what the pilot sees on RB-01 and RB-02 in the real aircraft, driven by the
simulator, with the same behaviour**. That includes validity flags, INOP states, units and
the pilot's own knob settings such as QNH. Sim-specific features (instructor station,
failures) build on top of it and never change the instruments' own logic.

## 1. Data the bridge must carry (MVP)

| Domain | Fields (canonical names in protocol §2) | Needed by |
|---|---|---|
| Time | sim UTC date/time, sim paused flag | RB-01 clock/timers, RB-02 clock, `$GPRMC` |
| Position / nav | lat, lon, GPS altitude MSL, ground track (true), ground speed, GPS vertical speed, on-ground flag, magnetic variation | RB-01 map, HSI, synthview, autopilot; RB-02 GPS speed, map, track |
| Attitude | pitch, roll, true heading, magnetic heading, turn rate, slip (ball), normal G | RB-01 attitude/six-pack/synthview; RB-02 AAT, gyro, turn/slip, G-meter |
| Air data | IAS, TAS, pressure altitude (1013.25 hPa reference), static pressure, baro vertical speed, OAT | RB-01 speed/altimeter/vario/wind; RB-02 speed/altimeter/vario |
| Wind | sim wind direction and speed (truth, for checks only) | test oracle for RB-01's own wind solution, never displayed directly |
| Engine (engine 1) | RPM, manifold pressure, oil temperature, oil pressure, CHT 1–4, EGT 1–4, fuel flow, fuel left/right, bus volts, coolant temperature (if any) | RB-01 EMS/EGT plates; RB-04 |
| Traffic | AI and multiplayer aircraft within 20 NM: id, callsign, lat, lon, altitude, track, ground speed, vertical speed, on-ground | RB-01 radar/map/synthview, GDL90/FLARM out; RB-05/RB-07 |
| Sim state | connected, paused, slew/teleport event, aircraft title | validity handling, IOS |

Sims usually model a single CHT and EGT. CHT and EGT 1–4 are therefore **synthesised**
from one value with a configurable per-cylinder spread from the aircraft profile. They are
labelled synthetic in the profile, and the IOS can override a cylinder (RB-SIM-MOD-060).

## 2. RB-01 (Pi PFD; roastbeef)

| Feature (HMI plate or daemon function) | Data | Path | Priority |
|---|---|---|---|
| Attitude, six-pack, speed, altimeter, vario, turn-slip, heading, G-meter | attitude, air data | RB-SIM-IF `ahrs`, `baro`, `airspeed` | **MVP** |
| Synthetic vision 3D | position, attitude | as above | **MVP** |
| Map (OpenLayers), HSI | position, track | RB-SIM-IF `gps` (through RB-01's real NMEA parser) | **MVP** |
| Radar / traffic | traffic | RB-SIM-IF `POST /sim/traffic` | **MVP** |
| EMS and EGT plates | engine | `POST /setEMS` (stock) | **MVP** |
| Alerts (with audio), timers, checklist | daemon-internal | free once the daemon is fed | **MVP** |
| Autopilot direct-to / flight plan, guidance tunnel | position | free in the daemon | **MVP** (display) |
| RB-01 autopilot **flying the sim** | `$GPAPB`/`$GPRMB`/`$PGRMH` from TCP `:2000` → sim AP heading bug, altitude preselect | reverse channel | **MVP** for A; B as option |
| SIM annunciation on every plate | `/sim/status` | sim addon (rbsim build only) | **MVP** (safety) |
| GDL90 / FLARM out to an EFB tablet (ForeFlight, SkyDemon) | — | free: RB-01 outputs are unchanged | **MVP** (free) |
| Radio remote plate ↔ sim COM1 active/standby | COM frequencies | reverse channel plus radio emulation (investigate) | R2 |
| QNH sync, RB-01 ↔ sim Kohlsman | baro setting | optional, off by default (each instrument has its own knob, as in the aircraft) | R2 |
| VOR addon fed from sim NAV1 radial | NAV1 freq, radial, signal | `/bridge/float` keys `vorfreq`, `vorradial`, `vorstatus`… (rb-vor contract) | Later |
| RB-08 voice (rbvoice) | — | free: it talks to the RB-01 API | Later (free, test only) |
| Flight log → RB-Cloud debrief | — | free, under **SIM device IDs** | R2 |
| Sensor-level AHRS (raw IMU through goflying) for AHRS-failure training | IMU accel/gyro/mag from sim kinematics | rbsim `imu` mode | Later |

The autopilot-flying-the-sim row and the reverse channel apply to interactive simulators
(A, C) only. A replay cannot be steered.

**Two hubs, one contract (decision D16).** Every source (replay, MSFS, X-Plane…) sends
RB-SIM-IF/1 over HTTP to a hub:

- **hub-lite** (any PC, no Pi): the Python bridge serves the RB-01 HMI through roastbeef's
  FakeStratux and drives RB-02/04/05 over USB. It has **no** daemon logic: no autopilot,
  alerts or GDL90 out. This is the first standalone replay deliverable, and the MSFS user
  without RB hardware.
- **RB-01 SIM build** (`stratuxrun-sim` on a real RB-01, or on Linux/WSL2): every feature
  above, and it feeds RB-02/04/05/07 on its own outputs, as in the aircraft. A flight
  build has no `/sim/*` endpoints and is never fed.

## 3. RB-02 family (ESP32-S3; rb-esp)

| Device | Screens / features | Data over USB | Existing parser? | Priority |
|---|---|---|---|---|
| **RB-02** | attitude (AAT), altimeter with QNH, digital altimeter vs GPS, speed (kt/km/h arcs), vario, turn & slip, gyro (DG), G-meter with min/max, GPS map, clock and timers, checklist | `$RBATT`, `$RBADC` (**new**); `$GPRMC`/`$GPGGA` (existing) | GPS yes (`RB_ENABLE_USB_NMEA`); attitude and air data **no** | **MVP** |
| **RB-04** | EMS display | `$RBEMS` 5 Hz, `$RBCYL` every 2 s | yes (`RB_ENABLE_EMS`, USB templates) | **MVP** (direct from the bridge, or via RB-01 as in the aircraft) |
| **RB-05** | traffic radar and list | `$PFLAU`/`$PFLAA` + `$GPRMC` | yes (`RB_ENABLE_USB_FLARM`, WIRED-USB templates) | **MVP** |
| **RB-07** | RB-02 + RB-05 | union of the above | partly | R2 |
| all | **SIM** annunciation, "SIMULATOR – NOT FOR FLIGHT" splash, INOP when sim data is stale (> 500 ms attitude, > 1 s air data) | — | new under `RB_ENABLE_SIM` | **MVP** (safety) |

RB-02 in the aircraft is standalone, with its own GPS, baro and IMU. In the simulator it
gets everything over USB from the bridge. The pilot's QNH setting and G-meter min/max stay
on the device, as in the aircraft.

## 4. Scenario-specific features

### A — MSFS home cockpit

| Feature | Priority |
|---|---|
| `rbsim-wrapper-sdk.exe` for MSFS 2020 and 2024 through the official SimConnect. It finds `SimConnect.dll` from the user's SDK install, auto-connects, and reconnects after a sim restart, aircraft change or flight load | **MVP** |
| Every §3.1 variable at 20–30 Hz; explicit units; signs proven on captures | **MVP** |
| AI traffic within a configurable radius (default 20 NM) to RB-01 radar/map and RB-05; multiplayer traffic best-effort | **MVP** |
| Pause mirrored as freeze (instruments hold), including the Esc pause, Active Pause and photo mode, which send no events (inferred from sim time) | **MVP** |
| MSFS aircraft profile: tank and bus names, ball scale, cylinder spread, EMS units; warnings for a non-piston or unmapped aircraft | **MVP** |
| Auto-detect RB ESP32 devices on USB (Espressif USB-Serial-JTAG `303A:1001`, verify) and assign a profile | **MVP** |
| RB-01 as hub-lite (browser window or second monitor) or a real RB-01 SIM build on the LAN | **MVP** |
| RB-01 autopilot couples to the MSFS AP: heading bug, then engage by the pilot (`bug`); optional `full`. Explicit-state events only, verified by read-back; a clear status when the aircraft ignores standard events | **MVP** |
| Single config file plus a status page: MSFS version, DLL, rates, traffic, pause source, last SimConnect exception, profile warnings | **MVP** |
| Remote mode: bridge on a second Windows PC (`SimConnect.xml`/`.cfg`) | **MVP** |
| QNH sync (`KOHLSMAN_SET`), COM standby sync, altitude preselect from RB-01 VNAV | R2 |
| Standalone builds with no SDK install: `rbsim-wrapper.exe` on Windows, and the Linux builds including the RB-01 Pi, through the pure SimConnect protocol. Synthetic and other simulators from day one; MSFS once Q16 is answered and the differential test passes | **MVP** (builds), MSFS after Q16 |
| Signed executables and an installer (without `SimConnect.dll` unless Q14 allows) | R2 |

### B — FBO / flight school: student mission review (decisions D11, D13)

No third-party simulator is involved. The school uses the RB-01 in its own training
aircraft to record each flight. After a training mission, the instructor imports that
flight and reviews it with the student on the RB instruments. The RB-01 HMI can run on a
PC, and the school can add RB-02/04/05 or a spare RB-01 on the desk. The student sees
exactly what the panel showed, at every moment of their own flight. The flight path is the
recorded one; nobody flies the replay.

**Import and missions**

| Feature | Priority |
|---|---|
| Import from the aircraft over its Wi-Fi. List with `GET /flightlogs` and download `/logs/<name>.sqlite` **plus `-wal`**. Consolidate locally (power-cut logs keep their data in the WAL) and leave out the log still being written. The settings capture comes with it, GET-only | **MVP** |
| Import from a file or USB stick (the same consolidation) | **MVP** |
| Mission record, stored locally: student, instructor, exercise, date (from the log's GPS time, not the file name), aircraft, notes, bookmarks. Never uploaded by the simulator and never in the repo; the instructor can delete it | **MVP** |
| School setup checklist: `ReplayLog` on, `ReplayLogPath` default, the aircraft's Wi-Fi reachable from the debrief PC | **MVP** |
| Import from RB-Cloud for schools already uploading (needs a new download route, Q12) | R2 |

**Review on the instruments**

| Feature | Priority |
|---|---|
| Replay through either hub: hub-lite on the PC (RB-01 HMI, RB-02/04/05 on USB) or a real RB-01 SIM build, which feeds the devices itself | **MVP** |
| Player: play, pause (instruments hold as valid, MOD-040), seek to any second with the full state rebuilt (situation, EMS and bridge maps, live traffic), speed 0.25×–20×, loop, step | **MVP** |
| Checkpoints derived from the flight: take-off, climb, top, steepest turn, final, landed (roastbeef `flightlog.checkpoints()`) | **MVP** |
| **Event list** against the aircraft's own limits, each event a seek target:<br>• EMS values in yellow or red arcs (from the captured `ems.json`)<br>• speed arcs<br>• bank and G beyond the configured limits<br>• traffic alarms<br>• GPS loss and AHRS invalid periods | **MVP** |
| Track overview (plan view, altitude profile) with the replay cursor; click to seek | **MVP** |
| Instructor bookmarks with notes, saved in the mission and listed with the checkpoints and events | **MVP** |
| Instructor page bringing these together, with connected devices, health and configuration ID | **MVP** |
| Instructor mirror of the RB-01 HMI (a web page: open it on a second screen) | **MVP** (free) |
| Statement on the instructor page: "debrief and avionics familiarisation; not a flight training device" | **MVP** |
| Smooth attitude: display-only interpolation of 1 Hz logs (off for tests) | **MVP** |
| 20 Hz attitude from the AHRS log (`sensors_*.csv`) when the aircraft recorded it, for upset and aerobatic debriefs | R2 |
| Cabin audio (RB-08 `vrin_`/`vrout_` recordings) played in sync. Caveat: their names carry the Pi clock, which is wrong before a GPS fix | R2 |
| "What-if" failure overlay (GPS loss, AHRS fail, pitot or static block, EMS anomalies) to discuss procedures at a chosen moment | R2 |
| Anonymised export when a flight leaves the school (RB support, a public fixture) | R2 |
| Debrief report (PDF or HTML): events, bookmarks and notes for the student's file | R2 |
| Interactive option with a licensed simulator (X-Plane 12 Professional, FlightGear), using the same instructor page | Later |

### C — Other simulators and games

| Feature | Priority |
|---|---|
| ForeFlight-UDP adapter (GPS, attitude, traffic level) | **MVP** for C |
| FlightGear adapter plus shipped `rbsim.xml` | R2 |
| Published RB-SIM-IF spec ([protocol.md](protocol.md)) and a reference "push JSON to the bridge" adapter for third parties | R2 |
| Prepar3D, DCS, Infinite Flight | Later |

## 5. Out of scope for v1

- Training credit. Any regulatory qualification work (decision D7).
- Sensor-level IMU simulation and AHRS error modelling. v1 injects attitude as a state.
- CAN (mag-can-bridge) and SDR inputs (ADS-B, UAT, OGN receivers). Traffic comes from the
  sim's AI.
- Radio audio and intercom. COM frequency sync is R2.
- Any change to RB flight builds' behaviour.
