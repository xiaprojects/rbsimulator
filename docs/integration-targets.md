# Step 1 — Integration targets

Snapshot: **2026-10-26**. This page covers which simulators and software the bridge talks
to, how it talks to each one, and what each means for the three scenarios. External facts
were checked on the date above; the sources are at the end. Re-check licensing before
anything ships to a school.

## Summary

| # | Target | Interface used by the bridge | Platform | Data richness | Write-back (AP, pause, reposition) | Commercial / school use | Scenario | Priority |
|---|---|---|---|---|---|---|---|---|
| T1 | **Recorded RB flights** (roastbeef datalog SQLite): **the standalone RB Simulator** | `flightlog.py` from roastbeef `test/display` | any | full RB situation, traffic, EMS, bridge values, as flown | no (it is a replay; students operate the avionics, not the aircraft) | **ours** (AGPL + commercial); no third-party licence | **B** (product), dev, demo, debrief | **M0 + M1, first** |
| T2 | **Synthetic scenarios** | in-process generator | any | whatever the script sets | no | ours | tests | M0 |
| T3 | **MSFS 2020 / 2024** | SimConnect: the official `SimConnect.dll` in the wrapper's SDK build; the pure protocol (pipe or TCP) in the standalone builds ([wrapper.md](wrapper.md)) | Windows only | full: attitude, air data, engine, radios, AI traffic | yes: events and SimVar writes | consumer use only; not pursued for schools (D11) | **A** | **M2** (live checks in the test-machine session, D12) |
| T4 | **X-Plane 12** (11 partly) | Web API REST+WebSocket `:8086` (12.1.1+); legacy UDP `RREF`/`DREF`/`CMND` `:49000` as fallback | Windows, macOS, **Linux** | full; TCAS target datarefs for AI traffic | yes: dataref writes and commands (v2 API), flight init (v3) | **yes**: Professional Use Licence (USB key or digital) required for corporate, university or revenue use | C (an interactive option for schools later) | **M6** |
| T5 | **ForeFlight-UDP producers** (`XGPS`/`XATT`/`XTRAFFIC`, UDP `:49002`) | UDP listener | any | GPS, attitude (heading/pitch/roll), traffic only; no air data, no engine | no | depends on the producer | C | M6 |
| T6 | **FlightGear** | generic protocol: XML-defined UDP output; property tree over telnet/HTTP for write-back | Windows, macOS, Linux | full (any property) | yes, through property writes | **yes**: GPL, free for any use | C, budget B | M6 |
| T7 | **Prepar3D v5/v6** | SimConnect (same adapter family as T3) | Windows | full | yes | yes: Academic / Professional licences (verify current terms) | B, C | later |
| T8 | Others: DCS World (`Export.lua` UDP), Infinite Flight (Connect API), Aerofly FS native | per target | — | — | — | — | C | later, on demand |

**Order:** T1 and T2, then T3, T4, T5, T6, with T7 and T8 later.

- **T1 is the scenario B product** (decision D11), so it comes first. With T2 it also runs
  the whole RB chain on this Linux box with real flights before any third-party simulator
  exists. That covers the sinks, the ESP32 USB protocol and the RB-01 sim input.
- **T3** is scenario A. Its mapping is pure and tested on Linux. Its transport is verified
  in the separate test-machine session on Windows with MSFS (D12).
- **T4, T5 and T6** widen scenario C. X-Plane runs on Linux, so it can be verified live
  here once installed.

## T1 — Recorded RB flights: the standalone RB Simulator (scenario B)

- **What:** RB-01 logs `mySituation`, traffic, `ems`, `bridgefloat` and status to
  `/var/log/flightlog-*.sqlite` (roastbeef `main/datalog.go`). The roastbeef reader
  `test/display/flightlog.py` replays them as the daemon's own socket frames. The
  standalone simulator turns that into a **debrief tool for schools** (D13):
  - the school imports each training flight from **its own aircraft's** RB-01;
  - the instructor reviews it with the student: play, pause, seek and speed, checkpoints,
    events against the aircraft's limits, and bookmarks;
  - the same timeline drives RB-01 and the ESP32 devices together.
- **Why first:**
  - It is the scenario B product (D11).
  - The logs are real flights (take-off, turns, aerobatics, traffic, engine), so they are a
    realistic and deterministic input for every sink.
  - It runs entirely on this Linux box.
  - It also provides debrief for A and C: a recorded session replays through the same
    instruments.
- **Already in roastbeef, reused:**
  - `FlightLog.events()` merges the streams on the log clock.
  - `situation_at`, `traffic_at` and `_kv_state_before` rebuild the full state at any
    seek point.
  - `checkpoints()` derives take-off, climb, top, steepest turn, final and landed.
  - `extract` cuts a small test fixture out of a full flight.
  - `simulator.py` already plays a log into the HMI alone.
- **Limits, stated to the instructor and the student:**
  - It is not flyable. The flight path is the recorded one, and the student operates the
    avionics only.
  - A log holds outputs after fusion, not raw sensor inputs. Attitude is what RB displayed,
    not truth: on I-RVDX the internal AHRS read about 4° nose-low against the external unit.
  - It has no autopilot plan. One can be derived from the track (roastbeef
    `flightpath.waypoints()`).
  - It is recorded at 1 Hz, one instantaneous sample per second, so attitude looks stepped
    unless interpolated. Interpolation is for display smoothness only: in aerobatic
    figures 1 Hz aliases badly. R2 can merge the 20 Hz AHRS log (`sensors_*.csv`) when the
    aircraft recorded it.
  - Logs identify the aircraft and third-party traffic, and a mission identifies the
    student. They stay on the school's PC and are never committed. A flight is anonymised
    only if it leaves the school (RB-SIM-RPL-060, R2).
  - A flight ended by switching the master off keeps its data in the SQLite `-wal`, and
    must be consolidated on import (RB-SIM-RPL-100).

## T3 — Microsoft Flight Simulator 2020 / 2024

- **Interface:** SimConnect, the native C API.
  - Read variables with `SimConnect_AddToDataDefinition` plus
    `SimConnect_RequestDataOnSimObject` at period `SIM_FRAME` or `SECOND`, always passing
    **explicit units** (`"degrees"`, `"knots"`, `"feet"`, `"celsius"`…). MSFS defaults
    include radians, Rankine and psf.
  - Read AI and multiplayer traffic with `SimConnect_RequestDataOnSimObjectType` within a
    radius.
  - Write back with key events (`HEADING_BUG_SET`, `AP_HDG_HOLD_ON`, `PAUSE_SET`…) and
    `SimConnect_SetDataOnSimObject` for repositioning.
  - The exact SimVar and event names, units and settable flags are in
    [protocol.md](protocol.md) §3.1, **checked against the MSFS 2024 SDK docs on
    2026-10-26** (T2.1). What the docs don't state (several signs, the `E:` prefix for
    time, the DLL path) is settled live in the test-machine session.
- **Findings that shape the adapter** (details and sources in protocol §3.1):
  - "DEGREES" variables default to radians and temperatures to Rankine, so every request
    names its unit.
  - Pitch and bank signs are undocumented on the SimVar pages. Microsoft's avionics
    framework documents positive as nose-down and left. Confirm with captures.
  - There is no per-cylinder CHT/EGT and no coolant temperature, so cylinders are
    synthesised and coolant uses the radiator temperature.
  - Fuel-quantity and main-bus variables are legacy-only. Modern aircraft need tank and
    bus names in the aircraft profile.
  - Time is an environment variable (`ZULU TIME`). Early MSFS 2024 builds returned the PC
    clock instead (reported fixed in 1.2.7).
  - Pause: `PAUSE_SET` is undocumented, and the Esc pause, Active Pause and photo mode
    send no reliable events. MSFS 2024 omits `SimStop` on the main menu. So pause and
    disconnection are inferred from the data too.
  - Traffic by type: radius ≤ 200 km. Multiplayer objects are buggy; AI is reliable.
  - Autopilot `AP_MASTER` and `AP_HDG_HOLD` are toggles. We use `AUTOPILOT_ON/OFF` and
    `AP_HDG_HOLD_ON/OFF` only, and verify by read-back (D15).
  - Per the SDK, a client built with the 2020 SDK keeps working on 2024. Both sims are
    supported; L10 tests the DLL cross-compatibility.
- **Transport:** local named pipe is the default. For remote clients, the server enables
  IPv4 in `SimConnect.xml` (global scope; port 5111 is the usual example) and the client
  places a `SimConnect.cfg` next to the program. The MSFS 2024 docs call `SimConnect.xml`
  legacy but still valid. The SimConnect client library is **Windows-only**, so the bridge
  runs on the sim PC or on another Windows machine. RB devices sit on the LAN (RB-01) or
  USB (ESP32).
- **Binding (D14, D17):** the Go wrapper's SDK build binds `SimConnect.dll` at run time
  (`syscall`; no SDK needed to compile). The standalone builds use the pure protocol. The
  DLL itself ships with the MSFS SDK.
  - Redistributing the DLL is unconfirmed: two devsupport threads ask and have no
    Microsoft answer. So the user installs the SDK, as FSUIPC asks its users to do, and the
    wrapper finds the DLL (Q14). Users without the SDK take the standalone build.
  - Loading a proprietary DLL from AGPL code needs the copyright holder's decision on an
    additional permission (Q15).
  - R2 option: a pure-Python implementation of the SimConnect wire protocol over TCP or
    the named pipe. No DLL, and it runs on Linux, even the RB-01 Pi. node-simconnect
    (LGPL-3.0, TypeScript) shows it works on MSFS 2020/2024 and reports the protocol as
    forward compatible. It is unofficial, and how ours may be written is Q16.
  - Rejected: `python-simconnect`. Its licence and maintenance need checking, and we only
    need a small API surface.
  - Rejected: FSUIPC7. It is paid, Windows-only, and an extra install.
- **Known risk:** a DevSupport thread reports `SimConnect_Open` returning `E_FAIL` on MSFS
  2024 1.7.35.0, with the SDK samples too. It is user-reported and unresolved in the search
  results, so check the current sim version before debugging our own code.
- **Licensing:** Microsoft's own commercial-use terms did not turn up. Forum posts say no
  commercial licence is offered; third-party add-on EULAs require separate commercial
  licences. Boeing's 2025 training product is built on MSFS technology but is a Boeing
  product.
- **Resolved:** MSFS is scenario A (personal use) only. Schools use the standalone replay
  (D11), so the question is closed.

## T4 — X-Plane 12

- **Web API** (preferred; REST and WebSocket on one TCP port, default `8086`, changed with
  `--web_server_port=`):
  - v1 (12.1.1+): datarefs. List them, read or write by id, and subscribe or stream over
    WebSocket at `ws://host:8086/api/v1`.
  - v2 (12.1.4+): commands. Activate them and stream activations.
  - v3 (12.4.0+): initialise or update a flight, used for IOS reposition and scenario
    start.
  - REST needs `Accept: application/json`. Writes are `PATCH /api/v1/datarefs/{id}/value`.
    Dataref ids are resolved by name at start-up.
  - Incoming traffic can be disabled in X-Plane's Network settings, so the bridge must
    detect that and report it.
- **Legacy UDP** (X-Plane 11 and 12 without the Web API): `RREF` subscriptions to `:49000`,
  `DREF` writes and `CMND` commands. Keep it as the fallback transport behind the same
  mapping.
- **Built-in ForeFlight output** (`XGPS`/`XATT`/`XTRAFFIC`, UDP `:49002`): zero
  configuration but limited data. It is handled by the T5 adapter.
- **AI traffic:** TCAS target datarefs (`sim/cockpit2/tcas/targets/...`, X-Plane 11.50+);
  verify in T6.1.
- **Licensing:** the Professional Use Licence is required for corporate, university or
  revenue-generating use. It also enables the frame-rate and hardware checks needed for FAA
  device approval. FAA credit still depends on the **whole** device being approved, not on
  the software.

## T5 — ForeFlight-UDP producers

- **Format:** ASCII datagrams on UDP `:49002`, one message per packet:
  - `XGPS<name>,lon,lat,alt_msl_m,track_true,gs_mps`
  - `XATT<name>,hdg_true,pitch,roll[,…]`
  - `XTRAFFIC<name>,icao,lat,lon,alt_ft,vs_fpm,airborne,track,gs_kt,callsign`
- **Reference implementation:** roastbeef `main/xplane.go` **produces** this format (it is
  an output of RB-01, not an input). Use it as the field-order reference and as a test
  oracle.
- **Producers:** X-Plane natively. Aerofly FS (verify its current output option). Several
  MSFS add-on bridges.
- **Limits:** no IAS, no baro, no slip, no G, no engine data. Instruments that need them
  show INOP. This adapter gives GPS, attitude and traffic level only, and has no
  write-back.

## T6 — FlightGear

- **Interface:** the generic protocol. We ship `rbsim/sources/flightgear/rbsim.xml` listing
  properties such as `/orientation/pitch-deg`, `/orientation/roll-deg`,
  `/velocities/airspeed-kt` and `/engines/engine[0]/rpm`. The user starts FlightGear with
  `--generic=socket,out,20,<bridge-ip>,<port>,udp,rbsim`.
- **Write-back:** the property server (`--telnet=` or `--httpd=`) for heading bug, pause and
  reposition.
- **Licence:** GPL. Free for any use, which makes it a zero-licence-cost option for schools.

## T7 / T8 — later

- **Prepar3D:** reuses the T3 SimConnect adapter with a P3D SDK DLL and small name
  differences. Its licences are explicitly for training (Academic / Professional); verify
  the current terms.
- **DCS, Infinite Flight, Aerofly native, GeoFS:** only on demand. The open protocol
  ([protocol.md](protocol.md)) lets third parties write their own adapter.

## Middleware considered and not used

The full make-or-buy comparison of free and open-source connectors (MobiFlight, node-simconnect,
fs2ff, the Go bindings and others) is in [make-or-buy.md](make-or-buy.md) (decision D18).

| Tool | Why not |
|---|---|
| FSUIPC7 | paid, Windows-only, a licence per seat for schools; it wraps SimConnect, which we call directly |
| MobiFlight / SPAD.neXt / Air Manager | cockpit-hardware and instrument platforms. They overlap with what RB devices are, and depending on them adds paid or closed layers. MobiFlight could later sit *next to* RB for switches and knobs |
| python-simconnect, node-simconnect | third-party wrappers; licence and maintenance risk for a small API surface |

## Regulatory context for scenario B

- **FAA:** time counts toward a certificate only on an approved Aviation Training Device:
  - BATD or AATD, under 14 CFR 61.4(c);
  - guidance in AC 61-136B (the FAA PDF is dated 9/12/18; 2024–2025 approval letters cite
    it as "AC 61-136");
  - approval covers the complete device: software, computer, controls and documentation.
- **EASA:** CS-FSTD(A) qualifies FFS, FTD, FNPT and BITD devices (Issue 2, 2018; check
  whether a later issue applies). Again the whole device is qualified, not the software.
- **Consequence:** v1 is positioned as a **non-credit avionics familiarisation and
  procedures trainer** (decision D7). The standalone replay has no flight controls, so it
  is not a flight training device in any regulatory class. The design keeps a later qualification path open,
  together with a device manufacturer:
  - a configuration ID shown on screen;
  - deterministic session records;
  - an instructor station with freeze, reposition and failures;
  - measured end-to-end latency.

## Sources (checked 2026-10-26)

- MSFS SimConnect remote setup: [SimConnect.xml (MSFS 2024 docs)](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/simconnect/simconnect-xml-definition/index.md), [SimConnect.cfg (MSFS 2024 docs)](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/simconnect/simconnect-cfg-definition/index.md), [TFDi remote guide](https://docs.tfdidesign.com/smartcars/user-guide/plugins/flight-tracking/running-on-different-computers), [python-simconnect](https://github.com/odwdinc/python-simconnect), [E_FAIL report, MSFS 2024 1.7.35.0](https://devsupport.flightsimulator.com/t/simconnect-open-consistently-returns-e-fail-in-msfs-2024-1-7-35-0/18221)
- MSFS commercial use: [forum wishlist: commercial licence](https://forums.flightsimulator.com/t/allow-license-for-commercial-flight-simulator-providers/705097), [FSimStudios EULA](https://www.fsimstudios.com/eula), [Boeing training platform on MSFS tech](https://msfsaddons.com/2025/11/07/boeing-is-officially-using-microsoft-flight-simulator-as-a-training-platform/), [Sporty's on MSFS 2024 for pilots](https://www.sportys.com/blog/five-things-pilots-value-in-msfs-2024-and-one-we-don-t/)
- X-Plane: [Web API docs](https://developer.x-plane.com/docs/web-apis/), [Web API v2 announcement](https://developer.x-plane.com/2025/01/sdk-update-web-api-v2/), [What licence do I need?](https://x-plane.helpscoutdocs.com/article/25-what-license-do-i-need), [FAA certification FAQ](https://x-plane.helpscoutdocs.com/article/31-faa-certification)
- FAA: [AC 61-136B](https://www.faa.gov/documentLibrary/media/Advisory_Circular/AC_61-136B.pdf), [Redbird: the A to Z of ATDs](https://www.redbirdflight.com/landing/the-a-to-z-of-atds)
- EASA: [CS-FSTD(A)](https://www.easa.europa.eu/en/downloads/1735/en)
