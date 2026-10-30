# rbsim-msfs: C# MSFS wrapper (decision D19)

Reads the user aircraft and traffic from Microsoft Flight Simulator 2020 / 2024 through
SimConnect. It serves the avionics status over HTTP and WebSocket, in the RB-SIM-IF/1
message shapes of [../docs/protocol.md](../docs/protocol.md) §4.1: RB-01 field names and
units, and `null` for an invalid group.

Later outputs go through the same `SimState`: a direct USB serial link to RB-02 (GPS NMEA
plus the RB sentences of protocol §4.4), and POSTing to an RB-01 hub.

It builds for Windows x64 and Linux (x64, arm64). On Linux the msfs source isn't
available, because `SimConnect.dll` exists only on Windows. Everything else works there:
the synthetic source, the API, the serial outputs, and the RB-01 HMI at `/RB-01/`.

## Layout

| Path | What |
|---|---|
| `src/RbSim.Core/` | pure, no I/O: `SimState` (SI), `Units`, `MsfsMap` (SimVar → SimState, signs), `MsfsDetectors` (pause, link, teleport), `DataLayout` (SimConnect block decoding), `RbSimIf` (contract messages), `Nmea`/`DeviceLines` (serial sentences), `Actuators`/`ActuatorManager` (writable controls, key events, read-back) |
| `src/RbSim.Msfs/` | the `rbsim-msfs` program (`rbsim-msfs.exe` on Windows): P/Invoke to `SimConnect.dll` (`SimConnectNative`, `MsfsSource`), `SyntheticSource`, the Kestrel HTTP/WebSocket server (`Program.cs`), the live page `wwwroot/index.html` |
| `tests/RbSim.Core.Tests/` | xunit tests on the core, tagged `// Covers: RB-SIM-…` |

SimConnect is called through P/Invoke on the **native** `SimConnect.dll`, not through the
managed `Microsoft.FlightSimulator.SimConnect.dll`:

- the build needs no SDK;
- the DLL is the one the user installed, found at run time in the protocol §3.1 order
  (`--simconnect-dll`, `%MSFS2024_SDK%`, `%MSFS_SDK%`, `C:\MSFS 2024 SDK`, `C:\MSFS SDK`);
- the DLL is never bundled or committed (Q14).

## Build, test, run

Needs the .NET 10 SDK. A plain build targets the host: win-x64 on Windows, linux-x64
elsewhere. `-r` picks another target.

```powershell
cd wrapper-cs
dotnet test RbSim.Wrapper.slnx
dotnet run --project src/RbSim.Msfs -- --source synthetic     # no simulator needed
dotnet run --project src/RbSim.Msfs                           # MSFS through SimConnect (Windows)
dotnet publish src/RbSim.Msfs -c Release -o out               # out\rbsim-msfs.exe
```

On Linux:

```sh
cd wrapper-cs
dotnet test RbSim.Wrapper.slnx
dotnet run --project src/RbSim.Msfs -- --source synthetic --rb01-web-dir ../../roastbeef/web/RB-01
dotnet publish src/RbSim.Msfs/RbSim.Msfs.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o out-linux
./out-linux/rbsim-msfs --source synthetic --serial /dev/ttyACM0=rb02
```

- **`--source msfs` is refused on Linux** with exit code 2, instead of retrying a DLL that
  can never load. The default source is still `msfs`, so set `"source": "synthetic"` in
  the configuration or pass `--source synthetic`.
- **Keep `libSystem.IO.Ports.Native.so` beside `rbsim-msfs`.** It is the native half of
  the serial port, and single-file publishing leaves it outside the executable. Without
  it, every serial output reports the load error in `/api/status` and retries every 2 s.
  The web service keeps running.
- **Serial ports** are `/dev/ttyACM0` (the ESP32-S3 USB console) or `/dev/ttyUSB0`. The
  user needs to be in the `dialout` group. `--list-ports` lists them.
- **linux-arm64** (the Pi) builds the same way with `-r linux-arm64`. It is not packaged
  by CI.
- **win-arm64 is refused at build time** (NETSDK1032), because `SimConnect.dll` is x64.

The page is at http://localhost:8090/. `--help` lists the options. The default bind is
`0.0.0.0:8090`, so devices on the LAN can poll it. Windows asks once for a firewall rule:
allow it on **private** networks only. Use `--listen 127.0.0.1` to keep the wrapper local.

| Endpoint | Content |
|---|---|
| `GET /RB-01/` | the RB-01 HMI files from `rb01_web_dir`, when set ([below](#rb-01-hmi-at-rb-01)) |
| `WS /situation`, `/traffic`, `/radar`, `/ems`, `/status` | the RB-01 daemon's HMI sockets, in its frame shapes ([below](#the-hmi-sockets-d22)) |
| `GET /addons`, `/radio`, `/playback`, `/updates/available`; `GET`/`POST /bridge/float`, `WS /bridge/float/ws` | the HMI's REST calls and the addons bridge ([below](#the-hmis-rest-calls-d23)) |
| `GET /api/state` | RB-SIM-IF/1 state message, the body of `POST /sim/state` |
| `GET /api/traffic` | RB-SIM-IF/1 traffic message |
| `GET /api/simstate` | canonical `SimState`, SI, snake_case |
| `GET /api/status` | link, DLL path, sim version, rates, rejected datums, last SimConnect exception |
| `WS /ws?rate=10` | state messages at 1–30 Hz, plus a traffic message (it has `targets`) once a second, unchanged (RB-SIM-IF-090). Accepts `{"control": {...}}`; `&control=1` adds control status at 2 Hz |
| `GET /api/radio` | COM1/2 and NAV1/2 active/standby, NAV1 radial, transponder code/state/ident |
| `GET /api/control` | every writable control: read-back, and the last command's status |
| `POST /api/control` | write radios, autopilot, sim altimeter, cockpit controls ([below](#radio-and-control-api)) |

## CI/CD

[.github/workflows/wrapper-cs.yml](../.github/workflows/wrapper-cs.yml) runs on every
push to `main`, on pull requests that touch `wrapper-cs/`, and on demand.

| Job | Runner | What it does |
|---|---|---|
| `test` | Ubuntu + Windows | `dotnet format --verify-no-changes` (Linux), Release build of the whole solution for the runner's OS, unit tests |
| `package` | Windows (win-x64) + Ubuntu (linux-x64) | self-contained single-file `rbsim-msfs.exe` / `rbsim-msfs` (no .NET install needed, about 50 MB); fails if any `SimConnect*.dll` is in the output; runs [ci/smoke.ps1](ci/smoke.ps1) against it on its own OS; uploads `rbsim-msfs-<version>-win-x64.zip` and `rbsim-msfs-<version>-linux-x64.tar.gz` (with `libSystem.IO.Ports.Native.so`), each with a `.sha256`, as the artifacts `rbsim-msfs-win-x64` and `rbsim-msfs-linux-x64` |
| `release` | Ubuntu | only for a tag `wrapper-cs-v<semver>`: creates the GitHub release with both archives and checksums. A version containing `-` is marked pre-release |

Versions:

- untagged builds are `<csproj Version>-ci.<run number>`;
- a tag `wrapper-cs-v0.2.0` builds `0.2.0`.

The commit SHA is embedded in the product version.

To cut a release, set `<Version>` in `src/RbSim.Msfs/RbSim.Msfs.csproj`, then:

```powershell
git tag wrapper-cs-v0.2.0
git push origin wrapper-cs-v0.2.0
```

The test-machine session uses the CI zip, not a local build, so the binary under test is the
one CI produced (CLAUDE.md, test-machine section).

Run the same checks locally:

```powershell
dotnet format RbSim.Wrapper.slnx --verify-no-changes
dotnet test RbSim.Wrapper.slnx -c Release
dotnet publish src/RbSim.Msfs/RbSim.Msfs.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IsTransformWebConfigDisabled=true -p:DebugType=none -o out
./ci/smoke.ps1 -Exe out/rbsim-msfs.exe
```

On Linux, publish with `-r linux-x64` and run `pwsh ci/smoke.ps1 -Exe out/rbsim-msfs`.

Line endings are LF on every OS (`.gitattributes`, `.editorconfig`), so the format check
gives the same answer here and on the Linux runner.

## Configuration file and USB serial output

Copy [rbsim-msfs.example.json](rbsim-msfs.example.json) to `rbsim-msfs.json`, in the
current folder or beside the executable, or pass `--config PATH`. Command-line options
override the file. Comments and trailing commas are allowed. An unknown key is an error, so
a misspelt setting is never silently ignored. `rbsim-msfs.json` is git-ignored, because
ports and folders differ per machine.

Each entry in `serial` feeds one directly connected RB device with the protocol §4.4
device lines: GPS NMEA, FLARM, and the RB sentences. `rbsim-msfs --list-ports` lists the
COM ports, or `/dev/tty*` on Linux.

| Profile | Lines | Works with |
|---|---|---|
| `nmea-flarm` | `$GPRMC`+`$GPGGA` 5 Hz, `$PFLAU`+`$PFLAA` 1 Hz | today's USB firmware: `RB_ENABLE_USB_NMEA` (RB-02/RB-07), `RB_ENABLE_USB_FLARM` (RB-05/RB-07) |
| `rb02` | `$RBATT` 20 Hz, `$RBADC` 10 Hz, `$GPRMC`+`$GPGGA` 5 Hz | an `RB_ENABLE_SIM` firmware build (T4.2, not built yet) |
| `rb04` | `$RBEMS` 5 Hz, `$RBCYL` every 2 s | RB-04 EMS-USB (`RB_ENABLE_EMS`) |
| `rb05` | `$GPRMC`, `$PFLAA`, `$PFLAU` 1 Hz | RB-05 WIRED-USB |
| `rb07` | `rb02` + `rb05` | RB-07 USB, SIM build for the attitude and air data |
| `rb09` | RB-09's own `$RBATT` 20 Hz, `$RBAIR` 10 Hz, `$GPRMC`+`$GPGGA` 5 Hz | RB-09 remote display, today's firmware (`RB_ENABLE_SLAVE_DEVICE`). Not `rb02`: RB-09 rejects that `$RBATT` (protocol §4.4, D24) |

| `rb01-nav` | `$GPRMB`, `$GPAPB`, `$GPBOD`, `$GPXTE` 1 Hz | RB-01's UDP autopilot input (the `udp` outputs, [below](#autopilot-guidance-path-and-hsi-d25)), or a serial autopilot |

- `rates` overrides one sentence rate in Hz. Setting a rate to 0 removes that sentence.
  The RB-09 sentences are `rbattslave` and `rbair`; the guidance sentences are `gprmb`,
  `gpapb`, `gpbod` and `gpxte`.
- `port: "file:capture.nmea"` writes the same lines to a file, for checking without
  hardware.
- An unplugged device is retried every 2 s.
- What the device prints is drained and logged, and the last line appears in
  `/api/status`.
- The line rules (ESP-010) are enforced:
  - XOR checksum, ASCII only, at most 250 characters per line;
  - the RB and FLARM numeric fields fit int16. Out-of-range values peg at the range
    limit, for example turn rate ±99.9.
- Data that isn't fresh never goes out as valid:
  - `$RBATT`/`$RBADC` go out with empty fields and status 0;
  - `$GPRMC`/`$GPGGA` stop, so the firmware's 3 s GPS timeout shows INOP;
  - FLARM sends no `$PFLAA` and reports GPS 0 in `$PFLAU`.
- FLARM limits:
  - at most 10 targets, nearest first (ESP-080);
  - targets more than 32 767 m away north/south, east/west or vertically are dropped,
    because the FlarmObstacle fields are int16.

### Checked against the public GitHub sources (2026-10-28)

Sources: `xiaprojects/roastbeef` at ecfbb20 and `xiaprojects/rb-esp` at 394d675.

- **The format follows roastbeef**, the producer RB-02/04/05 already run against in the
  aircraft:
  - `$GPRMC`/`$GPGGA` use 5-decimal minutes, and RMC carries the trailing mode `A`
    (`makeGPRMCString`, `makeGPGGAString`);
  - `$PFLAU` is the no-alarm form;
  - `$PFLAA` follows `makeFlarmPFLAAString`: ID `XXXXXX!CALLSIGN`, turn rate `0`, integer
    track and speed, climb rate ±32.7;
  - per cycle, every `$PFLAA` is sent first, then one `$PFLAU` (`traffic.go`);
  - `$RBEMS`/`$RBCYL` field order and rates come from `ems.go` (the `rb04` profile). The
    same order is in the doc comment of `RB04_NMEA_Parser.c`.
- **Checksums match** roastbeef's example lines (unit tests).
- **RB-02 `nmea_RMC_mini_parser` / `nmea_GGA_mini_parser`** (`RB02.c:930`/`:979`):
  - time is `hhmmss.ss`, speed in kt, track, GGA fix quality, satellites, HDOP, and
    altitude in m. All match;
  - the parser ignores the RMC A/V status and never checks the checksum. That's why RMC
    and GGA are not sent at all while the position is stale.

Still unverified:

- **RB-05/RB-07 FLARM parsing.** The public `RB05_FlarmParser.c` is a stub ("final source
  code will be released once we finish refactoring"), so it's unknown how
  `ID!CALLSIGN` fills `tail[8]`. The callsign is cut to 7 characters as a guess. The
  `RB_Usb.c` line reader and the RB-04 parser are stubs too.
- **Firmware finding, not changed here:** the RB-02 mini parsers don't handle a `-` sign
  correctly. `-3.0` is read as `-7.0`, so a GGA altitude below sea level is misread. It
  is affected by the same bug with roastbeef's output; report it to the rb-esp maintainer.
- Whether DTR/RTS reset an ESP32-S3 **on Windows**. On Linux, checked with an RB-09
  (D24): it receives with DTR off or on, but opening the port with both lines off resets
  it once, so set `"dtr": true` on that output ([rb-knowledge-base](../docs/rb-knowledge-base.md)).
- Behaviour on a real device, except an RB-09 on profile `rb09`. On Linux it followed the
  synthetic source: attitude, QNH and VS read back from its own NMEA output (D24).

## RB-01 HMI at /RB-01

`rb01_web_dir` in the configuration, or `--rb01-web-dir PATH`, serves an RB-01 HMI folder
at `/RB-01/`. Typically that is a roastbeef working tree's `web/RB-01`, for example
`/home/stefano/roastbeef/web/RB-01`.

- **Path.** A relative path is relative to the current folder. A folder that doesn't
  exist is an error at startup (exit code 2), never a silent 404.
- **Reload.** Files are sent with `Cache-Control: no-cache`, so the browser revalidates
  them and an edit in the working tree shows on the next reload.
- **What is served.** `/RB-01` redirects to `/RB-01/`, which serves `index.html`. Dotfiles
  are never served, and nothing outside the folder can be reached.
- **Status.** `/api/status` reports the folder in use as `rb01_web_dir`.

The HMI also loads files from roastbeef's `web/` root: `/css`, `/js`, `/img`, `/plates`,
`/settings` and `/synthview`. Copies of those folders live in `src/RbSim.Msfs/wwwroot/`.
The build copies them beside the executable as `wwwroot/`, and the wrapper serves them at
the root, with `no-cache`.

- **Not inside the single file.** A user can edit or replace them in place.
- **`.glb` models** are served as `model/gltf-binary`.
- **The status page is separate.** `wwwroot/index.html` is the wrapper's own page. It is
  embedded and served at `/`, and never copied there.
- **It is a copy.** Refresh it from roastbeef's `web/` when the HMI changes.
- **Never commit a real flight record.** `settings/hobbsmeter.json` came over with the
  copy and is git-ignored (CLAUDE.md rule 6).

### The HMI sockets (D22)

The wrapper serves the RB-01 daemon's HMI WebSockets, so the HMI runs on the wrapper's
data. The frames have the daemon's shapes (roastbeef `main/managementinterface.go`). They
are built by `Rb01Hmi` from the same state message as `/api/state`, as an RB-01 hub would
after `POST /sim/state`.

| Socket | Frame | Rate |
|---|---|---|
| `/situation` | `SituationData` | 10 Hz |
| `/traffic` | one `TrafficInfo` per target fresher than 10 s, nearest first | 1 Hz |
| `/radar` | the daemon's radar settings (`RadarLimits` 2000, `RadarRange` 10), then as `/traffic` | 1 Hz |
| `/ems` | the EMS map: every key of the contract's `ems` group, in `ems.json` units | 2 Hz |
| `/status` | the `globalStatus` fields the HMI reads; `Version` ends in `-SIM` | 1 Hz |
| `/bridge/float/ws` | the addons bridge map, when it changes ([below](#the-hmis-rest-calls-d23)) | checked at 5 Hz |
| `/autopilot` | `AutopilotStatus` from the sim GPS's active leg ([below](#autopilot-guidance-path-and-hsi-d25)) | 1 Hz |
| `/keypad`, `/alerts`, `/radioStatus`, `/gdl90`, `/weather` | none: accepted and quiet, as the daemon's are while nothing happens | — |

A plain HTTP request to one of these paths gets 404, except `/autopilot` (below).

**Stale data never looks valid** (RB-SIM-SAF-050). The HMI believes every frame: the PFD
altitude and speed tapes read `GPSAltitudeMSL`, `BaroPressureAltitude` and
`GPSGroundSpeed` without a validity check. So each frame is built from scratch and a last
value is never resent. A stale or invalid group goes out as the daemon's no-data state
(zeros, as in the HMI's own initial `window.situation`), plus the daemon's invalid markers:

| Group | Markers the HMI acts on |
|---|---|
| `ahrs` | `3276.7` (`ahrs.Invalid`) on every angle, turn rate, slip and G: the PFD draws its attitude failure |
| `gps` | `GPSFixQuality` 0, satellites 0, `GPSHorizontalAccuracy` 999999: no fix, and the radar's "GPS invalid" |
| `baro` | `BaroSourceType` 0, `BaroLastMeasurementTime` the zero time: no baro, and the radar's baro freshness test fails |
| `airspeed` | `IndicatedAirSpeed` 0: the speed tape shows the ground speed, labelled GS |
| `ems` | every key 0, the daemon's starting value. The HMI has no invalid EMS state, so this is the closest it can show |

The `*Time` fields use a "stratux clock": monotonic seconds counted from year 1, as the
daemon's. A fresh group carries the frame's clock and a stale one the zero time.
`GPSLastGPSTimeStratuxTime` is always the frame's clock, because the radar and autopilot
plates use it as "now" when they judge the baro and AHRS.

**Fields a simulator doesn't have** are synthetic while the position is fresh: 8
satellites (as in `$GPGGA`), accuracy 3 m / 4 m, NACp 10, and `BaroSourceType` 1 (the
value of a real baro, verify against the BARO_TYPE_SIM proposal of T3.1). `GPSTurnRate`
is always invalid: the HMI only falls back on it when the AHRS is invalid, and in a
simulator that means the position is stale too. Traffic has `Last_source` 4 and
`TargetType` 1, as RB-01's FLARM path gives it. `Alt` is MSL with `AltIsGNSS` true
(verify, T3.1). `Age` is the time since the simulator reported the target; the HMI fades
a target with it and drops it after 59 s, as for any RB-01 traffic.

Measured with headless Chrome against the Linux build on 2026-10-28, roastbeef working
tree of that day:

- **All files load.** That is 97 files: everything under `/RB-01/` plus the shared ones.
- **Every socket connects.** The rates measured were `/situation` 10.2/s, `/ems` 2.1/s,
  and `/status`, `/traffic` and `/radar` about 1/s.
- **The plates run on the synthetic flight.**
  - The PFD shows the bank, IAS, altitude and auto QNH.
  - The aircraft plate shows fuel, EMS OK and GPS 8.
  - The radar shows the AI target with its relative altitude.
- **The REST calls answer** since 2026-10-29 ([below](#the-hmis-rest-calls-d23)). The
  only failing request left is `/favicon.ico`, which isn't in the `wwwroot/` copy (it is
  roastbeef's `web/favicon.ico`).
- **Not checked live:** the plates during a stale or disconnected simulator. The unit tests
  cover those frames.

### The HMI's REST calls (D23)

The HMI also calls some of the daemon's REST endpoints, on boot and on some plates. Each
answer is the daemon's own answer when it has nothing to report:

| Call | Answer | Why it is empty |
|---|---|---|
| `GET /addons` | `[]` | The addons in `RB-01/addons/` write the settings files and the magnetometer calibration, which the wrapper doesn't keep. The HMI then has no addon plates |
| `GET /radio` | `[]` | No radios on the wrapper; the radio plate shows none and posts nothing |
| `GET /playback` | `[]` | No radio recordings |
| `GET /updates/available` | `[]` | A SIM build never offers flight packages (CLAUDE.md rule 1); `/updates/install` is not served |

**The addons bridge** (`/bridge/float`) is kept, because the HMI publishes the QNH in use
there (`qnh`, `autoqnh`) for addons and probes:

- `POST /bridge/float` merges a JSON object of numbers into the map (float32, as on the
  daemon). The content type can be anything: the addons' own `curl -d` examples send
  curl's form default. An empty body or `null` changes nothing.
- `GET /bridge/float` returns the map.
- `WS /bridge/float/ws` sends the whole map whenever it changes, but not on connect, as
  on the daemon.
- The map lives in memory and is lost on restart.

The differences from the daemon:

- **Nothing posted reaches the flight data.** The daemon takes `ias_mps` as the indicated
  airspeed when it has no pitot sensor. The wrapper takes the airspeed from its source
  only.
- **A bad post is refused with 400 and stores nothing.** That covers a value that isn't a
  finite number, a key longer than 64 characters, or a key beyond the 256th. The daemon
  logs the error and answers 200.
- **A post from a browser page of another origin is refused with 403,** as for
  `/api/control`.

## Autopilot guidance, path and HSI (D25)

The sim GPS's active leg (the waypoint it flies to, and the one it comes from) becomes the
lateral guidance RB-01's autopilot computes. RB-01 draws the path and the HSI from it, in
two ways:

- **A real RB-01, over UDP.** The `rb01-nav` profile sends `$GPRMB`, `$GPAPB`, `$GPBOD`
  and `$GPXTE` at 1 Hz, in roastbeef `autopilot.go`'s layouts, to RB-01's UDP autopilot
  input. That is the input an EFB such as Easy VFR feeds (`autopilotudp.go`). On RB-01,
  turn on *Autopilot UDP* (`AutopilotUdp_Enabled`); the port is `AutopilotUdp_Port`,
  default 1234. Then:

  ```
  rbsim-msfs --udp 192.168.10.1          # RB-01's address; port 1234, profile rb01-nav
  ```

  or in the configuration file:

  ```json
  "udp": [ { "name": "RB-01 autopilot", "host": "192.168.10.1", "port": 1234, "profile": "rb01-nav" } ]
  ```

  From each `$GPRMB`, RB-01 stores a two-point plan (the aircraft, then the waypoint).
  The HSI plate steers to it, and synthview and the map draw it. RB-01 also forwards
  `$GPRMB` and `$GPAPB` to its own serial autopilot output.
- **The HMI the wrapper serves at `/RB-01/`.** `WS /autopilot` sends `AutopilotStatus`
  every second. `GET /autopilot` returns the daemon's waypoint list: the origin (past),
  then the destination (target). The HSI plate and the path read that list. `Active` is
  true while there is guidance, so the PFD course bug and the steer arrow show.

What the guidance is:

- It is computed from positions only, as roastbeef does (`Guidance`):
  - great-circle distance and bearing to the waypoint;
  - the leg course, origin to waypoint;
  - the cross-track from that line, + right of course, so the steer is L;
  - the closing speed, the ground speed along the bearing;
  - arrival inside 500 m.

  The sim's cross-track and desired-track SimVars are not used, because their signs and
  magnetic references are unverified. `$GPBOD` adds the magnetic course from the sim's
  variation; roastbeef leaves that field empty.
- With no active waypoint, or with a stale position or leg, **nothing is sent** and the
  HMI gets the daemon's idle frame and an empty list. Over UDP, RB-01 keeps showing the
  last leg it received. That is how its UDP input treats an EFB that stops (verify on the
  bench).
- A leg without an origin is flown as a direct-to from the aircraft: the course is the
  bearing and the cross-track is 0.
- The HMI's own autopilot commands change nothing here, because the sim's GPS owns the
  plan: `POST` (load a plan), `PUT` (start) and `DELETE` (stop) on `/autopilot` get the
  daemon's `[]`.
- `/api/status` shows the leg under `nav` and each UDP target under `udp`.

**Datagram padding.** RB-01's UDP input reads each datagram into one reused 2048-byte
buffer and parses the whole buffer, not just the bytes received. A shorter datagram would
therefore hand it the tail of the previous one again. To prevent that, every datagram is
padded with spaces to `pad_bytes` (default 1024). The fix belongs in roastbeef
(`string(p[:n])`); after that, `"pad_bytes": 0` turns the padding off.

**The MSFS variables** are `GPS IS ACTIVE WAY POINT`, `GPS WP NEXT LAT/LON/ALT/ID`, and
`GPS WP PREV VALID/LAT/LON/ID`. These are the SDK's GPS variables. Whether every avionics
package keeps them current is **not verified**: the WT G1000 NXi, G3X, GNS 430/530 and
MSFS 2024's own may not. Check each one on the test machine with a flight plan and a
direct-to.

## Radio and control API

`POST /api/control` with `Content-Type: application/json` and an object of controls.
Several can go in one request:

```powershell
$b = '{"com1_standby_hz": 121500000, "com1_swap": true, "xpdr_code": "7000",
       "hdg_bug_mag_deg": 270, "hdg_mode": true, "ap_master": true}'
Invoke-RestMethod http://localhost:8090/api/control -Method Post -ContentType application/json -Body $b
```

The answer is `202 {"ok": true, "accepted": {...}}`. The request is all or nothing: one
bad key returns `400 {"ok": false, "errors": [...]}`, and nothing is written. Without a
simulator connection the answer is 503.

| Control | Value | MSFS key event | Read-back |
|---|---|---|---|
| `com1_active_hz`, `com1_standby_hz`, `com2_…` | Hz, 118 000 000–136 990 000 | `COM_RADIO_SET_HZ`, `COM_STBY_RADIO_SET_HZ`, `COM2_…` | `COM ACTIVE/STANDBY FREQUENCY:n` |
| `nav1_active_hz`, `nav1_standby_hz`, `nav2_…` | Hz, 108 000 000–117 950 000 | `NAV1_RADIO_SET_HZ`, `NAV1_STBY_SET_HZ`, `NAV2_…` | `NAV ACTIVE/STANDBY FREQUENCY:n` |
| `com1_swap`, `com2_swap`, `nav1_swap`, `nav2_swap` | `true` | two explicit sets built from the read-back, not the swap toggle | as above |
| `xpdr_code` | `"7000"` (four octal digits) | `XPNDR_SET` (BCO16) | `TRANSPONDER CODE:1` |
| `xpdr_ident` | `true` | `XPNDR_IDENT_ON` | `TRANSPONDER IDENT:1` |
| `ap_master` | bool | `AUTOPILOT_ON` / `AUTOPILOT_OFF` | `AUTOPILOT MASTER` |
| `hdg_mode`, `nav_mode`, `alt_hold`, `vs_hold` | bool | `AP_HDG_HOLD_ON/OFF`, `AP_NAV1_HOLD_ON/OFF`, `AP_ALT_HOLD_ON/OFF`, `AP_VS_ON/OFF` | the matching `AUTOPILOT … LOCK/HOLD` |
| `hdg_bug_mag_deg` | 0–360 | `HEADING_BUG_SET` | `AUTOPILOT HEADING LOCK DIR` (±1°) |
| `alt_sel_ft` | 0–60 000 | `AP_ALT_VAR_SET_ENGLISH` | `AUTOPILOT ALTITUDE LOCK VAR` (±10 ft) |
| `vs_sel_fpm` | −6 000–6 000 | `AP_VS_VAR_SET_ENGLISH` | `AUTOPILOT VERTICAL HOLD VAR` (±50) |
| `qnh_hpa` | 900–1 100 | `KOHLSMAN_SET` (mb × 16) | `KOHLSMAN SETTING MB:1` (±0.1) |
| `flaps_pct` | 0–100 | `FLAPS_SET` | `FLAPS HANDLE PERCENT` (±2) |
| `elevator_trim_pct` | −100–100 | `ELEVATOR_TRIM_SET` | `ELEVATOR TRIM PCT` (±2) |
| `gear_down`, `parking_brake` | bool | `GEAR_DOWN` / `GEAR_UP`, `PARKING_BRAKE_SET` | `GEAR HANDLE POSITION`, `BRAKE PARKING POSITION` |

How a command is carried out:

- **Explicit-state events only**, never toggles such as `AP_MASTER`, `AP_HDG_HOLD`,
  `GEAR_TOGGLE` or the radio swap (RB-SIM-MSF-090, D15). A test enforces this.
- **Level semantics.** A command is the desired value; a newer one replaces it. Nothing
  is written when the read-back already matches (status `confirmed`, "already set").
- **At most 2 writes per second per control** (protocol §5).
- **Read-back.** Each write is `sent`, then `confirmed` when the read-back matches within
  the tolerance. After 2 s without a match it becomes `mismatch`, with the read-back value
  (MSF-100), and it is never retried. Aircraft with their own systems may ignore standard
  events; `mismatch` is how that shows up. A control the sim doesn't know
  (`NAME_UNRECOGNIZED`) becomes `unsupported`; the other controls keep working.
- **Who may write.** Reading stays open to the LAN. Writing is narrower:
  - this PC only, unless `control.allow_remote` or `--control-remote` is set;
  - off entirely with `control.enabled: false` or `--no-control`;
  - limited to the groups listed in `control.groups` (`radio`, `autopilot`,
    `altimeter`, `controls`);
  - refused from a browser page of another origin (403), and refused without
    `application/json` (415), so a web page open on the same PC cannot post to localhost.
- **The sim altimeter** (`qnh_hpa`) sets only the simulator. It never moves an RB knob
  (protocol §2.2).

This is the manual control path for the instructor page, tools and tests. Coupling RB-01's
autopilot intents to the sim (protocol §5, plan T5.1, coupling `off|bug|full`) will feed
the same actuator layer; it isn't done yet.

The synthetic source emulates a cockpit, so the whole API works without MSFS. Its flaps
snap to 25 % detents, so `flaps_pct: 30` ends in `mismatch` on purpose; the smoke test
checks that.

## Safety behaviour

- A stale group is sent as `null` and never as its last value. The timeouts are those of
  protocol §2.3. Disconnection makes every group `null`.
- A pause freezes the values instead of failing. `Pause_EX1` reports a pause, or
  `SIMULATION TIME` stops while frames still arrive. Groups stay valid and `paused` is
  `true` (MOD-040, MSF-050).
- If SimConnect rejects a datum, that datum is dropped from the definition and only its
  field becomes invalid (MSF-130). A data block whose size doesn't match the definition
  is discarded, never decoded into shifted fields.

## Verified on this PC (2026-10-28, Windows 11, MSFS 2020 Store 1.39.12 running)

- 102 unit tests pass, and the 36 smoke checks of `ci/smoke.ps1` pass on the published
  exe under Windows PowerShell 5.1.
- The control API on the synthetic source: radio swap, squawk, autopilot, QNH and gear
  writes are confirmed by read-back; an ignored write ends as `mismatch`; bad values (400),
  a wrong content type (415) and a foreign origin (403) are refused. The page renders the
  radio and control panels (headless Edge).
- With the synthetic source, all endpoints, the page and the WebSocket work: about 20
  state messages/s at `rate=20`, plus traffic at 1 Hz.
- The MSFS source **without the SDK installed** sends all four required groups as `null`,
  and `/api/status` names the missing DLL and the paths it tried.

## Verified on the Linux dev box (2026-10-28, x86-64, .NET SDK 10.0.403, PowerShell 7.5.4)

- **Checks.** The 128 unit tests pass and the format check is clean.
- **Smoke test.** All 56 checks of `ci/smoke.ps1` pass on the self-contained single-file
  `linux-x64` binary, with no .NET installed for it. That includes the `/RB-01` checks,
  the HMI sockets, the HMI's REST calls and the addons bridge, and the Linux-only refusal
  of `--source msfs`.
- **Real tty.** A serial output on a pseudo-terminal delivers `$RBATT`, `$RBADC`,
  `$GPRMC` and `$GPGGA` (profile `rb02`). Without `libSystem.IO.Ports.Native.so`, the
  output reports the error and the wrapper keeps running.
- **Cross-builds from Linux.** `win-x64` gives a PE32+ x86-64 exe and `linux-arm64` an
  aarch64 ELF with its own `.so`. `win-arm64` is refused (NETSDK1032).
- **The CI package step** for linux-x64, run locally, gives a tar.gz with the executable
  bit kept and a checksum that verifies.
- **The RB-01 HMI** from `/home/stefano/roastbeef/web/RB-01` loads at `/RB-01/` in
  headless Chrome, together with the shared files from `wwwroot/`. Its plates run on the
  wrapper's HMI sockets and REST answers, and it publishes its auto QNH on the bridge. The
  only console error left is the missing `/favicon.ico`.

Not verified:

- the changed `ci/smoke.ps1` under Windows PowerShell 5.1, and the new CI matrix on
  GitHub. Both run on the next push;
- `linux-arm64` on a Pi;
- a real RB device on a Linux serial port.

## Not verified yet: needs the MSFS SDK installed

Install it from MSFS: Developer Mode → Help → SDK Installer. Then check these, as test
items L1 and L2:

- the SimConnect connection itself, and the `SIMCONNECT_RECV_*` offsets this code assumes
  (pack(1) layout of SimConnect.h);
- `dwDefineCount` meaning "number of datums" in user frames. If it doesn't, every frame is
  dropped and `/api/status` shows `data block mismatch`;
- whether an unknown SimVar raises `NAME_UNRECOGNIZED` at `AddToDataDefinition`, which
  the rebuild logic relies on;
- all **(verify)** signs in `MsfsMap.cs`: pitch, bank, turn rate, ball, MAGVAR, the
  world-velocity axes. These need the L2 captures;
- `ZULU TIME` without the `E:` prefix;
- application version major 11 = MSFS 2020, 12 = MSFS 2024;
- whether frames keep arriving during a full pause;
- the EMS units (`--ems-*`) against the target aircraft's `ems.json`;
- the ball full scale per aircraft (`--ball-full-scale-deg`, default 10°, a placeholder);
- the pseudo-ICAO range `0xF00000 | hash`, provisional until T3.1 picks it.
- Radio and control (test items L1 and L7):
  - whether the radio SimVars convert to `hertz` (the doc unit is Frequency BCD16);
  - the `TRANSPONDER CODE` read-back as `Bco16`;
  - whether `TRANSPONDER STATE` and `TRANSPONDER IDENT` exist on MSFS 2020;
  - the events not documented for MSFS 2020: `AP_VS_ON`, `AP_VS_OFF`,
    `PARKING_BRAKE_SET`. An unknown one should become `unsupported`, not fail the session;
  - whether `NAME_UNRECOGNIZED` comes at `MapClientEventToSimEvent` or only at transmit.
    Both are handled;
  - negative values (`AP_VS_VAR_SET_ENGLISH`, `ELEVATOR_TRIM_SET`) as two's-complement
    DWORDs, and the trim sign;
  - `HEADING_BUG_SET` and `AP_ALT_VAR_SET_ENGLISH` with the index parameter left at its
    default. `TransmitClientEvent_EX1` is not used yet;
  - the aircraft acceptance matrix of L7: which aircraft honour which events. Run it from
    the status page or `POST /api/control`, and record the `mismatch` and `unsupported`
    results.
