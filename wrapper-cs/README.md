# rbsim-msfs: C# MSFS wrapper (decision D19)

Reads the user aircraft and traffic from Microsoft Flight Simulator 2020 / 2024 through
SimConnect. It serves the avionics status over HTTP and WebSocket, in the RB-SIM-IF/1
message shapes of [../docs/protocol.md](../docs/protocol.md) §4.1: RB-01 field names and
units, and `null` for an invalid group.

Later outputs go through the same `SimState`: a direct USB serial link to RB-02 (GPS NMEA
plus the RB sentences of protocol §4.4), and POSTing to an RB-01 hub.

## Layout

| Path | What |
|---|---|
| `src/RbSim.Core/` | pure, no I/O: `SimState` (SI), `Units`, `MsfsMap` (SimVar → SimState, signs), `MsfsDetectors` (pause, link, teleport), `DataLayout` (SimConnect block decoding), `RbSimIf` (contract messages), `Nmea`/`DeviceLines` (serial sentences), `Actuators`/`ActuatorManager` (writable controls, key events, read-back) |
| `src/RbSim.Msfs/` | the `rbsim-msfs.exe` program: P/Invoke to `SimConnect.dll` (`SimConnectNative`, `MsfsSource`), `SyntheticSource`, the Kestrel HTTP/WebSocket server (`Program.cs`), the live page `wwwroot/index.html` |
| `tests/RbSim.Core.Tests/` | xunit tests on the core, tagged `// Covers: RB-SIM-…` |

SimConnect is called through P/Invoke on the **native** `SimConnect.dll`, not through the
managed `Microsoft.FlightSimulator.SimConnect.dll`:

- the build needs no SDK;
- the DLL is the one the user installed, found at run time in the protocol §3.1 order
  (`--simconnect-dll`, `%MSFS2024_SDK%`, `%MSFS_SDK%`, `C:\MSFS 2024 SDK`, `C:\MSFS SDK`);
- the DLL is never bundled or committed (Q14).

## Build, test, run

Needs the .NET 10 SDK, Windows x64.

```powershell
cd wrapper-cs
dotnet test RbSim.Wrapper.slnx
dotnet run --project src/RbSim.Msfs -- --source synthetic     # no simulator needed
dotnet run --project src/RbSim.Msfs                           # MSFS through SimConnect
dotnet publish src/RbSim.Msfs -c Release -o out               # out\rbsim-msfs.exe
```

The page is at http://localhost:8090/. `--help` lists the options. The default bind is
`0.0.0.0:8090`, so devices on the LAN can poll it. Windows asks once for a firewall rule:
allow it on **private** networks only. Use `--listen 127.0.0.1` to keep the wrapper local.

| Endpoint | Content |
|---|---|
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
| `test` | Ubuntu + Windows | `dotnet format --verify-no-changes` (Linux), Release build of the whole solution (it cross-builds the win-x64 exe on Linux), unit tests |
| `package` | Windows | self-contained single-file `rbsim-msfs.exe` (no .NET install needed, about 50 MB); fails if any `SimConnect*.dll` is in the output; runs [ci/smoke.ps1](ci/smoke.ps1) against the exe; uploads `rbsim-msfs-<version>-win-x64.zip` + `.sha256` as the `rbsim-msfs-win-x64` artifact |
| `release` | Ubuntu | only for a tag `wrapper-cs-v<semver>`: creates the GitHub release with the zip and checksum. A version containing `-` is marked pre-release |

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

Line endings are LF on every OS (`.gitattributes`, `.editorconfig`), so the format check
gives the same answer here and on the Linux runner.

## Configuration file and USB serial output

Copy [rbsim-msfs.example.json](rbsim-msfs.example.json) to `rbsim-msfs.json`, in the
current folder or beside the executable, or pass `--config PATH`. Command-line options
override the file. Comments and trailing commas are allowed. An unknown key is an error, so
a misspelt setting is never silently ignored.

Each entry in `serial` feeds one directly connected RB device with the protocol §4.4
device lines: GPS NMEA, FLARM, and the RB sentences. `rbsim-msfs --list-ports` lists the
COM ports.

| Profile | Lines | Works with |
|---|---|---|
| `nmea-flarm` | `$GPRMC`+`$GPGGA` 5 Hz, `$PFLAU`+`$PFLAA` 1 Hz | today's USB firmware: `RB_ENABLE_USB_NMEA` (RB-02/RB-07), `RB_ENABLE_USB_FLARM` (RB-05/RB-07) |
| `rb02` | `$RBATT` 20 Hz, `$RBADC` 10 Hz, `$GPRMC`+`$GPGGA` 5 Hz | an `RB_ENABLE_SIM` firmware build (T4.2, not built yet) |
| `rb04` | `$RBEMS` 5 Hz, `$RBCYL` every 2 s | RB-04 EMS-USB (`RB_ENABLE_EMS`) |
| `rb05` | `$GPRMC`, `$PFLAA`, `$PFLAU` 1 Hz | RB-05 WIRED-USB |
| `rb07` | `rb02` + `rb05` | RB-07 USB, SIM build for the attitude and air data |

- `rates` overrides one sentence rate in Hz. Setting a rate to 0 removes that sentence.
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
- Whether the ESP32-S3 USB console needs DTR asserted. Both control lines default off
  because of the auto-reset circuit.
- Behaviour on a real device: no RB device is connected to this PC.

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
