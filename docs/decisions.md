# Decisions and open questions

Newest entries go on top inside each section. Status is one of: **proposed** (awaiting
the maintainer), **accepted**, or **superseded**. Never delete an entry. Supersede it and
link the replacement.

## Decisions

### D23 — The C# wrapper answers the HMI's REST calls and keeps the addons bridge (accepted, 2026-10-29)
- **Request** (maintainer): add the REST answers D22 left out, so the HMI at `/RB-01/`
  stops failing on them.
- **Empty lists:** `GET /addons`, `/radio`, `/playback` and `/updates/available` answer
  `[]`, the daemon's answer when it has nothing to report.
  - **Addons:** the addons in `RB-01/addons/` write the settings files (`settings`) and the
    magnetometer calibration (`magnetometer`), which the wrapper doesn't keep. So the
    wrapper lists none, rather than plates that fail when used.
  - **Updates:** a SIM build never offers flight packages (rule 1), and
    `/updates/install` is not served.
- **The addons bridge** (`/bridge/float`) is kept, because the HMI publishes the QNH in use
  there. `RbSim.Core.Rb01Bridge` holds it in memory, as the daemon does.
  - A POST merges a JSON object of float32 numbers.
  - A GET returns the map.
  - `/bridge/float/ws` sends the map on each change, checked at 5 Hz, and nothing on
    connect.
- **Differences from the daemon:**
  - **No flight data from the bridge.** The daemon takes `ias_mps` as the indicated
    airspeed when it has no pitot sensor. The wrapper ignores it, so the airspeed comes
    from the source only.
  - **A bad post gets 400 and stores nothing.** That covers a value that isn't a finite
    number, a key longer than 64 characters, or a key beyond the 256th. The daemon logs the
    error and answers 200.
  - **A foreign web origin gets 403,** as for `/api/control` (D20). Any content type is
    accepted, because the addons' own `curl -d` examples post with curl's form default.
- New requirement RB-SIM-HMI-050.
- **Not done:** `/favicon.ico`, which is a static file: copy roastbeef's `web/favicon.ico`
  into `wwwroot/`. Also the SIM annunciation on every plate (HMI-030).

### D22 — The C# wrapper serves the RB-01 daemon's HMI sockets (accepted, 2026-10-28)
- **Request** (maintainer): generate the WebSockets the RB-01 HMI needs, so the HMI at
  `/RB-01/` (D21) runs on the wrapper's data.
- **Shape:**
  - `/situation` (10 Hz), `/traffic` and `/radar` (1 Hz), `/ems` (2 Hz) and `/status`
    (1 Hz) carry the daemon's own frames: `SituationData`, `TrafficInfo`, the EMS map and
    `globalStatus`.
  - `RbSim.Core.Rb01Hmi` builds them from the RB-SIM-IF/1 state message, as an RB-01 hub
    would after `POST /sim/state`, so units and staleness stay in one place.
  - `/autopilot`, `/keypad`, `/alerts`, `/radioStatus`, `/bridge/float/ws`, `/gdl90` and
    `/weather` are accepted and stay quiet. (Since D23, `/bridge/float/ws` carries the
    addons bridge.)
  - This is the socket half of hub-lite (protocol §4.3), in the wrapper. It is new
    requirement RB-SIM-HMI-040.
- **Safety (SAF-050):**
  - The HMI checks validity only through the daemon's markers, and its altitude and speed
    tapes check nothing. So each frame is built from scratch.
  - A stale group goes out as the daemon's no-data state (zeros) plus its markers:
    `ahrs.Invalid` 3276.7, `GPSFixQuality` 0 with accuracy 999999, `BaroSourceType` 0,
    `IndicatedAirSpeed` 0. A last value is never resent.
  - EMS has no invalid state in the HMI, so stale EMS keys go out as 0.
- **Finding for the roastbeef maintainer, not changed here (rule 3):**
  - The PFD altitude tape (`GPSAltitudeMSL`), the altimeter (`BaroPressureAltitude`) and
    the speed tape fall back to `GPSGroundSpeed` without checking `GPSFixQuality` or
    `BaroSourceType`.
  - The EMS plates keep the last value forever.
  - With the real daemon, a lost GPS or baro therefore leaves those indications frozen on
    the last value.
- **Synthetic values** while fresh: 8 satellites, accuracy 3/4 m, NACp 10,
  `BaroSourceType` 1 (verify against the `BARO_TYPE_SIM` proposal, T3.1). Traffic `Alt`
  is MSL with `AltIsGNSS` true (verify, T3.1).
- **Not done:**
  - the HMI's REST calls (`/addons`, `/radio`, `/playback`, `/bridge/float`,
    `/updates/available`), done in D23;
  - the SIM annunciation on every plate (HMI-030).

### D21 — A Linux build of the C# wrapper, and the RB-01 HMI served at /RB-01 (accepted, 2026-10-28)
- **Request** (maintainer): a Linux-compatible build of the wrapper (D19). Also a
  configuration parameter that serves an existing RB-01 website at `/RB-01`, for now
  `/home/stefano/roastbeef/web/RB-01`.
- **Build:**
  - `rbsim-msfs` builds for the host by default: win-x64 on Windows, linux-x64 elsewhere.
    `-r linux-arm64` also works.
  - win-x64 stays the only target with the msfs source. `SimConnect.dll` is Windows-only,
    so on Linux `--source msfs` exits with code 2 and a clear message; it doesn't retry
    forever. The synthetic source, the API, the WebSocket and the serial outputs run on
    Linux.
  - CI's `package` job is now a matrix: win-x64 zip and linux-x64 tar.gz, each
    smoke-tested on its own OS. A release attaches both.
  - On Linux the serial port has a native half, `libSystem.IO.Ports.Native.so`, which
    ships beside the executable. A missing copy is now a per-device error. Before this,
    it was an unhandled exception that killed the wrapper.
- **HMI:**
  - `rb01_web_dir` / `--rb01-web-dir` serves that folder's static files at `/RB-01/`,
    with `no-cache`. A missing folder is a startup error.
  - The HMI's shared files (roastbeef `web/` `/css`, `/js`, `/img`, `/plates`,
    `/settings`, `/synthview`) are copied into `wrapper-cs/src/RbSim.Msfs/wwwroot/` by the
    maintainer. They ship beside the executable and are served at the root, so the HMI
    loads without a roastbeef checkout. The copy has to be refreshed when roastbeef
    changes. `settings/hobbsmeter.json`, a real flight record, is git-ignored.
  - The daemon's WebSockets (`/situation`…) and REST calls still return 404, so the
    plates show no data. See
    [wrapper-cs/README.md](../wrapper-cs/README.md#rb-01-hmi-at-rb-01).
  - A roastbeef-shaped `/situation` feed is a separate decision. It overlaps with the
    RB-01 hub role of D16 and with the open D19 question of the Go wrapper.
- **Local configuration:** `wrapper-cs/rbsim-msfs.json` is git-ignored, because ports and
  folders are per machine.

### D20 — Radio and control write API in the C# wrapper, local-only by default (accepted, 2026-10-28)
- **Request** (maintainer, test-machine session): add an API for radio/comm and actuator
  management to the C# wrapper (D19).
- **Shape:**
  - `GET /api/radio` reads COM1/2, NAV1/2 and the transponder.
  - `GET` and `POST /api/control`, plus `{"control": …}` over the WebSocket, write a
    whitelist of controls: radios, transponder, autopilot modes and targets, the sim
    altimeter, flaps, trim, gear and parking brake.
  - The actuator table and the read-back state machine are pure code in `RbSim.Core`
    (`Actuators`, `ActuatorManager`), unit-tested and shared by the MSFS and synthetic
    sources.
  - See [wrapper-cs/README.md](../wrapper-cs/README.md#radio-and-control-api).
- **Rules carried over:**
  - explicit-state events only (D15, MSF-090); radio swaps become two explicit sets;
  - writes only when the read-back differs, at most 2 Hz per control (§5);
  - every write is verified by read-back within 2 s: `mismatch` is reported and never
    retried (MSF-100);
  - an event the sim doesn't know makes only its own control `unsupported` (as MSF-130
    does for datums).
- **Access:**
  - Reads stay open to the LAN.
  - Writes are limited to this PC unless `control.allow_remote` is set. They need
    `application/json` and refuse a foreign `Origin`, so a web page can't drive the
    simulator through localhost.
  - Writes can be switched off (`--no-control`) or limited by group.
- **Not RB-SIM-IF:** this API is the wrapper's own, for the instructor page, tools and tests.
  RB-01's autopilot intents still arrive in the `/sim/state` response, and T5.1 coupling
  (`off|bug|full`, default `off`) will drive the same actuator layer. COM sync with the
  RB-01 radio plate stays R2 (protocol §5).
- **Unverified:** every event and read-back unit against live MSFS (L1, L7), listed in
  the README.

### D19 — A C# MSFS wrapper on the Windows test machine, serving avionics status over HTTP and WebSocket (accepted, 2026-10-28)
- **Request** (maintainer, in the test-machine session): code the MSFS wrapper in C#. It
  starts with SimConnect and a web service plus WebSocket that clients poll for the
  avionics status. A direct USB link to RB-02 follows (GPS NMEA plus the RB sentences).
- **Shape:** `wrapper-cs/` holds the .NET 10 solution: `RbSim.Core` (pure), the
  `rbsim-msfs.exe` program, and xunit tests. See [wrapper-cs/README.md](../wrapper-cs/README.md).
- **Transport:** P/Invoke to the native `SimConnect.dll`, loaded at run time in the §3.1
  discovery order. It doesn't use the managed SDK assembly, so the build needs no SDK and
  never bundles the DLL (Q14).
- **Contract:** the HTTP and WebSocket payloads are the RB-SIM-IF/1 state and traffic
  messages, unchanged (IF-090). One sender's output can therefore later be POSTed to an
  RB-01 hub, or turned into the device lines of §4.4.
- **Relation to D17 and D18:** this is still a make (D18), but it narrows D17 for MSFS on
  Windows. Still open with the maintainer: whether the Go wrapper keeps the Linux/Pi and
  standalone pure-protocol builds, or C# replaces it. Until then, plan tasks T2.x describe
  the Go layout. The C# wrapper implements the same mapping tables (protocol §3.1) and the
  same golden values.
- **Consequence:** the C# code is developed on the Windows test machine.
- **CI** (`.github/workflows/wrapper-cs.yml`, 2026-10-28):
  - GitHub Actions builds and tests it on both Linux and Windows, then publishes and
    smoke-tests the self-contained `rbsim-msfs.exe` on Windows;
  - a `wrapper-cs-v*` tag turns that build into a release.
  - The "test machine runs CI binaries" rule of CLAUDE.md therefore applies to the CI zip.
    The Linux box can also run the core tests with a .NET 10 SDK.

### D18 — Make the wrapper; adopt open-source connectors only around it (proposed, 2026-10-28)
- **Question:** could MobiFlight Connector or another free and open-source connector
  replace the wrapper? The analysis is in [make-or-buy.md](make-or-buy.md).
- **Answer: no, the wrapper is a make.** No connector streams a validated whole state to a
  hub over RB-SIM-IF/1 (D16), and none runs on Linux and the Pi as a single binary
  (D17). MobiFlight works output by output to USB boards, on Windows only.
- **Adopt around it:**
  - MobiFlight Connector **side by side** for switches and knobs (no work);
  - the **MobiFlight WASM module** (MIT), optional, for LVars on complex aircraft (R2);
  - **ForeFlight-UDP producers** such as fs2ff (Unlicense) through the T5 adapter, as a
    degraded zero-install path;
  - MIT Go SimConnect bindings as a **reference** for T2.3, not a dependency.
- **Evaluate later, STOP:** RB-02 answering MobiFlight's board protocol in a SIM build, for
  builders without an RB-01. It touches rb-esp and the product scope.
- **Still rejected:** GPL, AGPL or NOSA code (Little Navconnect, ExtPlane, XPlaneConnect,
  Python-SimConnect) can't go into the commercial edition (D9).

### D17 — The wrapper is a Go program cross-compiled from Linux, in an SDK build and a standalone build (proposed, 2026-10-26)
- **Request** (maintainer): the wrapper is developed under Linux and also runs on Windows.
  On Windows it comes in two builds: with the MS Flight Simulator SDK, and standalone.
- **Proposal:** one Go module `wrapper/`, `CGO_ENABLED=0`, four artifacts from one Linux
  `make`:

  | Artifact | Transport |
  |---|---|
  | Linux amd64 | pure SimConnect protocol over TCP |
  | Linux arm64 (the RB-01 Pi) | pure SimConnect protocol over TCP |
  | Windows standalone `rbsim-wrapper.exe` | pure protocol over the pipe or TCP |
  | Windows SDK `rbsim-wrapper-sdk.exe` (tag `simconnect_sdk`) | the official `SimConnect.dll`, bound at run time |

  Details in [wrapper.md](wrapper.md).
- **Why Go rather than Python for this program:**
  - It cross-compiles Windows executables from Linux with no Windows build machine.
    PyInstaller can't cross-build, so Python would need a Windows runner or Wine, and
    there's no Wine here.
  - Build tags are a natural fit for the two Windows builds.
  - It produces a single static binary for consumers, with no Python runtime.
  - The same code runs on the RB-01 Pi.
  - The RB-01 team already works in Go.
- **Verified 2026-10-26:** a scratch module built all four artifacts from this box, vet
  ran on the Windows+SDK combination, and only the SDK `.exe` references
  `SimConnect_Open`.
- **Python stays** for what reuses roastbeef's Python tooling: the flight-log replay,
  hub-lite (FakeStratux), the validator `rbsim check`, the instructor page and the mission
  tools. D1 is narrowed to those.
- **One contract, two implementations.** The Go wrapper and the Python tools both
  implement RB-SIM-IF/1 against the same golden messages (`tests/fixtures/contract/`).
- **Roastbeef keeps its own contract code** in `siminput.go`, tested against the same
  golden messages. Importing a package from this repo would add a dependency to the
  certified repo's `go.mod` and provenance list.
- **Consequences:**
  - M2, M5 (wrapper side) and M6 move from `rbsim/sources/*.py` to `wrapper/internal/…`.
  - The standalone builds' MSFS support depends on Q16, the pure-protocol licensing.
  - Until Q16 is answered, the standalone builds carry the other sources.

### D16 — RB-01 is the hub; one HTTP contract (RB-SIM-IF/1) from thin wrappers (accepted, 2026-10-26)
- **Chain:** simulator ⇄ SDK ⇄ wrapper → **RB-01** → RB-02/04/05/07. Every
  simulator-specific detail lives in the wrapper. RB-01 (`stratuxrun-sim` only) exposes
  **one** input, and drives everything downstream through its existing paths.
- **Transport: HTTP**, as the maintainer chose: `POST /sim/state` at 10–20 Hz on a
  keep-alive connection, `POST /sim/traffic` at 1 Hz, and `GET /sim/status`.
  - UDP and WebSocket were considered. HTTP is enough on a LAN at these rates.
  - **The JSON messages are the contract.** A faster transport can be added later carrying
    the same messages (RB-SIM-IF-090).
- **Return channel in the response.** RB-01's autopilot intents (magnetic heading bug, HDG
  mode, AP on) come back in each state response as **level-triggered** desired state. The
  wrapper needs no second connection, no `$GPAPB` parsing and no magnetic variation.
- **One message, fanned out inside RB-01 to the real paths:**
  - `gps` → synthesised NMEA → the real `processNMEALine`;
  - `ems` → the `/setEMS` code;
  - traffic → the external-traffic path;
  - `ahrs`, `baro` and `airspeed` → `mySituation`.

  Wrappers speak one protocol instead of four, and RB-01's real parsers and validity logic
  still run.
- **Field names and units are RB-01's own** (`SituationData`, `TrafficInfo`, EMS keys).
  The contract is self-checking against `GET /getSituation`.
- **Strict validation** (400 naming the field, nothing applied partially), and **single
  source** (409 for a second sender). This avoids the silent-drop behaviour of `/setEMS`
  and `/bridge/float`.
- **RB-01 feeds the ESP32 devices.** It adds `$RBATT` and `$RBADC` to its NMEA outputs
  in the rbsim build only, as it already does `$PFLAA`/`$RBEMS` for RB-04 and RB-05.
- **hub-lite.** The Python bridge implements the same contract on a PC for setups without
  RB-01 hardware: HMI through FakeStratux, ESP32 devices over USB, no autopilot intents.
- **Parallel development** through a contract test kit:
  - the reference sender (flight-log replay);
  - the reference receiver and validator (`python -m rbsim check`);
  - golden messages.

  The MSFS wrapper (Windows), the RB-01 module (Go) and the RB-02 firmware (C) can be
  built independently.
- **Removed:** the stock-build "D0" mode, which fed an unmodified RB-01 through TCP
  :30011, `/setEMS` and `/bridge/float` from outside. A flight build answers 404 and
  wrappers stop (RB-SIM-IF-080).
- Supersedes D3. Requirements: RB-SIM-IF-010…100, RB1-080, RB1-090; RB1-010/020/030/040/070
  and REV-010 withdrawn.

### D15 — MSFS write-back: explicit-state events, read-back, data-inferred pause (proposed, 2026-10-26)
- **Never send toggle events.** `AP_MASTER` and `AP_HDG_HOLD` flip whatever state the
  aircraft is in, so a missed read turns the autopilot *off*. Use `AUTOPILOT_ON/OFF` and
  `AP_HDG_HOLD_ON/OFF`.
- **Verify every write by read-back within 2 s.** The SDK doesn't warn that aircraft with
  their own autopilot logic may ignore standard events. Read-back turns that into a
  visible "aircraft does not accept standard autopilot events" status, instead of silent
  non-coupling.
- **Infer pause and disconnection from the data as well as from events.** Esc pause,
  Active Pause and photo mode send no reliable events, and MSFS 2024 omits `SimStop` on
  the main menu. So a frozen `SIMULATION TIME` means paused, and 2 s with no frames means
  disconnected.
- Requirements: RB-SIM-MSF-050, MSF-060, MSF-090, MSF-100.

### D14 — MSFS transport: official DLL first; pure protocol later (proposed, 2026-10-26; refined by D17: Go, `syscall` instead of `ctypes`, and the pure protocol becomes the standalone builds' transport)
- **MVP: `ctypes` over the official `SimConnect.dll`.** It is documented and official.
  The bridge runs on the Windows sim PC (or another Windows PC, using `SimConnect.cfg`).
  The DLL comes from the user's MSFS SDK install, as FSUIPC asks its users to do, because
  redistribution rights are unconfirmed (Q14).
- **R2: a pure-Python SimConnect wire-protocol client** over TCP or the Windows named pipe.
  - **Why:** it removes the SDK install, and it lets the bridge run off Windows, even on
    the RB-01 Pi, with the Windows PC only editing `SimConnect.xml`.
  - **Evidence it can work:** node-simconnect (LGPL-3.0) does exactly this for MSFS 2020
    and 2024, and reports the protocol as forward compatible.
  - **Risks:** the protocol is unofficial. LGPL code cannot go into a dual-licensed
    product, which constrains how ours can be written (Q16).
  - **Gate:** a differential test against the DLL transport (RB-SIM-MSF-140).
- **Rejected:** FSUIPC7 (paid, an extra install) and third-party Python wrappers
  (licence and maintenance risk for a small API surface).

### D13 — Schools replay their own flights to review student missions (accepted, 2026-10-26)
- The standalone simulator (D11) is a **debrief tool**. After a training mission, the
  school takes the flight log off **its own aircraft's RB-01**. The instructor reviews it
  with the student on the RB instruments: checkpoints, events against the aircraft's own
  limits, bookmarks and notes. There is no lesson library shipped by RB, and no flights
  are distributed between organisations.
- **Consequences:**
  - **Import from the aircraft is MVP.** Use `GET /flightlogs`, then
    `GET /logs/<name>.sqlite` **and** `<name>.sqlite-wal`. Consolidate the copy locally
    with `PRAGMA wal_checkpoint(TRUNCATE)`. Leave out the log still being written.
    - Why: switching the master off is a power cut, which leaves the flight in the
      `-wal`.
    - Tested 2026-10-26: such a copy opened `immutable=1` (as roastbeef `flightlog.py`
      does) reads as **empty**. After a local checkpoint it reads fully.
  - **Aircraft settings come from the school's own RB-01.** A GET-only capture is stored
    with the mission. This answers Q9.
  - **Student data stays local.** Student, instructor, exercise and notes live in a
    mission file on the school's PC. The simulator never uploads them, and they never go
    into this repo.
  - **Anonymisation only when a flight leaves the school**, for example to RB support or
    as a repo fixture (CLAUDE.md rule 6). It drops to R2 for schools. This answers Q10.
  - **Debrief aids become MVP:** an event list (exceedances of the aircraft's own EMS
    arcs, speed arcs, bank and G limits, traffic alarms, GPS/AHRS invalid periods), a
    track overview with click-to-seek, and instructor bookmarks.
  - **Failure overlay drops to R2** as a "what-if" discussion aid, because the point is
    reviewing what really happened.
  - **The aircraft must record:** `ReplayLog` must be on (it is off by default in
    roastbeef). This goes in the school setup checklist.
- Refines D11 (the lesson-library parts of D11 no longer apply).

### D12 — Live tests run in a separate session on the test machine (accepted, 2026-10-26)
- The maintainer provides the test machine (Windows with MSFS and the SDK, later RB
  hardware) to **another Claude session**.
- This Linux session builds everything that can be tested here. It leaves each live check
  as a checklist in [plan.md](plan.md) §"Test-machine session".
- The test-machine session follows CLAUDE.md §"If this session runs on the test machine".
  It reports results back into plan.md and commits captures as fixtures.
- Answers Q8.

### D11 — Scenario B is a standalone flight-log replay simulator (accepted, 2026-10-26; use case refined by D13)
- For FBO and flight-school training, the product does not run a third-party simulator. It
  **replays recorded RB flight logs through the real RB instruments**:
  - RB-01 as the HMI on a PC, or a real RB-01 with the `rbsim` build;
  - RB-02, RB-04, RB-05 and RB-07 over USB.

  The instructor controls the replay: lesson, play, pause, seek, speed, checkpoints and
  failure injection.
- **Consequences:**
  - No simulator licensing question for schools, so Q4 is closed.
  - The T1 replay source becomes a product, not just a dev tool, and the standalone
    simulator is the **first deliverable**: plan M1.
  - Students operate the avionics (QNH, pages, timers, checklist, autopilot planning, EMS
    and traffic reading) but do **not** fly the aircraft. A replay cannot be steered. That
    limit is stated in the instructor guide and the IOS.
  - Lessons need distributable flight logs, which means anonymisation (RB-SIM-RPL-060) and
    the recording aircraft's settings snapshot (RB-SIM-RPL-050).
  - X-Plane and FlightGear move to scenario C only. An interactive school setup can come
    later as an option.
- Supersedes D6 for scenario B.

### D10 — roastbeef gets build-tagged sim files and Makefile targets (accepted, 2026-10-26)
- The maintainer approved adding `main/siminput*.go` (`//go:build rbsim`) and Makefile
  targets to roastbeef.
- **Done 2026-10-26, uncommitted in roastbeef:**
  - `stratuxrun-sim`: `go build -tags rbsim`, version `<ver>-SIM`, honours `debug=true`.
  - `check-sim`: `go vet -stdmethods=false -tags rbsim ./main/` and
    `go test -tags rbsim ./main/`.
  - `clean` removes the binary, and `.gitignore` ignores it.
  - Neither target is in `all`, `dpkg` or `check`. CI is unchanged.
- **Verified:** `make stratuxrun-sim` built a binary carrying `2.4.0-SIM`, and
  `make check-sim` passed (vet clean, `main/` tests ok in 21 s). Both ran with no rbsim
  files yet.
- **Checked:** the `-SIM` suffix does not change the GDL90 version parse
  (`gen_gdl90.go:516` only looks for `rc`/`r`/`b`).
- Answers Q2. T3.1 still decides the design of `siminput.go` itself.

### D9 — AGPL-3.0 + commercial dual licence (accepted, 2026-10-26)
- `LICENSE` is now the AGPL-3.0 text (copied from roastbeef `agpl-3.0.txt`). The README
  states the dual licence, as rb-esp does.
- New source files carry the RB header from CLAUDE.md rule 7.
- Answers Q1.

### D8 — Level H imports FakeStratux from a roastbeef checkout (proposed)
- No vendored copy during development, so nothing drifts. Packaging (M8) vendors a pinned
  roastbeef `web/` and `test/display` subset, with its commit hash and AGPL notice.

### D7 — Non-credit trainer positioning (proposed)
- v1 is an avionics familiarisation and procedures trainer. It claims no training credit.
- Credit needs a complete FAA BATD/AATD or EASA BITD/FNPT qualified device, which means a
  device manufacturer and a regulator, and is out of scope for v1.
- The design keeps that path open: configuration ID, session records, IOS and measured
  latency. This applies all the more to a replay trainer, which has no flight controls.

### D6 — Default simulator per scenario (proposed; superseded for B by D11)
- **A:** MSFS 2020/2024.
- **B:** ~~X-Plane 12 Professional / FlightGear~~ → standalone replay (D11).
- **C:** ForeFlight-UDP, X-Plane 12, FlightGear and the open protocol.

### D5 — State-level injection for v1 (proposed)
- The simulator's attitude and air data are injected as the AHRS and baro outputs.
- Sensor-level injection is deferred to Later: synthesising raw IMU, baro and pitot
  samples and running goflying's AHRS. It is valuable for AHRS-failure training, but it
  adds AHRS error to every lesson and needs IMU kinematics.

### D4 — Sim code is gated at compile time (proposed)
- Go build tag `rbsim` and `RB_ENABLE_SIM` templates. A runtime setting alone is rejected,
  because a flight unit must not be one setting away from accepting simulated attitude.
- Gating also keeps roastbeef's certified-core sources unchanged. The Makefile side of the
  tag is in place (D10).

### D3 — Reuse existing RB inputs first (superseded by D16: the inputs are now reused *inside* RB-01 behind one contract)
- **Existing paths:**
  - GPS and traffic over NMEA TCP `:30011`;
  - EMS via `/setEMS`;
  - IAS via `/bridge/float`;
  - USB NMEA, FLARM and EMS sentences on ESP32.
- **New inputs, only where none exists (attitude and air data):**
  - RB-01 `/sim/ws`, rbsim builds;
  - RB-02 `$RBATT` and `$RBADC`, `RB_ENABLE_SIM` builds.
- Feeding GPS through NMEA exercises RB's real GPS parser and validity logic, which is
  closer to the aircraft than injecting fields directly.

### D2 — SI inside the bridge, RB contract units on the wire (proposed)
- One canonical model avoids N×M conversions. Sinks speak each RB contract in its own
  units: kt, ft and ft/min for `SituationData`, the aircraft profile's units for EMS. This
  way no RB code changes to accommodate us.

### D1 — The bridge is Python, with a stdlib core (proposed, 2026-10-26; narrowed by D17: the simulator wrapper is Go, and Python keeps the replay, hub-lite, validator and school tools)
- **Why Python:**
  - It imports roastbeef's `FakeStratux` and `flightlog.py` directly, which gives the
    standalone replay and Level H at almost no cost.
  - It matches the team's other sidecars (rb-vor, mag-can-bridge, rbvoice).
  - SimConnect is reachable through `ctypes`.
  - 20–30 Hz is far from any performance limit.
- **Alternatives considered:**
  - Go: one static binary that matches roastbeef, but it loses the Python reuse.
  - C#: native SimConnect, but Windows-centric, and the RB code has no C#.
- **Consequences:**
  - Windows users get a PyInstaller bundle (M8).
  - Optional dependencies only, such as `pyserial`.

## Open questions for the maintainer

| # | Question | Blocks |
|---|---|---|
| Q16 | **Pure-protocol SimConnect client (R2):** may it be written using node-simconnect (LGPL-3.0) as a *reference*, or must it be clean-room, from captures of the official DLL's traffic on the test machine? LGPL code can't be copied into a dual-licensed (commercial) product. A third option keeps node-simconnect unmodified as a separate sidecar process. | **standalone builds' MSFS support** (T2.6) |
| Q15 | **AGPL + proprietary DLL:** the bridge loads Microsoft's `SimConnect.dll` at runtime. Should XIAPROJECTS add an AGPL section 7 additional permission ("linking exception") for the SimConnect client library to `LICENSE`/README, as GPL projects that link proprietary platform SDKs often do? This is a legal decision for the copyright holder. | T2.3 |
| Q14 | **`SimConnect.dll` redistribution:** no official statement found. Two devsupport threads ask the same question and have no Microsoft answer (2026-10-26). Should XIAPROJECTS ask through devsupport? Until there's an answer, users install the MSFS SDK and the bridge finds the DLL (RB-SIM-MSF-010). | M8 bundle |
| Q13 | **Pilot school:** which school and aircraft go first? Do their RB-01s have `ReplayLog` on, and is `ReplayLogPath` the default `/var/log`? | T7.1 live check (L9) |
| Q12 | **Import from RB-Cloud:** schools that already upload with rb-cloud-upload could import from the cloud. RB-Cloud has no whole-file download today, only a list, a summary, CSV per table and KML. Should rb-cloud-php get an additive `GET /flights/{deviceId}/{file}/download` route? | R2 |
| Q11 | **Non-default `ReplayLogPath`** (a USB stick or a partition): roastbeef serves `/logs/<name>.sqlite` from there, but `<name>.sqlite-wal` falls back to `/var/log` (`managementinterface.go:2628`). Power-cut flights on such units then cannot be fully downloaded over Wi-Fi. Fix with a one-line roastbeef change (also match `.sqlite-wal`), or import from the stick? | T7.1, only if a school uses it |
| Q7 | Default reverse-channel coupling for scenario A: `bug` or `full`? (The proposal is `off` by default and `bug` in the A preset.) | T5.1 |
| Q6 | RB-Cloud for the simulator: SIM device IDs, a separate OTA component `avionics-sim`, and how debrief logs are shown | M8 |
| Q3 | RB-02 SIM templates: which `RB_LICENSE_TYPE`, which names, and which display sizes first? | T4.2 |
| ~~Q10~~ | Distributing lesson flights → **D13**: schools review their own flights; anonymise only when a flight leaves the school | — |
| ~~Q9~~ | Lesson aircraft settings → **D13**: captured from the school's own RB-01 and kept with the mission | — |
| ~~Q5~~ | First lesson aircraft → **D13**: no RB lessons; the pilot school is Q13 | — |
| ~~Q8~~ | Test machines → **D12**: provided in a separate Claude session | — |
| ~~Q4~~ | MSFS commercial use for schools → **D11**: not needed, scenario B is a standalone replay | — |
| ~~Q2~~ | roastbeef build-tagged files and Makefile target → **D10**: accepted, targets added | — |
| ~~Q1~~ | Repo licence → **D9**: AGPL-3.0 + commercial | — |
