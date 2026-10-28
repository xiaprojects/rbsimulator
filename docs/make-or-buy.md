# Make or buy: simulator connectors

Snapshot: **2026-10-28**. This page asks whether the wrapper ([wrapper.md](wrapper.md)) should
be written by us ("make") or replaced, in part or whole, by an existing free and open-source
connector ("buy", meaning adopt). MobiFlight Connector is the reference point. Repository
facts (licence, language, last push) come from the GitHub API on the date above. Treat
anything marked `(verify)` as unchecked.

## What a connector has to do for RB

The wrapper's job is set by D16 and [protocol.md](protocol.md). A connector replaces it
only if it does all of this:

| # | Need | Source |
|---|---|---|
| N1 | Stream a **whole canonical state** (attitude, air data, GPS, engine, radios) at 10–20 Hz, plus traffic at 1 Hz | protocol §4.1 |
| N2 | Speak **RB-SIM-IF/1** to a hub (RB-01 or hub-lite), not to individual displays | D16 |
| N3 | **Staleness and validity**: a missing or stale value must reach the hub as invalid, never as the last value | ground rule 2 |
| N4 | **Write-back with read-back**: heading bug, HDG, AP on/off, using explicit-state events only | D15 |
| N5 | **Several simulators** behind one mapping: MSFS, X-Plane, FlightGear, ForeFlight-UDP | integration-targets |
| N6 | **Linux and the RB-01 Pi** as well as Windows, cross-compiled from Linux | D17 |
| N7 | **Licence**: code we take in must allow the AGPL **and** the commercial edition | D9 |
| N8 | A single binary with no extra runtime for consumers | D17 |

None of the connectors below does N1–N3 the way RB needs, because none of them knows the RB
contract. So the realistic options are not "buy instead of make". They are
**make the core, and buy the parts that are expensive or that users already have.**

## Candidates

Legend: ✓ meets it · ~ partly, or with work · ✗ no · — not applicable.

| Candidate | What it is | Licence | Lang | Last push | Sims | N1 full state | N4 write-back | N6 Linux/Pi | N7 licence fits AGPL + commercial | Role for RB |
|---|---|---|---|---|---|---|---|---|---|---|
| **[MobiFlight Connector](https://github.com/MobiFlight/MobiFlight-Connector)** | Home-cockpit hub: maps sim variables to LEDs, 7-segment displays, LCDs, steppers and "custom devices" on Arduino-class boards over USB serial | MIT | C# (.NET) | 2026-10-27, v11.3.1 | MSFS, X-Plane, P3D, FSX | ~ per-output configs that the user builds, no traffic | ✓ events, inputs | ✗ Windows only | ✓ | **Coexist** for switches and knobs. A possible R2 "MobiFlight custom device" mode for RB-02 builders (below) |
| **[MobiFlight WASM Module](https://github.com/MobiFlight/MobiFlight-WASM-Module)** | MSFS in-sim module that exposes LVars and calculator code to out-of-process clients through SimConnect client data | MIT | C++ | 2024-03-15 | MSFS | — adds LVars | ✓ H-events, calculator code | — runs inside MSFS | ✓ (we only talk to it) | **Buy (R2)**: optional runtime dependency for aircraft whose AP or engine live in LVars |
| [MobiFlight firmware](https://github.com/MobiFlight/MobiFlight-FirmwareSource) | Board firmware for the Connector (PlatformIO); custom boards and custom devices | MIT | C++ | 2026-09-03 | — | — | — | — | ✓ | Reference, only if the R2 MobiFlight mode is built |
| **[node-simconnect](https://github.com/EvenAR/node-simconnect)** | Pure SimConnect wire protocol over TCP or named pipe, no DLL; MSFS 2020 and 2024 | LGPL-3.0 | TypeScript | 2026-10-03 | MSFS | ✓ (raw SimConnect) | ✓ | ✓ needs Node | ~ not copyable into the commercial edition. A separate unmodified process is fine | Already **Q16**: reference, or a sidecar. Breaks N8 if it is a sidecar |
| [fs2ff](https://github.com/astenlund/fs2ff) | MSFS → ForeFlight UDP (`XGPS`/`XATT`/`XTRAFFIC`) for EFBs | Unlicense | C# | 2024-04-11 | MSFS 2020, P3D 4+ | ✗ GPS, attitude, traffic only | ✗ | ✗ | ✓ | **Buy by composition**: the user runs it, our T5 ForeFlight-UDP adapter listens. MSFS 2024 support `(verify)` |
| [Python-SimConnect](https://github.com/odwdinc/Python-SimConnect) | Python wrapper over `SimConnect.dll` | AGPL-3.0 | Python | 2023-02-13 | MSFS 2020 | ✓ | ✓ | ✗ needs the DLL | ✗ AGPL code can't go into the commercial edition | Rejected (as before) |
| [pysimconnect](https://github.com/patricksurry/pysimconnect) | Python wrapper over `SimConnect.dll` | MIT | Python | 2023-08-13 | MSFS 2020 | ✓ | ✓ | ✗ | ✓ | Not needed: the wrapper is Go (D17) |
| [lian/msfs2020-go](https://github.com/lian/msfs2020-go) | Go bindings for `SimConnect.dll` through `syscall` | MIT | Go | 2021-03-06 | MSFS 2020 | ✓ | ✓ | ✗ needs the DLL | ✓ | **Reference** for T2.3. Stale, so don't depend on it |
| [Zwergpro/simconnect-go](https://github.com/Zwergpro/simconnect-go), [bmurray/simconnect-go](https://github.com/bmurray/simconnect-go) | Go bindings for the 2024 SDK | MIT | Go | 2026-05, 2025-12 | MSFS 2024 | ✓ | ✓ | ✗ | ✓ | **Reference** only. Maturity is negligible (0 and 1 stars) |
| [Little Navconnect](https://github.com/albar965/littlenavconnect) | Agent that serves sim position and AI traffic to Little Navmap over TCP | GPL-3.0 | C++ (Qt) | 2026-10-23 | FSX, P3D, MSFS 2020/2024, X-Plane | ✗ navigation data, no engine | ✗ | ~ | ✗ GPL | Rejected |
| [NASA XPlaneConnect](https://github.com/nasa/XPlaneConnect) | X-Plane plugin plus UDP client libraries | NOSA 1.3 | C | 2024-04-23 | X-Plane | ✓ datarefs | ✓ | ✓ | ✗ NOSA is not GPL-compatible (FSF) | Rejected: the X-Plane 12 Web API is built in |
| [ExtPlane](https://github.com/vranki/ExtPlane) | X-Plane plugin, text TCP protocol for datarefs | GPL-3.0 | C++ | 2024-01-11 | X-Plane | ✓ | ✓ | ✓ | ✗ GPL | Rejected: same reason |
| [YourControls](https://github.com/Sequal32/yourcontrols) | MSFS shared cockpit (variable sync) | GPL-3.0 | Rust | 2026-04-12 | MSFS | ✓ | ✓ | ✗ | ✗ GPL | Rejected. Its per-aircraft variable lists are a useful catalogue to read |
| FSUIPC7 + FSUIPC WebSocket Server | Offsets interface plus a WebSocket gateway | proprietary (WebSocket Server freeware; licence `(verify)`) | — | — | MSFS, P3D | ✓ | ✓ | ✗ | ✗ closed, an extra install | Rejected (as before) |
| Built-in: X-Plane 12 Web API, FlightGear generic protocol, X-Plane ForeFlight output | First-party interfaces | — | — | — | X-Plane, FlightGear | ✓ | ✓ | ✓ | ✓ | **Use directly**: no connector needed |

Paid platforms (SPAD.neXt, Air Manager, Axis and Ohs) fall outside "free and open source"
and are already rejected in [integration-targets.md](integration-targets.md).

## MobiFlight in detail

MobiFlight is the closest product to RB's "sim drives real instruments" story, so it is
worth being precise about why it doesn't replace the wrapper.

- **Its model is output-by-output.** A user configures each value (one SimVar → one
  display or LED) in a profile. RB needs one validated state message per frame, which
  RB-01 then fans out to the HMI, the autopilot, the alerts, the datalog, GDL90 and the ESP32s.
  Using MobiFlight to feed RB-01 would mean a profile of about 40 outputs with no units
  contract and no validity flags (N1, N3).
- **It talks to USB boards, not to a LAN hub.** RB-01 is a Pi on the network. Feeding it
  from MobiFlight would bypass D16: each ESP32 would be driven directly, so RB-01's
  autopilot, alerts and traffic would see nothing.
- **It has no traffic path.** AI and multiplayer traffic, which RB-01 turns into `$PFLAA` and
  GDL90, isn't one of its output types `(verify)`.
- **It is Windows-only .NET.** That fails N6 (the Pi and Linux builds) and N8.
- **Its licence is fine.** MIT allows reuse in both editions, with the notice kept.

Where MobiFlight **does** fit:

1. **Coexistence (today, no work).** A builder's MobiFlight panel handles switches, knobs
   and annunciators. RB instruments run from the wrapper. Both are SimConnect clients of
   the same MSFS, so they don't interfere.
2. **The WASM module (R2, small).** For aircraft whose autopilot or engine state lives in
   LVars, the wrapper's MSFS adapter reads them through the MobiFlight WASM client data
   area. Users who have MobiFlight already have it installed. It is optional: without it,
   the adapter uses standard SimVars and reports what it cannot read.
3. **RB-02 as a MobiFlight custom device (R2, evaluate only).** RB-02 firmware could
   answer MobiFlight's board protocol in a SIM build, so MobiFlight users can drive one
   round instrument with no RB-01. Costs and open points:
   - It is a second sim input path in rb-esp, so it must live under `RB_ENABLE_SIM` with
     the SIM annunciation and the timeout to INOP (ground rules 1 and 2). MobiFlight
     doesn't tell a device that the sim has stopped.
   - ESP32-S3 support in the MobiFlight firmware and the serial protocol details are
     `(verify)` against the firmware source.
   - It serves builders who don't own an RB-01. That is a product question for the
     maintainer, not a technical gap.

## Make versus buy, by component

| Component | Decision | Why |
|---|---|---|
| Canonical state, failure layer, contract client, staleness, recorder | **Make** | No connector knows RB-SIM-IF/1 or RB's validity rules. This is the core and the product |
| MSFS mapping (`internal/sources/msfs/map.go`) | **Make** | Pure, tested on Linux, already verified against the SDK docs (T2.1). Read YourControls' and MobiFlight's variable lists for coverage |
| MSFS transport, SDK build (DLL via `syscall`) | **Make**, with the MIT Go bindings as a reference | About ten SimConnect functions. A dependency would break "standard library only" for little gain, and the existing bindings are stale or unproven |
| MSFS transport, standalone (pure protocol) | **Q16** | Clean-room from captures, node-simconnect as a reference, or an unmodified node-simconnect sidecar. The sidecar is the only real "buy", and it costs N8 |
| MSFS LVars / complex aircraft | **Buy (R2)**: MobiFlight WASM module, optional | MIT, widely installed, and writing our own WASM module means a WASM toolchain and MSFS packaging we can't test here |
| X-Plane | **Make**, on the built-in Web API (legacy UDP fallback) | First-party, works on Linux. Go's standard library has no WebSocket client, so either write a minimal one or use REST polling and UDP `RREF` `(decide in T6.2)` |
| FlightGear | **Make** (an XML file and a UDP listener) | The generic protocol is built in |
| ForeFlight-UDP | **Make** the listener, **buy** the producers | fs2ff (MSFS), X-Plane and others already emit it. It gives a zero-install degraded path: no air data and no engine, so those instruments show INOP |
| Hardware panels (switches, knobs) | **Buy**: MobiFlight, side by side | Out of RB's scope. Already the position in integration-targets |
| RB-02 driven directly by MobiFlight | **Evaluate (R2), STOP** | It touches rb-esp and the product scope |

## What this changes

- No change to the plan's critical path (M0, M1, M2). The wrapper stays a make.
- New R2 items: the MobiFlight WASM module reader (MSFS adapter), and an evaluation of
  RB-02 as a MobiFlight custom device. Both are recorded as proposed in decision D18.
- integration-targets' "Middleware considered and not used" points here for the detail.

## Not verified

- MobiFlight's board serial protocol, ESP32-S3 support, and whether the Connector has any
  traffic output or external API: not checked against the firmware or connector source.
- fs2ff on MSFS 2024: not tested. Its last push predates MSFS 2024's November 2024 release.
- The FSUIPC WebSocket Server licence.
- The maintenance figures are a snapshot of last-push dates and star counts. They aren't an audit of
  each project's health.
