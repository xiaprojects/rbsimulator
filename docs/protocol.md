# Step 3a — RB-SIM-IF v1: protocol and requirements

Status: **draft 2, 2026-10-26.** The MSFS mapping (§3.1) has been checked against the
SDK docs. Two kinds of marker remain:

- **`(verify)`**: a unit, sign or name not yet checked against the simulator SDK or the RB
  source.
- **L1 / L2 / L7 / L10**: something the docs leave open, settled live in the
  test-machine session ([plan.md](plan.md) §"Test-machine session").

[plan.md](plan.md) names the task that checks each one. Don't implement a marked item
without checking it first.

## 1. Architecture and layers (decision D16)

```
 Simulator ⇄ SDK ⇄ WRAPPER (sender)          RB-SIM-IF/1 over HTTP            HUB                     devices
                   rbsim sources:           POST /sim/state    10–20 Hz ──▶  RB-01, stratuxrun-sim ─┬─▶ HMI, alerts, autopilot, datalog
                   msfs, xplane, replay,    POST /sim/traffic   1 Hz   ──▶  (or hub-lite on a PC)   ├─▶ GDL90 / FLARM to EFBs
                   or any third party       ◀── response: autopilot intents ─┘                       └─▶ RB-02/04/05/07 over
                                            GET  /sim/status                                              serial/USB/BLE (NMEA)
```

- **The wrapper** (sender) owns everything simulator-specific: SDK, names, units, signs,
  reconnects and the read-back of what it writes. Inside the Python bridge, the L1 sources
  produce the canonical SimState (§2). The failure layer (§6) and recorder act on it. The
  **contract client** then turns it into RB-SIM-IF messages. Third parties can write their
  own wrapper against §4 alone.
- **RB-SIM-IF/1 is the contract**: three HTTP endpoints carrying JSON in RB-01's own field
  names and units (§4.1). **The message is the contract and the transport is replaceable**.
  If HTTP ever becomes the bottleneck, a WebSocket or UDP carrying the same messages can be
  added without changing them (RB-SIM-IF-090).
- **The hub** receives the contract and drives the RB devices through their existing paths.
  - **RB-01**, in the `stratuxrun-sim` build only (§4.2): it fans the contract out to its
    real internal paths, and re-emits to the ESP32 devices on its existing NMEA outputs.
  - **hub-lite**, the Python bridge on a PC, for setups without RB-01 hardware (§4.3): it
    serves the RB-01 HMI and drives the ESP32 devices over USB.

  A wrapper cannot tell the two hubs apart.
- **Return channel**: RB-01's autopilot intents travel in the HTTP response to each state
  POST (§5). There is no second connection.
- **Device lines** to the ESP32 devices are NMEA sentences (§4.4), produced by RB-01 or by
  hub-lite. Two are new and compile-time gated on both ends: `$RBATT` and `$RBADC`.
- **Versioning:** the protocol identifier is `RB-SIM-IF/1` (`"v": 1` in every message).
  Within v1, changes are additive only: new optional fields, and new sentences under new
  names. Removing a field or changing a unit needs `v: 2`.

**Parallel development.** The teams meet only at the contract, each with the test kit
(RB-SIM-IF-100):

- **Reference sender:** the flight-log replay, sending real flights as contract messages.
  The RB-01 and RB-02 teams need no simulator.
- **Reference receiver and validator:** `python -m rbsim check` listens like a hub, checks
  fields, units, rates and timeouts, and returns scripted autopilot intents. Wrapper teams
  need no RB-01.
- **Golden messages** in `tests/fixtures/contract/`, used by both sides' tests.

## 2. Canonical SimState (L2)

### 2.1 Conventions

- Units are SI and angles are in degrees. Temperatures are °C, pressures Pa, fuel volume L
  and fuel flow L/h. Every conversion lives in `rbsim/units.py`.
- **Attitude:**
  - pitch is **+ nose up**;
  - roll is **+ right wing down** (the same as roastbeef `/cageAHRS`);
  - turn rate is **+ right**;
  - slip is the ball deflection in degrees, **+ ball right**. Check this against RB-01
    `AHRSSlipSkid` in T3.1 (verify).
- Headings and tracks are 0 ≤ x < 360. They are **true** unless the name says `mag`.
  Magnetic variation is **+ east** (mag = true − var).
- Position is WGS84 in decimal degrees, + north and + east. `alt_msl_m` is geometric
  altitude above MSL, and `pressure_alt_m` is referenced to 1013.25 hPa.
- Time is UTC from the simulator clock, not the PC clock. `t_mono` is the bridge's
  monotonic receive time in seconds.

### 2.2 Fields

| Group | Field | Unit | Notes |
|---|---|---|---|
| `sim` | `source` | str | `replay`, `msfs`, `xplane`, `ffudp`, `flightgear`, `synthetic` |
| | `connected`, `paused`, `on_ground` | bool | |
| | `rb_raw` | dict | replay only: the original roastbeef frames by socket path (§3.5) |
| | `utc` | datetime | sim Zulu |
| | `teleport_seq` | int | +1 on every slew, reposition or flight load (RB-SIM-MOD-050) |
| | `aircraft` | str | sim aircraft title; selects the aircraft profile |
| `pos` | `lat_deg`, `lon_deg` | deg | |
| | `alt_msl_m`, `geoid_sep_m` | m | `geoid_sep_m` is optional; NMEA GGA carries it |
| | `track_true_deg` | deg | ground track |
| | `gs_mps` | m/s | ground speed |
| | `vs_gps_mps` | m/s | + up |
| | `magvar_deg` | deg | + east |
| `att` | `pitch_deg`, `roll_deg` | deg | §2.1 signs |
| | `hdg_true_deg`, `hdg_mag_deg` | deg | |
| | `turn_rate_dps` | deg/s | + right |
| | `slip_deg` | deg | ball, + right |
| | `g_load` | — | normal load factor, 1.0 in level flight |
| `air` | `ias_mps`, `tas_mps` | m/s | |
| | `pressure_alt_m` | m | 1013.25 hPa reference |
| | `static_pa` | Pa | ambient static pressure |
| | `vs_baro_mps` | m/s | + up |
| | `oat_c` | °C | |
| | `sim_qnh_pa` | Pa | the sim's own altimeter setting; information only, never pushed to RB knobs |
| `wind` | `from_true_deg`, `speed_mps` | | **test oracle only**; RB devices compute their own wind |
| `eng[i]` | `rpm` | 1/min | |
| | `map_pa`, `oil_press_pa`, `fuel_press_pa` | Pa | |
| | `oil_temp_c`, `coolant_temp_c` | °C | |
| | `cht_c[4]`, `egt_c[4]` | °C | synthesised per cylinder from one sim value plus the profile spread (RB-SIM-MOD-060) |
| | `fuel_flow_lph` | L/h | |
| `fuel` | `left_l`, `right_l` | L | |
| `elec` | `bus_v` | V | |
| `traffic[]` | `id24` | int | ICAO 24-bit if known, else a stable hash of the sim object id into a reserved range |
| | `callsign` | str | |
| | `lat_deg`, `lon_deg`, `alt_msl_m` | | |
| | `track_true_deg`, `gs_mps`, `vs_mps`, `on_ground`, `category` | | |
| `radio` (R2) | `com1_active_hz`, `com1_standby_hz`, `nav1_active_hz`, `nav1_radial_deg`, `nav1_valid` | | |
| `ap` | `master`, `hdg_bug_mag_deg`, `alt_sel_m`, `hdg_mode` | | the sim's own autopilot state, used as reverse-channel feedback |

### 2.3 Validity and staleness

- Every group carries `valid` and `t_mono`. A group is **stale** when `now − t_mono`
  exceeds its timeout:

  | Group | `att` | `air` | `pos` | `eng` | traffic target |
  |---|---|---|---|---|---|
  | Timeout | 0.5 s | 1.0 s | 2.0 s | 3.0 s | 10 s (then dropped) |

  Sinks send a stale group as **invalid**. They never resend the last value as if it were
  fresh.
- **Pause is freeze, not failure.** While `sim.paused`, the bridge keeps sending the last
  values with valid flags kept and fresh send times, so the instruments hold their
  indication (RB-SIM-MOD-040). Disconnection is a failure: everything becomes invalid.

## 3. Adapter mappings (L1)

### 3.1 MSFS SimConnect (checked against the MSFS 2024 SDK docs, 2026-10-26)

**Reading the tables:**

- "Doc unit" is the default the SDK page gives. The adapter always requests the **request
  unit**, and SimConnect converts (RB-SIM-ADP-020). Several "DEGREES" variables are
  radians by default, and temperatures are Rankine.
- **L1/L2/L7** marks a sign, scale or behaviour the docs don't state. The test-machine
  session settles it (plan §"Test-machine session"), and the mapping tests then run on
  those captures.
- Sources are at the end of this section.

**User aircraft** (always requested on `SIMCONNECT_OBJECT_ID_USER`)

| SimVar | SDK page | Doc unit | Request unit | → SimState | Status, note |
|---|---|---|---|---|---|
| `PLANE LATITUDE`, `PLANE LONGITUDE` | Aircraft Misc | radians | degrees | `pos.lat_deg`, `lon_deg` | doc: north and east positive |
| `PLANE ALTITUDE` | Aircraft Misc | feet | meters | `pos.alt_msl_m` | |
| `GROUND VELOCITY` | Aircraft Misc | knots | meters per second | `pos.gs_mps` | |
| `GPS GROUND TRUE TRACK` | Radio Navigation | radians | degrees | `pos.track_true_deg` | doc silent on whether the GPS must be powered: **L2** checks with avionics off. Fallback `atan2(VELOCITY WORLD X, VELOCITY WORLD Z)` |
| `VELOCITY WORLD X`, `Y`, `Z` | Miscellaneous | ft/s | meters per second | track/GS fallback; true vertical speed | doc: X east/west, Y vertical, Z north/south; signs **L2** |
| `VERTICAL SPEED` | Aircraft Misc | ft/s | meters per second | `pos.vs_gps_mps` | doc: "current *indicated* vertical speed". **L2** compares it with `VELOCITY WORLD Y`, and the geometric one feeds GPS VS |
| `MAGVAR` | Miscellaneous | degrees | degrees | `pos.magvar_deg` | computed at the user aircraft. **Sign L2** (capture at a place of known declination) |
| `PLANE PITCH DEGREES` | Aircraft Misc | radians (the name misleads) | degrees | `att.pitch_deg` = **−value** | no sign in the SimVar doc. Microsoft's avionics framework documents positive pitch as nose **down**. **L2** confirms |
| `PLANE BANK DEGREES` | Aircraft Misc | radians | degrees | `att.roll_deg` = **−value** | same source: positive is roll **left**. **L2** confirms |
| `PLANE HEADING DEGREES TRUE`, `…MAGNETIC` | Aircraft Misc | radians | degrees | `att.hdg_true_deg`, `hdg_mag_deg` | |
| `DELTA HEADING RATE` | Aircraft System | rad/s | degrees per second | `att.turn_rate_dps` | "rate of turn of heading indicator"; sign **L2** |
| `TURN COORDINATOR BALL` | Aircraft System | position 128 (−127…127) | position 128 | `att.slip_deg` = ball / 127 × profile `ball_full_scale_deg` | direction **L2**; the scale is a profile value |
| `G FORCE` | Aircraft Flight Model | GForce | GForce | `att.g_load` | |
| `AIRSPEED INDICATED`, `AIRSPEED TRUE` | Aircraft Misc | knots | meters per second | `air.ias_mps`, `tas_mps` | |
| `PRESSURE ALTITUDE` | Aircraft System | meters | meters | `air.pressure_alt_m` | doc: "at a 1013.25 hPa (1 atmosphere) setting" |
| `AMBIENT PRESSURE` | Miscellaneous | inHg | millibars | `air.static_pa` (×100) | at the user aircraft |
| `AMBIENT TEMPERATURE` | Miscellaneous | celsius | celsius | `air.oat_c` | |
| `AMBIENT WIND DIRECTION`, `…VELOCITY` | Miscellaneous | degrees (from true north), knots | degrees, meters per second | `wind.*` | test oracle only |
| `KOHLSMAN SETTING MB:1` | Aircraft System | millibars | millibars | `air.sim_qnh_pa` (×100) | index is the altimeter index |
| `SIM ON GROUND` | Miscellaneous | bool | bool | `sim.on_ground` | |
| `IS SLEW ACTIVE` | Aircraft Misc | bool | bool | teleport detection | |
| `TITLE` (+ `LIVERY NAME`) | Aircraft Misc | string ≤ 128 | string | `sim.aircraft` → profile | |
| `AUTOPILOT MASTER`, `…HEADING LOCK`, `…HEADING LOCK DIR`, `…HEADING SLOT INDEX`, `…ALTITUDE LOCK VAR` | Autopilot | bool, degrees, number, feet | same | `ap.*`, the §5 read-back | `HEADING LOCK DIR` and `ALTITUDE LOCK VAR` are settable |
| `COM ACTIVE FREQUENCY:1`, `COM STANDBY FREQUENCY:1` | Radio Navigation | Frequency BCD16 | hertz (**L1**: confirm it converts) | `radio.*` (R2) | |
| `NAV ACTIVE FREQUENCY:1`, `NAV RADIAL:1`, `NAV HAS NAV` | Radio Navigation | MHz, degrees, bool | same | `radio.nav1_*` (Later: RB-01 VOR addon) | |

**Engine 1, fuel and electrics.** The aircraft profile picks the variant.

| SimVar | Doc unit | Request unit | → SimState | Note |
|---|---|---|---|---|
| `GENERAL ENG RPM:1` | rpm | rpm | `eng[0].rpm` | |
| `ENG MANIFOLD PRESSURE:1` | inHg | inHg | `eng[0].map_pa` | the read-only variant; `RECIP ENG MANIFOLD PRESSURE` (psi) is the settable one |
| `GENERAL ENG OIL TEMPERATURE:1` | rankine | celsius | `eng[0].oil_temp_c` | |
| `GENERAL ENG OIL PRESSURE:1` | psf | psi | `eng[0].oil_press_pa` | |
| `GENERAL ENG FUEL PRESSURE:1` | psi | psi | `eng[0].fuel_press_pa` | |
| `RECIP ENG CYLINDER HEAD TEMPERATURE:1` | celsius | celsius | `eng[0].cht_c[*]` seed | MSFS has **no per-cylinder CHT or EGT**, so these are synthesised (MOD-060) |
| `GENERAL ENG EXHAUST GAS TEMPERATURE:1` | rankine | celsius | `eng[0].egt_c[*]` seed | |
| `RECIP ENG RADIATOR TEMPERATURE:1` | celsius | celsius | `eng[0].coolant_temp_c` | liquid-cooled profiles only; no coolant-temperature SimVar exists |
| `ENG FUEL FLOW GPH:1` | gallons per hour | gallons per hour | `eng[0].fuel_flow_lph` | |
| `FUELSYSTEM TANK QUANTITY:<tank>` | gallons | gallons | `fuel.left_l`, `right_l` | modern fuel system; tank names come from the profile |
| `FUEL LEFT QUANTITY`, `FUEL RIGHT QUANTITY` | gallons | gallons | `fuel.*` | fallback only. SDK: "the majority of these SimVars are only valid for legacy aircraft" |
| `ELECTRICAL BUS VOLTAGE:<bus>` | volts | volts | `elec.bus_v` | v2+ electrical system; the bus name comes from the profile |
| `ELECTRICAL MAIN BUS VOLTAGE` | volts | volts | `elec.bus_v` | fallback; marked **legacy** in the SDK |
| `NUMBER OF ENGINES`, `ENGINE TYPE` | number, enum | same | profile check | warn when the aircraft is not a single piston (`ENGINE TYPE` 0) |

A variable the aircraft does not implement raises a SimConnect exception that names the
datum. Only that field goes invalid, never to zero, and the adapter keeps running
(RB-SIM-MSF-130).

**Time.** These are environment variables, read through SimConnect like SimVars:

- `ZULU TIME` (seconds since 00:00Z), `ZULU DAY OF MONTH`, `ZULU MONTH OF YEAR`,
  `ZULU YEAR`;
- `SIMULATION TIME` (seconds since the sim started), used for pause detection.

Whether the datum needs the `E:` prefix is checked on **L1**. Early MSFS 2024 builds
returned the PC's UTC for `E:ZULU TIME` (reported fixed in 1.2.7), so the adapter logs the
sim version.

**Traffic.**

- **Request:** `SimConnect_RequestDataOnSimObjectType(…, dwRadiusMeters,
  SIMCONNECT_SIMOBJECT_TYPE_AIRCRAFT)`, repeated once a second rather than a periodic
  subscription.
- **Radius:** default 37 040 m (20 NM); the SDK maximum is 200 000 m, and anything larger
  returns `OUT_OF_BOUNDS`.
- **Per object:** `PLANE LATITUDE/LONGITUDE/ALTITUDE`, `GROUND VELOCITY`,
  `PLANE HEADING DEGREES TRUE`, `VERTICAL SPEED`, `SIM ON GROUND`, `ATC ID` (≤ 10
  characters), `ATC AIRLINE` and `ATC FLIGHT NUMBER`.
- **Processing:** exclude the user aircraft. Track comes from `VELOCITY WORLD X/Z` or
  from successive positions.
- **Limits:** multiplayer traffic is best-effort. The SDK makes only some variables
  available for far multiplayer aircraft (up to 10 km), and a devsupport bug reports
  by-type requests returning wrong data for multiplayer objects. Injected traffic in
  MSFS 2024 needs the 2024 AI model packages installed.

**System events** (`SimConnect_SubscribeToSystemEvent`):

- `Sim` (1 running, 0 not), `SimStart`, `SimStop`;
- `Pause_EX1`, a flag set: 1 full pause, 4 Active Pause, 8 sim-only pause (2 is
  deprecated);
- `FlightLoaded`, `AircraftLoaded`, `PositionChanged`, `Crashed`, `ObjectAdded`,
  `ObjectRemoved`.

Known gaps:

- The Esc pause, Active Pause and photo mode don't reliably send events.
- MSFS 2024 doesn't send `SimStop` when leaving to the main menu.

So pause and disconnection are also inferred from the data itself (RB-SIM-MSF-050,
MSF-060).

**Write-back key events** (`MapClientEventToSimEvent`, then `TransmitClientEvent`):

| Purpose | Event | Parameters | Rule |
|---|---|---|---|
| heading bug | `HEADING_BUG_SET` | [0] integer degrees 0–360, [1] index | |
| heading bug, fine | `AP_HEADING_BUG_SET_EX1` | [0] 0…16384, [1] index | preferred where the aircraft accepts it (**L7**) |
| HDG mode | `AP_HDG_HOLD_ON`, `AP_HDG_HOLD_OFF` | — | explicit state; **never** the `AP_HDG_HOLD` toggle |
| AP on/off | `AUTOPILOT_ON`, `AUTOPILOT_OFF` | — | coupling `full` only; **never** the `AP_MASTER` toggle |
| altitude preselect | `AP_ALT_VAR_SET_ENGLISH` | [0] feet, [1] index | R2 |
| altimeter | `KOHLSMAN_SET` | [0] millibars × 16, [1] altimeter index | R2, QNH sync |
| COM standby | `COM_STBY_RADIO_SET_HZ` | [0] Hz | R2 |
| pause | `PAUSE_SET` | 1 or 0 | **undocumented**. Community-confirmed; acts like the developer-mode pause. **L1** |

Every write is checked by read-back (RB-SIM-MSF-100). The SDK has no such warning, but
aircraft with their own autopilot logic may ignore standard events, and read-back is how
we find out.

**Reposition** (IOS; R2 for scenario A). Set `SIMCONNECT_DATA_INITPOSITION` on the user
aircraft:

- latitude and longitude in degrees, altitude in feet;
- pitch, bank and heading in degrees;
- `OnGround`;
- `Airspeed` in knots, or −1 for cruise speed or −2 to keep the current one.

The SDK says it is for initialising a position (it reloads scenery), not for incremental
moves. The `AddToDataDefinition` datum name and data type are checked on **L1**.

**Requests and rates.** Use `SimConnect_RequestDataOnSimObject(…, SIMCONNECT_OBJECT_ID_USER,
SIMCONNECT_PERIOD_SIM_FRAME, flags 0, origin 0, interval n, limit 0)`. Choose `interval` so
the rate stays ≤ 30 Hz; the `SIM_FRAME` rate follows the sim frame rate and is measured on
**L1**. The SDK says to always use `SIMCONNECT_OBJECT_ID_USER` for multiplayer clients.

**Versions.**

- The adapter supports both MSFS 2020 and MSFS 2024.
- Per the SDK, a client built with the 2020 SDK keeps working on 2024, without the new
  features. The 2024 struct changes concern facility data, which we don't read.
- The adapter loads the `SimConnect.dll` the config names, which can differ per sim.
  **L1** tests the 2024 DLL against 2020 and the reverse.
- Discovery order:
  1. the config path;
  2. `%MSFS2024_SDK%` or `%MSFS_SDK%` plus `\SimConnect SDK\lib\SimConnect.dll`;
  3. the default `C:\MSFS 2024 SDK\…`.

  The exact path is confirmed on **L1**.

**Transport choice** (decisions D14, D17; build details in [wrapper.md](wrapper.md)):

- **The SDK build** (`rbsim-wrapper-sdk.exe`, Windows, Go build tag `simconnect_sdk`) uses
  the official `SimConnect.dll`, bound at run time. The DLL comes from the user's SDK
  install, because redistribution is unconfirmed (Q14). This is the reference transport.
- **The standalone builds** (`rbsim-wrapper.exe` on Windows; Linux amd64/arm64, including
  the RB-01 Pi) use a pure-Go implementation of the SimConnect wire protocol, over the
  named pipe or TCP.
  - node-simconnect (LGPL-3.0, TypeScript) shows that this works on MSFS 2020/2024, and
    reports the protocol as forward compatible.
  - The protocol is unofficial, and how ours may be written is Q16.
  - It is offered only after the differential test against the SDK build
    (RB-SIM-MSF-140).

**Sources** (checked 2026-10-26):

- SDK SimVar pages:
  - [Aircraft Misc](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/simvars/aircraft-simvars/aircraft-miscellaneous-variables/)
  - [Aircraft System](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/simvars/aircraft-simvars/aircraft-system-variables/)
  - [Flight Model](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/simvars/aircraft-simvars/aircraft-flight-model-variables/)
  - [Engine](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/simvars/aircraft-simvars/aircraft-engine-variables/)
  - [Fuel](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/simvars/aircraft-simvars/aircraft-fuel-variables/)
  - [Electrics](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/simvars/aircraft-simvars/aircraft-electrics-variables/)
  - [Autopilot](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/simvars/aircraft-simvars/aircraft-autopilot-and-assistant-variables/)
  - [Radio Navigation](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/simvars/aircraft-simvars/aircraft-radio-navigation-variables/)
  - [Miscellaneous](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/simvars/miscellaneous-variables/)
  - [Environment variables](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/environment-variables/)
- SDK events and API:
  - [Autopilot key events](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/key-events/aircraft-autopilot-and-flight-assistance-events/)
  - [Radio navigation events](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/key-events/aircraft-radio-navigation-events/)
  - [Instrumentation events](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/key-events/aircraft-instrumentation-events/index.md)
  - [SubscribeToSystemEvent](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/simconnect/api-reference/general/simconnect_subscribetosystemevent/)
  - [RequestDataOnSimObject](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/simconnect/api-reference/events-and-data/simconnect_requestdataonsimobject/)
  - [RequestDataOnSimObjectType](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/simconnect/api-reference/events-and-data/simconnect_requestdataonsimobjecttype/index.md)
  - [SIMCONNECT_DATA_INITPOSITION](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/simconnect/api-reference/structures-and-enumerations/simconnect_data_initposition/index.md)
  - [SimConnect SDK (2020 → 2024)](https://docs.flightsimulator.com/msfs2024/retail/programming-apis/simconnect/simconnect-sdk/)
- Sign evidence: [MS avionics framework, AttitudeDataProvider](https://microsoft.github.io/msfs-avionics-mirror/2024/docs/api/@microsoft/msfs-epic2-shared/interfaces/AttitudeDataProvider).
- Community and devsupport:
  - [PAUSE_SET usage](https://devsupport.flightsimulator.com/t/set-pause-simulation-with-simconnect/13479)
  - [photo mode sends no pause events](https://devsupport.flightsimulator.com/t/photo-mode-does-not-send-pause-events/15233)
  - [no SimStop on main menu](https://devsupport.flightsimulator.com/t/msfs2024-not-sending-simstop-when-leaving-for-the-main-menu/11572)
  - [ZULU TIME bug](https://devsupport.flightsimulator.com/t/time-related-simvars-are-mixed-up/11270)
  - [multiplayer by-type bug](https://devsupport.flightsimulator.com/t/no-multiplayer-simobject-data-through-simconnect-requestdataonsimobjecttype/17869)
  - [2024 traffic injection](https://www.avsim.com/forums/topic/694311-msfs-2024-traffic-injection-issue-realtraffic-psxt-compa/)
  - [DLL redistribution question 1](https://devsupport.flightsimulator.com/t/redistribution-rights-for-msfs-2024-native-simconnect-dll/18233), [question 2](https://devsupport.flightsimulator.com/t/redistribution-permission-for-msfs-2024-simconnect-dlls/18295)
  - [node-simconnect](https://github.com/EvenAR/node-simconnect)

### 3.2 X-Plane 12 datarefs (verify names and units in T6.1)

| Dataref | Unit | → SimState |
|---|---|---|
| `sim/flightmodel/position/latitude`, `longitude`, `elevation` | deg, deg, m MSL | `pos.*` |
| `sim/flightmodel/position/hpath` | deg true | `pos.track_true_deg` |
| `sim/flightmodel/position/groundspeed` | m/s | `pos.gs_mps` |
| `sim/flightmodel/position/vh_ind` | m/s | `pos.vs_gps_mps` |
| `sim/flightmodel/position/magnetic_variation` | deg | `pos.magvar_deg` (verify sign) |
| `sim/flightmodel/position/true_theta`, `true_phi`, `true_psi`, `mag_psi` | deg | `att.pitch_deg`, `roll_deg`, `hdg_true_deg`, `hdg_mag_deg` |
| `sim/cockpit2/gauges/indicators/turn_rate_heading_deg_pilot` | deg/s (verify) | `att.turn_rate_dps` |
| `sim/cockpit2/gauges/indicators/slip_deg` | deg | `att.slip_deg` (verify sign) |
| `sim/flightmodel2/misc/gforce_normal` | g | `att.g_load` |
| `sim/flightmodel/position/indicated_airspeed`, `true_airspeed` | kt, m/s | `air.ias_mps`, `tas_mps` |
| `sim/weather/aircraft/barometer_current_pas` | Pa (verify, XP12) | `air.static_pa` |
| `sim/flightmodel2/position/pressure_altitude` | ft (verify) | `air.pressure_alt_m` |
| `sim/cockpit2/temperature/outside_air_temp_degc` | °C | `air.oat_c` |
| `sim/cockpit2/gauges/actuators/barometer_setting_in_hg_pilot` | inHg | `air.sim_qnh_pa` |
| `sim/flightmodel/failures/onground_any`, `sim/time/paused`, `sim/time/zulu_time_sec`, `sim/time/local_date_days` | | `sim.*` |
| `sim/cockpit2/engine/indicators/engine_speed_rpm[0]`, `MPR_in_hg[0]`, `oil_temperature_deg_C[0]`, `oil_pressure_psi[0]`, `CHT_deg_C[0]`, `EGT_deg_C[0]`, `fuel_flow_kg_sec[0]` | | `eng[0].*`; kg/s → L/h through the profile fuel density |
| `sim/cockpit2/fuel/fuel_quantity[0..1]` | kg | `fuel.*` |
| `sim/cockpit2/electrical/bus_volts[0]` | V | `elec.bus_v` |
| `sim/cockpit2/tcas/targets/position/*`, `…/modeS_id`, `…/flight_id` | | `traffic[]` (verify) |
| write: `sim/cockpit2/autopilot/heading_dial_deg_mag_pilot`, `altitude_dial_ft`; commands `sim/autopilot/heading`, `sim/operation/pause_toggle`; v3 flight init for reposition | | reverse channel, IOS |

### 3.3 ForeFlight UDP (`:49002`)

| Datagram | Fields → SimState |
|---|---|
| `XGPS<id>` | lon, lat, alt MSL m, track true, gs m/s → `pos.*` |
| `XATT<id>` | true heading, pitch, roll (+ right; verify against roastbeef `xplane.go` and the producer) → `att.*` (other `att` fields invalid) |
| `XTRAFFIC<id>` | icao, lat, lon, alt ft, vs fpm, airborne, track, gs kt, callsign → `traffic[]` |

`air`, `eng` and `ap` are always invalid with this adapter (RB-SIM-ADP-050).

### 3.4 FlightGear

The shipped `rbsim.xml` generic protocol emits one CSV line per frame. Its column order is
the SimState field order in §2.2. Each column carries the FlightGear property as its
source, for example `/orientation/pitch-deg`, `/orientation/roll-deg`,
`/orientation/heading-deg`, `/velocities/airspeed-kt`, `/environment/pressure-inhg`,
`/engines/engine[0]/rpm`. The full list is written in T6.3.

### 3.5 RB flight logs: the standalone replay source (scenario B, student mission review)

**Input.** `flightlog.py` frames are roastbeef `SituationData`, `TrafficInfo`, EMS and
bridge maps, in RB units (kt, ft, ft/min, ft/s). The replay source converts them back to
SimState. Each state also carries the original frame as `sim.rb_raw`, keyed by socket
path. Sinks may take fields from it that SimState does not model, such as satellite counts
or `AHRSStatus`. **Canonical fields always win**, and validity always comes from the
canonical groups, so an injected failure cannot be undone by a raw field
(RB-SIM-RPL-020).

**Timebase.** This is the log's own clock (`FlightLog` seconds since start; sessions laid
end to end). The UTC shown on the instruments is the log's GPS time, shifted only in an
anonymised export.

**Player state** (RB-SIM-RPL-010):

| Control | Behaviour |
|---|---|
| `play(speed)` | 0.25×–20×; frames are delivered on the log clock scaled by `speed` |
| `pause()` | freeze: the last state is resent as valid at the sink rates (MOD-040) |
| `seek(t)` | rebuild the full state at `t` before anything is sent: the last situation (`situation_at`), the accumulated EMS and bridge maps (`_kv_state_before`), and the live traffic set (`traffic_at`). Each sink then gets one consistent snapshot, never a burst of history |
| `step(dt)` | advance `dt` seconds while paused; deliver only the final snapshot per stream (as roastbeef `Replay.step`) |
| `loop` | at the end, seek to the start of the mission's `range` |
| end of log | hold the last state and keep sending it, as the daemon would |

**Checkpoints.** Derived with roastbeef `flightlog.checkpoints(log)` (take-off, climb, top,
turn, final, landed), listed together with the mission's bookmarks (RB-SIM-RPL-030).

**Interpolation.** Logs are about 1 Hz. Linear interpolation of `pos` and `att` (angles
unwrapped) is a **display-only** option, off by default and always off in tests. It is
never used across a gap longer than 3 s or across a seek (RB-SIM-RPL-040).

**Import from the school's aircraft** (`python -m rbsim import <rb01-url>`,
RB-SIM-RPL-090 and RPL-100). GET only; nothing is ever written to the aircraft.

1. `GET /flightlogs` returns `[{Name, Size, ModTime, Path}]` from `ReplayLogPath`.
   **Skip the log being written**: the newest whose size grows between two listings 5 s
   apart.
2. Download `GET /logs/<Name>` and `GET /logs/<Name>-wal`.
   - The handler (`managementinterface.go:2622` `viewLogs`) answers a missing file with
     **HTTP 200** and a text body. Validate each download by its magic bytes instead:
     `SQLite format 3\0` for the database; `0x377f0682` or `0x377f0683` (big-endian) for
     the WAL.
   - A missing WAL is normal for a cleanly closed log.
   - With a non-default `ReplayLogPath`, the WAL is not reachable over HTTP (Q11). Warn
     and offer the file or USB import.
3. **Consolidate locally.**
   - Open the copy read-write and run `PRAGMA wal_checkpoint(TRUNCATE)`, then
     `PRAGMA integrity_check`. The copy is then self-contained, and roastbeef's
     `immutable=1` reader sees all of it.
   - Why: a power-cut log (the master switched off) keeps the flight in the `-wal`. Tested
     2026-10-26: such a copy read `immutable=1` without consolidation shows **no tables at
     all**.
   - Record the SHA-256 of the consolidated file.
4. **Capture the aircraft settings** with the GET-only endpoint list of roastbeef
   `test/display/capture.py`, blanking secrets as it does. Store them in the mission
   folder, never in the repo.
5. **Date the mission** from the log's GPS time (`FlightLog.started`), not from the file
   name or `ModTime`: the Pi clock is wrong before a GPS fix.

The same consolidation applies to a file or USB import that includes the `-wal`.

**Mission file** (`<mission>.rbmission.json`, stored locally by the school,
RB-SIM-RPL-050 and RPL-110). Values in angle brackets are filled by the school and never
leave its PC:

```json
{"mission": "RB-SIM-MISSION/1",
 "log": "flights/flightlog-20261017161040.0001.sqlite",
 "imported": {"from": "http://192.168.10.1", "at": "2026-10-26T15:02:11Z",
              "wal": true, "consolidated": true, "sha256": "…"},
 "aircraft": {"registration": "<school aircraft>", "settings": "flights/flightlog-20261017161040.0001.settings/"},
 "student": "<student>", "instructor": "<instructor>", "exercise": "<exercise>",
 "date": "2026-10-17", "range": [0, null],
 "bookmarks": [{"t": 410, "note": "<note>"}],
 "limits": {"bank_deg": null, "g_pos": null, "g_neg": null},
 "notes": "<notes>"}
```

**Event list** (RB-SIM-RPL-120). Computed once at load over the whole log. Each event is
`{t_start, t_end, kind, detail, severity}` and is a seek target.

| Kind | Source of the threshold |
|---|---|
| `ems_yellow`, `ems_red` per sensor | the arcs in the captured `ems.json`, in its own units |
| `speed_arc` | the captured speed-arc settings (location to verify in T7.3) |
| `bank`, `g` | `limits` in the mission or the school's defaults. **Never** an invented number presented as an aircraft limitation; no limit configured means no event |
| `traffic_alarm` | the alarm level in the logged traffic (field to verify in T7.3) |
| `gps_invalid`, `ahrs_invalid` | the canonical validity |

Events are debounced: at least 2 s long, and gaps under 3 s are merged.

**Anonymiser** (R2, only when a flight leaves the school: `python -m rbsim anonymise`,
RB-SIM-RPL-060). It writes a new SQLite file and a report:

- ownship registration, ICAO address and pilot fields are replaced with `X-ABCD`, `PILOT`
  and a fixed address;
- each third-party `Icao_addr`, `Tail` and callsign becomes a stable pseudonym, consistent
  within the file;
- the raw radio tables are dropped, as `flightlog.py extract` does;
- optionally, positions shift by a fixed vector and times by a fixed offset.

It finishes with a self-check that searches the output for every original identifier and
fails if any survives.

## 4. The contract and the hubs

### 4.1 RB-SIM-IF/1 over HTTP

The same three endpoints exist on both hubs. **Flight builds have none of them** (404),
and a wrapper that gets a 404 stops and reports "not a SIM build" (RB-SIM-IF-080).

| Endpoint | Sender rate | Purpose |
|---|---|---|
| `POST /sim/state` | **10–20 Hz** (20 recommended) | ownship situation, engine, the sim's autopilot read-back; the response carries RB-01's autopilot intents (§5) |
| `POST /sim/traffic` | 1 Hz | the full set of traffic targets |
| `GET /sim/status` | as needed | hub health, for wrappers, tests and the instructor page |

**Transport rules for the sender** (RB-SIM-IF-060):

- HTTP/1.1 with a **persistent connection**, one request in flight at a time, and
  `Content-Type: application/json`.
- A 500 ms timeout per request. On a timeout or error, drop that message and send the
  **latest** state next. Never resend a backlog.

On a LAN a keep-alive POST costs a few milliseconds; 20 Hz is far from any limit of Go's
`net/http` or Python's `http.client`.

**`POST /sim/state`.** Field names and units are RB-01's own: `SituationData` (roastbeef
`main/gps.go`) and the EMS keys of contract 1. A wrapper author can compare its output with
RB-01's `GET /getSituation`.

```json
{"v": 1, "src": "msfs", "seq": 4711, "paused": false,
 "gps":      {"GPSTime": "2026-10-26T10:15:30.00Z", "GPSLatitude": 43.185280, "GPSLongitude": 12.136674,
              "GPSAltitudeMSL": 3000.0, "GPSGeoidSep": 154.2, "GPSTrueCourse": 271.4,
              "GPSGroundSpeed": 98.0, "GPSVerticalSpeed": -4.2},
 "ahrs":     {"AHRSPitch": 2.1, "AHRSRoll": -15.3, "AHRSGyroHeading": 271.4, "AHRSMagHeading": 268.6,
              "AHRSTurnRate": -3.0, "AHRSSlipSkid": 0.4, "AHRSGLoad": 1.03},
 "baro":     {"BaroPressureAltitude": 2980.0, "BaroVerticalSpeed": -250.0},
 "airspeed": {"IndicatedAirSpeed": 98.4},
 "ems":      {"enginerpm": 2350, "manifoldpressure": 24, "oiltemperature": 92, "oilpressure": 4,
              "outsidetemperature": 12, "fuel1": 45, "fuel2": 44,
              "cht1": 165, "cht2": 172, "cht3": 160, "cht4": 168, "egt1": 690, "egt2": 712, "egt3": 705, "egt4": 698},
 "ap":       {"master": true, "hdg_mode": true, "hdg_bug_mag": 268, "alt_sel_ft": 3500},
 "sim":      {"name": "MSFS 2024", "version": "1.x.y", "aircraft": "<aircraft title>"}}
```

| Key | Required | Meaning |
|---|---|---|
| `v`, `src`, `seq` | yes | protocol version 1; sender name; increasing sequence number |
| `paused` | yes | `true` = the sim is paused. The hub keeps the values valid (freeze, §2.3) and the instructor page shows FROZEN |
| `gps`, `ahrs`, `baro`, `airspeed` | **yes, every message** | an object means valid and fresh now; **`null` means invalid**, and the hub shows invalid or INOP (RB-SIM-IF-030). Besides the fields in the example, a group may carry other `SituationData` fields of its own family (for example `GPSSatellites`, `GPSHorizontalAccuracy`, `GPSNACp` in `gps`); the replay passes these through. Unknown names are rejected (400) |
| `ems` | no | the full EMS map, sent at least once a second; stale after 3 s |
| `ap` | no | the sim autopilot's **read-back** state, which the hub uses to decide the intents (§5) |
| `sim` | no | sent on the first message and every 10 s; logged and shown in `/sim/status`. Fields: `name`, `version`, `aircraft`, and optionally `wrapper` (e.g. `"rbsim-wrapper 0.1.0 sdk windows/amd64"`) |

**Units** (RB's, not SI). Watch these:

| Field | Unit |
|---|---|
| `GPSAltitudeMSL`, `BaroPressureAltitude` | ft |
| `GPSGroundSpeed`, `IndicatedAirSpeed` | kt |
| `GPSVerticalSpeed` | **ft/s** (as in `SituationData`) |
| `BaroVerticalSpeed` | **ft/min** |
| `GPSTrueCourse`, `AHRS*Heading`, `AHRSPitch`, `AHRSRoll` | degrees. Headings 0–360; pitch + nose up; roll + right wing down |
| `AHRSGLoad` | g |
| `AHRSTurnRate`, `AHRSSlipSkid` | as RB-01 defines them (unit and sign confirmed in T3.1) |
| `GPSTime` | RFC 3339 UTC, the **sim's** clock |
| EMS values | numbers in the units of the aircraft's `ems.json` |

Static pressure for RB-02 is derived on the hub from `BaroPressureAltitude` (an exact ISA
relation), so senders don't send it. OAT is the EMS key `outsidetemperature`, as on the
aircraft.

**Validation** (RB-SIM-IF-040): any missing required key, non-numeric value or
out-of-range value rejects the **whole** message:

```json
400 {"v": 1, "error": "ahrs.AHRSPitch: not a number"}
```

Nothing is applied partially. This is deliberately unlike `/setEMS`, which silently drops
a whole POST over one bad value with HTTP 200.

**Single source** (RB-SIM-IF-050): the first sender owns the hub until it has been silent
for 2 s. Others get `409 {"v": 1, "error": "hub owned by msfs@192.168.10.23"}`.

**Response 200:**

```json
{"v": 1, "seq": 4711, "cmd": {"ap": {"hdg_bug_mag": 271, "hdg_mode": true, "master": null}}}
```

`cmd` is absent when there is nothing to steer (§5).

**`POST /sim/traffic`.** The full target set once a second, nearest first, at most 50.
Field names and units are RB-01's `TrafficInfo`:

```json
{"v": 1, "src": "msfs", "targets": [
  {"Icao_addr": 16711935, "Tail": "AI0042", "Lat": 43.20, "Lng": 12.10, "Alt": 3500,
   "Track": 90, "Speed": 110, "Vvel": 0, "OnGround": false}]}
```

- `Alt` is in ft, `Speed` in kt, `Vvel` in ft/min, and `Track` in degrees true.
- A target missing from a later set ages out through RB-01's normal traffic ageing.
- Sim objects without a real ICAO address get a stable pseudo-address from a hash of the
  sim object ID, in a range T3.1 picks.
- Whether `Alt` is pressure altitude or GNSS (`AltIsGNSS`) is settled in T3.1 (verify).

**`GET /sim/status`:**

```json
{"v": 1, "hub": "rb01", "build": "2.4.0-SIM", "src": "msfs", "peer": "192.168.10.23",
 "rate_hz": {"state": 19.8, "traffic": 1.0},
 "fresh": {"gps": true, "ahrs": true, "baro": true, "airspeed": true, "ems": true},
 "paused": false, "last_error": null}
```

`hub` is `rb01` or `hub-lite`.

### 4.2 Hub on RB-01 (`stratuxrun-sim`, roastbeef `main/siminput*.go`)

**Fan-out to RB-01's real paths** (RB-SIM-RB1-080). The functions are confirmed in T3.1.

| Contract group | Into RB-01 through |
|---|---|
| `gps` | NMEA `$GPRMC`/`$GPGGA` synthesised from the fields and fed to the real `processNMEALine` (`main/gps.go:1121`), with the GPS type set the way the TCP :30011 listener does. RB-01's own GPS parsing and validity logic run, as with a real receiver. A `null` group stops the sentences, so the fix is lost through the normal path |
| `ahrs`, `baro`, `airspeed` | written into `mySituation` under `muAttitude` / `muBaro`. This stamps `AHRSLastAttitudeTime` and `BaroLastMeasurementTime`, sets the AHRS status valid bits and a SIM `BaroSourceType`, and tracks `AHRSGLoadMin/Max`. `null`, or nothing for 500 ms (`ahrs`) or 1 s (`baro`, `airspeed`), marks them invalid the way a lost sensor does (RB-SIM-RB1-050) |
| `ems` | the same code as the `/setEMS` handler, so EMS store, datalog and `$RBEMS`/`$RBCYL` output behave as on the aircraft |
| traffic | the path that external traffic (FLARM `$PFLAA` / CoT) takes into the traffic store |

**Hardware sensors** do not write `mySituation` in an rbsim build (RB-SIM-RB1-060).
**Device output** (RB-SIM-RB1-090): the rbsim build adds `$RBATT` (20 Hz) and `$RBADC`
(10 Hz) to its NMEA outputs (serial, USB, BLE, TCP :2000), next to the `$GPRMC`, `$PFLAA`
and `$RBEMS` it already sends. RB-02/04/05/07 are then fed by RB-01, exactly as RB-04 and
RB-05 are in the aircraft. Flight builds never emit the two new sentences. BLE throughput
and latency at 20 Hz are measured on the bench (L4); USB is the reference link.

### 4.3 Hub-lite on a PC (no RB-01 hardware)

hub-lite is the Python bridge in hub mode, e.g. `python -m rbsim hub`. It:

- implements §4.1 identically, so wrappers don't change (RB-SIM-HMI-010);
- serves the RB-01 HMI through roastbeef's `FakeStratux`, imported from the checkout named
  by `RB_ROASTBEEF` or the config, from that checkout's `web/`. It pushes the service
  sockets with the daemon's own frame shapes:

  | Socket | Frame | Rate |
  |---|---|---|
  | `/situation` | merged `SituationData` | ≤ 10 Hz; the HMI drops frames within 100 ms |
  | `/traffic` | one `TrafficInfo` per target | 1 Hz each |
  | `/ems` | EMS map | ≤ 3 Hz |
  | `/status` | | 1 Hz |

- answers REST with a **neutral fixture** committed here (`X-ABCD`), never roastbeef's real
  aircraft snapshot. It lists the SIM annunciation addon in `/addons`;
- drives the ESP32 devices over USB with the §4.4 profiles (RB-SIM-ESP-070);
- has **no daemon logic**: no autopilot, alerts or GDL90. It never returns `cmd`, and
  `/sim/status` says `"hub": "hub-lite"`.

When the bridge runs a source (replay, msfs…) and hub-lite in one process, the contract
still runs over HTTP on localhost. One code path, testable like any other wrapper.

### 4.4 Device lines to the ESP32 instruments (RB-02, RB-04, RB-05, RB-07)

**Producers:** RB-01 (rbsim) on its NMEA outputs, or hub-lite over USB. Same sentences,
same rates.

**Physical link:** USB CDC (the ESP32-S3 USB-Serial-JTAG console, baud rate irrelevant),
or the RB-01 BLE / serial outputs.

**Line rules** (from rb-esp `main/RB/RB_Usb.c`, `RB04_NMEA_Parser.c`), RB-SIM-ESP-010:

- Format `$<ID>,<fields>*<hh>\r\n`, with the standard NMEA XOR checksum.
- ASCII 0x20–0x7E only. **At most 250 characters** before the line end, because the
  firmware buffer is 256 bytes including the NUL.
- Numeric fields: an optional `-`, digits, and at most one `.`; no exponent. **|value| ×
  10^decimals ≤ 32767**, so the number fits an `int16` once the point is removed.
- An empty field means unknown.

**Existing sentences, reused unchanged:**

| Sentence | Parsed in firmware when | Producer reference |
|---|---|---|
| `$GPRMC`/`$GNRMC`, `$GPGGA`/`$GNGGA` | `RB_ENABLE_USB_NMEA` (RB-02/RB-07 USB templates) | `RB02.c:1115` `NMEA_ParseBuffer` |
| `$PFLAU`, `$PFLAA` | `RB_ENABLE_USB_FLARM` (RB-05 WIRED-USB, RB-07 USB) | roastbeef `flarm-nmea.go`; rb-esp `RB05_FlarmParser.c` |
| `$RBEMS`, `$RBCYL` | `RB_ENABLE_EMS` (RB-04 EMS-USB) | roastbeef `ems.go:82`/`:98`; integers |

```
$RBEMS,2350,24,92,4,0,12,0,45,44*58
$RBCYL,165,172,160,168,690,712,705,698*46
```

**New sentences** (produced only by the rbsim RB-01 or hub-lite, parsed only in
`RB_ENABLE_SIM` firmware):

`$RBATT,<pitch>,<roll>,<hdg>,<turn>,<slip>,<g>,<status>*hh` (RB-SIM-ESP-020)

| # | Field | Unit / sign | Decimals | Range |
|---|---|---|---|---|
| 1 | pitch | deg, + nose up | 1 | −90.0 … 90.0 |
| 2 | roll | deg, + right wing down | 1 | −180.0 … 180.0 |
| 3 | hdg | deg true | 1 | 0.0 … 359.9 |
| 4 | turn | deg/s, + right | 1 | −99.9 … 99.9 |
| 5 | slip | ball deg, + right | 1 | −20.0 … 20.0 |
| 6 | g | normal load factor | 2 | −9.99 … 9.99 |
| 7 | status | 1 valid, 0 invalid | 0 | |

`$RBADC,<ias>,<static>,<vs>,<oat>,<status>*hh` (RB-SIM-ESP-030)

| # | Field | Unit | Decimals | Range |
|---|---|---|---|---|
| 1 | ias | kt | 1 | 0.0 … 999.9 |
| 2 | static | hPa, static pressure | 1 | 100.0 … 1100.0 |
| 3 | vs | ft/min, baro VSI | 0 | −9999 … 9999 |
| 4 | oat | °C | 1 | −99.9 … 99.9 |
| 5 | status | 1 valid, 0 invalid | 0 | |

```
$RBATT,2.1,-15.3,271.4,-3.0,0.4,1.03,1*4D
$RBADC,98.4,908.4,-250,11.5,1*7A
$RBATT,,,,,,,0*4D
$RBADC,,,,,0*4A
```

**Why static pressure and not altitude:** RB-02 computes altitude from its own baro reading
and the pilot's QNH, as it does with the BMP280. Injecting pressure keeps the device's QNH
knob, Auto-QNH and altimeter logic running unchanged.

**Device profiles and rates** (RB-SIM-ESP-070):

| Profile | Sentences and rates |
|---|---|
| `rb02` | `$RBATT` 20 Hz, `$RBADC` 10 Hz, `$GPRMC` + `$GPGGA` 5 Hz |
| `rb04` | `$RBEMS` 5 Hz, `$RBCYL` every 2 s (as RB-01 does) |
| `rb05` | `$GPRMC` 1 Hz, `$PFLAU` 1 Hz, `$PFLAA` 1 Hz per target; **≤ 10 targets, nearest first** (the firmware store holds 10) |
| `rb07` | `rb02` ∪ `rb05` |

`rb02` uses about 2 kB/s.

- hub-lite **reads each device continuously**: the firmware prints logs and NMEA echoes.
  It logs that output and recognises the boot banner, so it can warn when a device is not
  a SIM build (RB-SIM-ESP-060).
- **In the firmware**, sim data that is not fresh shows **INOP**. The limits are attitude
  500 ms, air data 1 s and GPS 3 s. There is no fallback to the board IMU or baro
  (RB-SIM-SAF-050).

## 5. Return channel: RB-01 autopilot → simulator (in the HTTP response)

### R1, autopilot (MVP, RB-01 hub only)

- **Who decides.** RB-01, in `siminput.go`, reads its own autopilot (active flag,
  heading-to-steer, nav validity) and the `ap` read-back in the request. It returns the
  **desired state** in the response:
  - `hdg_bug_mag`: heading-to-steer converted to magnetic. RB-01 does the conversion; the
    wrapper does none;
  - `hdg_mode`: `true`;
  - `master`: `true` while the RB-01 autopilot is active;
  - R2: `alt_sel_ft` from the VNAV target.

  `null` means "leave as is".
- **Level-triggered** (RB-SIM-IF-070). The intent is repeated in every response while it
  holds, so a lost response costs nothing. There are no edge events.
- **No intent** (`cmd` absent) when the RB-01 autopilot is inactive or its navigation is
  invalid. The wrapper then stops steering after 3 s without `cmd`, leaves the bug where it
  is, and shows "RB NAV LOST" (RB-SIM-REV-040). It never moves the bug to a default value.
- **The wrapper decides how much to apply**, through its local coupling mode
  (RB-SIM-REV-020), **default `off`**:

  | Mode | Behaviour |
  |---|---|
  | `off` | ignore `cmd` |
  | `bug` | apply `hdg_bug_mag` only; the pilot engages the sim AP |
  | `full` | also `hdg_mode` and `master` |

- **Writes:** at most 2 Hz, with a 1° deadband, only when the intent differs from the
  read-back. Explicit-state events only, each verified by read-back (MSFS:
  RB-SIM-MSF-090, MSF-100; decision D15).

### R2, later

- COM1 active/standby sync with the RB-01 radio plate (needs a radio emulation design).
- Optional QNH sync (off by default).
- IOS reposition and scenario load go IOS → wrapper → simulator directly, not through RB.

## 6. Failure layer (IOS, sim-agnostic)

Failures are applied in the bridge, between SimState and the contract client, so they work
the same with every simulator; third-party wrappers do not get them (RB-SIM-FLT-010). On a
replay of a student mission they are the R2 "what-if" overlay, started from the instructor
page. A seek re-applies every failure active at the target time. Every onset and clear is
logged with sim (or log) time and wall time (RB-SIM-FLT-030).

| Id | Effect | MVP |
|---|---|---|
| `gps_loss` | `gps: null` in the contract → RB-01 loses the fix through its normal path; device lines RMC `V`, GGA fix 0 | ✔ |
| `ahrs_fail` | `ahrs: null` → RB-01 AHRS invalid, `$RBATT` status 0 | ✔ |
| `pitot_block` | IAS frozen at onset value (R2: physical ram/drain model) | ✔ |
| `static_block` | `static_pa` and pressure altitude frozen, `vs_baro` 0 (R2: IAS error model) | ✔ |
| `cht_hot:<n>` | `cht_c[n]` ramps +Δ over T s | ✔ |
| `oil_press_low` | `oil_press_pa` ramps to the set value | ✔ |
| `traffic_conflict` | synthetic intruder on a collision course (bearing, closure, time to CPA configurable) | ✔ |
| `device_off:<sink>` | stop feeding one sink → that device goes INOP through staleness | ✔ |
| `freeze` | sim pause plus heartbeat (§2.3) | ✔ |

## 7. Timing and performance

- **Sampling:** adapters sample the simulator at **≥ 20 Hz** (RB-SIM-PRF-030).
- **Processing:** the bridge's per-frame processing is **≤ 5 ms at p99** on a typical
  simulator PC (RB-SIM-PRF-010).
- **End-to-end latency targets** (RB-SIM-PRF-020):
  - sim frame → RB-02 pixels: **≤ 100 ms**;
  - sim frame → RB-01 HMI pixels: **≤ 200 ms**. The HMI throttles `/situation` to 100 ms
    by design.

  Measure with a 240 fps video of the sim and the instrument during a roll step (T7.4).
  Before any qualification discussion, check the response-time criteria in AC 61-136B and
  CS-FSTD(A).
- **Rate caps:** sinks never exceed the RB contract rates. The HMI takes `/situation` at
  ≤ 10 Hz and `/ems` at ≤ 3 Hz. The daemon's AHRS loop runs at 20 Hz.

## 8. Requirements

The identifier scheme is `RB-SIM-<AREA>-<nnn>`, allocated in steps of 10. Identifiers are
permanent: withdraw, never renumber. Tests declare `# Covers: <id>`. Verification is
**T**est, **I**nspection, **D**emonstration or **A**nalysis.

| Id | Requirement | V | Milestone |
|---|---|---|---|
| **SAF-010** | RB-01 sim input code exists only in builds compiled with Go tag `rbsim`; the default `make` / `make ddpkg` output contains none of it | T (symbol check) | M3 |
| **SAF-020** | RB-02 family sim input exists only in templates defining `RB_ENABLE_SIM`; no flight template defines it (CI grep) | T | M4 |
| **SAF-030** | Every sim build shows a permanent SIM annunciation on every screen, and the splash "SIMULATOR – NOT FOR FLIGHT" | D | M3, M4 |
| **SAF-040** | Sim builds carry a `-SIM` version suffix, a distinct OTA component (`avionics-sim`) and distinct RB-Cloud device IDs | I | M3, M8 |
| **SAF-050** | Stale or invalid sim data shows INOP/invalid within the §2.3 / §4.3 timeouts; sim builds never fall back to hardware sensors | T | M3, M4 |
| **MOD-010** | SimState uses the §2.1 units; all conversions live in `units.py`, with golden-value tests | T | M0 |
| **MOD-020** | Every group carries `valid` and `t_mono`; staleness follows §2.3 | T | M0 |
| **MOD-030** | Every adapter has a test proving the §2.1 signs (nose up, right roll, right turn, ball right) | T | M0, M2, M6 |
| **MOD-040** | While paused, sinks keep sending the last values as valid (freeze, not failure) | T | M0 |
| **MOD-050** | Reposition, slew or flight load increments `teleport_seq` | T | M2, M6 |
| **MOD-060** | Synthesised CHT/EGT per cylinder comes from the aircraft profile spread and is flagged synthetic | T | M0 |
| **ADP-010** | Each adapter separates pure mapping from transport; the mapping is tested on Linux with captured fixtures | I, T | M2, M6 |
| **ADP-020** | The MSFS adapter requests an explicit unit for every SimVar | I, T | M2 |
| **ADP-030** | Adapters reconnect within 5 s of a simulator restart without a bridge restart | D | M2, M6 |
| **ADP-040** | The X-Plane adapter detects and reports a disabled or unreachable Web API | T | M6 |
| **ADP-050** | The ForeFlight-UDP adapter marks `air`, `eng` and `ap` invalid | T | M6 |
| **ADP-060** | The replay adapter accepts a roastbeef flight log by path or by (part of) its name, as `simulator.py` does | T | M0 |
| **RPL-010** | The standalone player provides play (0.25×–20×), pause-as-freeze, seek, step, loop and hold-at-end, per §3.5 | T | M1 |
| **RPL-020** | Replay states carry the original frames as `sim.rb_raw`; canonical fields and validity always override them | T | M1 |
| **RPL-030** | Checkpoints come from `flightlog.checkpoints()`, listed with the mission's bookmarks; jumping to one is a seek | T | M1 |
| **RPL-040** | Interpolation is display-only, off by default, off in tests, and never spans a gap > 3 s or a seek | T | M1 |
| **RPL-050** | Mission files follow the §3.5 format; a mission whose log or settings capture is missing or fails its SHA-256 is refused with a clear reason | T | M7 |
| **RPL-060** | (R2) The anonymiser replaces ownship and third-party identifiers per §3.5 and fails its self-check if any original identifier survives in the output | T | M7 |
| **RPL-070** | (R2) With the what-if overlay, a seek re-applies every failure active at the target time | T | M7 |
| **RPL-080** | The instructor page states that the replay is a debrief tool, not flyable, and not a flight training device | I | M1 |
| **RPL-090** | Import from an RB-01 is GET-only. It skips the log being written, downloads the database and its `-wal`, and validates both by magic bytes, not HTTP status | T | M7 |
| **RPL-100** | Imported logs are consolidated (WAL checkpoint + integrity check) before use, and their SHA-256 is recorded; the replay only opens consolidated copies | T | M7 |
| **RPL-110** | Student and mission data are stored only locally. The simulator has no path that uploads them, and deleting a mission removes its file and its imported copies | T, I | M7 |
| **RPL-120** | The event list follows §3.5. Thresholds come from the captured aircraft settings or the school's configuration only, and every event is a seek target | T | M7 |
| **RPL-130** | The instructor page shows a track overview and altitude profile with the replay cursor; clicking seeks | T | M7 |
| **MSF-010** | The MSFS adapter works with MSFS 2020 and MSFS 2024 through the `SimConnect.dll` the configuration names, found in the §3.1 discovery order. The bridge does not bundle `SimConnect.dll` while redistribution is unconfirmed (Q14) | T, I | M2 |
| **MSF-020** | User-aircraft data is requested on `SIMCONNECT_OBJECT_ID_USER`, period `SIM_FRAME`, with an interval that keeps delivery ≤ 30 Hz and ≥ 20 Hz | T, D (L1) | M2 |
| **MSF-030** | The §3.1 sign and scale rules (pitch, bank, turn rate, ball, MAGVAR, velocity axes) are applied in `msfs_map.py`. Each one is proven by a test on a real capture (L2) before release | T | M2 |
| **MSF-040** | UTC comes from the sim's environment variables (`ZULU TIME`, date). The sim version is logged with every session | T | M2 |
| **MSF-050** | `sim.paused` is true while `Pause_EX1` reports any pause flag, **or** while `SIMULATION TIME` does not advance for ≥ 0.5 s while frames still arrive. This covers the Esc pause, Active Pause and photo mode, which send no events | T | M2 |
| **MSF-060** | The adapter reports disconnected on a Quit message, on `Sim` = 0, or when no user-aircraft frame arrives for 2 s. It does not depend on `SimStop`, which MSFS 2024 omits when leaving to the main menu | T | M2 |
| **MSF-070** | Traffic is polled once a second with `RequestDataOnSimObjectType(AIRCRAFT)` within a configurable radius (default 37 040 m, max 200 000 m), excluding the user aircraft. Multiplayer traffic is best-effort and labelled so on the status page | T | M2 |
| **MSF-080** | Fuel, electrical and coolant variables come from the aircraft profile, modern variant first (`FUELSYSTEM TANK QUANTITY:<tank>`, `ELECTRICAL BUS VOLTAGE:<bus>`) with the legacy fallback. An unmapped field is invalid, never zero | T | M2 |
| **MSF-090** | Write-back uses explicit-state events only (`AUTOPILOT_ON/OFF`, `AP_HDG_HOLD_ON/OFF`), never toggles (`AP_MASTER`, `AP_HDG_HOLD`). The heading goes by `HEADING_BUG_SET` (integer degrees) or `AP_HEADING_BUG_SET_EX1` (0…16384) with the slot index | T | M5 |
| **MSF-100** | Every write is verified by read-back (`AUTOPILOT HEADING LOCK DIR`, `…HEADING LOCK`, `…MASTER`) within 2 s. On a mismatch, coupling stops and the status page shows "aircraft does not accept standard autopilot events" | T | M5 |
| **MSF-110** | Reposition uses `SIMCONNECT_DATA_INITPOSITION` for jumps only, never for incremental moves | T | M7 |
| **MSF-120** | The status page shows the MSFS version, the DLL path and version, the request rates, traffic count and the last SimConnect exception, decoded | D | M2 |
| **MSF-130** | A SimConnect exception on a data definition makes only the named datum invalid; the adapter keeps running and reports it | T | M2 |
| **MSF-140** | The pure-protocol transport (standalone builds) passes a differential test against the DLL transport on the same sim session (same values, same rates) before it is offered | T (L11) | M2 (after Q16) |
| **WRP-010** | The wrapper builds from Linux, with `CGO_ENABLED=0`, for linux/amd64, linux/arm64, windows/amd64 standalone and windows/amd64 with `simconnect_sdk` (`make wrapper-all`) | T | M2 |
| **WRP-020** | Only the SDK build contains the `SimConnect.dll` transport; standalone binaries contain no reference to it (symbol check in `make wrapper-check`) | T | M2 |
| **WRP-030** | All builds share every source except the transport files in `internal/simconnect/`; sources use the `simconnect.Conn` interface; build tags appear nowhere else | I, T | M2 |
| **WRP-040** | The binary reports version, variant (`sdk`/`standalone`) and OS/arch in `version`, on the status page and in the contract's `sim.wrapper` | T | M2 |
| **WRP-050** | The SDK build, when `SimConnect.dll` is missing, names every searched path and suggests the standalone build | T | M2 |
| **WRP-060** | `make wrapper-check` vets all four build combinations and runs the Linux tests; the Windows CI job runs the tests natively | T | M2 |
| **IF-010** | RB-SIM-IF/1 is HTTP/1.1 + JSON: `POST /sim/state`, `POST /sim/traffic`, `GET /sim/status`, the same on the RB-01 hub (rbsim) and on hub-lite (§4.1) | T | M0, M3 |
| **IF-020** | Contract field names and units are RB-01's (`SituationData`, `TrafficInfo`, EMS keys) per §4.1; every message carries `"v": 1` | T | M0 |
| **IF-030** | `gps`, `ahrs`, `baro` and `airspeed` are present in every state message: an object means valid and fresh, `null` means invalid | T | M0, M3 |
| **IF-040** | Any missing required key, non-numeric or out-of-range value rejects the whole message with 400 naming the field; nothing is applied partially | T | M0, M3 |
| **IF-050** | The first sender owns the hub until 2 s of silence; another sender gets 409 | T | M0, M3 |
| **IF-060** | Senders use one persistent connection, one request in flight, a 500 ms timeout, latest-wins (never a backlog); state at 10–20 Hz, traffic at 1 Hz | T | M0 |
| **IF-070** | Autopilot intents in the response are level-triggered desired state, repeated while valid, and absent when the RB-01 autopilot is inactive or its navigation invalid | T | M3, M5 |
| **IF-080** | Flight builds have no `/sim/*` handlers; a sender that gets 404 stops and reports "not a SIM build" | T | M3 |
| **IF-090** | The messages are the contract. Any future transport (WebSocket, UDP) carries the same v1 messages unchanged | I | — |
| **IF-100** | The contract test kit (reference sender = flight-log replay, reference receiver/validator `python -m rbsim check`, golden messages) passes against both hubs and every wrapper | T | M0, M1, M3 |
| ~~RB1-010~~ | *Withdrawn (D16): GPS and traffic over TCP :30011 from outside; now RB1-080* | — | — |
| ~~RB1-020~~ | *Withdrawn (D16): EMS by external `/setEMS`; now RB1-080* | — | — |
| ~~RB1-030~~ | *Withdrawn (D16): IAS by `/bridge/float` (stock-build "D0" mode removed)* | — | — |
| ~~RB1-040~~ | *Withdrawn (D16): `/sim/ws`; now IF-010/IF-020* | — | — |
| **RB1-050** | The rbsim daemon invalidates AHRS 500 ms and baro/airspeed 1000 ms after the last valid state, or at once on `null` | T | M3 |
| **RB1-060** | In an rbsim build, hardware sensor senders do not write `mySituation` | T | M3 |
| ~~RB1-070~~ | *Withdrawn (D16): flight-build fallback feeding; now IF-080 (no fallback)* | — | — |
| **RB1-080** | The RB-01 hub feeds `gps` through its real NMEA parser, `ems` through the `/setEMS` code path and traffic through the external-traffic path (§4.2) | T | M3 |
| **RB1-090** | The rbsim RB-01 emits `$RBATT` (20 Hz) and `$RBADC` (10 Hz) on its NMEA outputs; flight builds never emit them | T | M3 |
| **HMI-010** | hub-lite implements §4.1 identically and serves the HMI from a configured roastbeef checkout through FakeStratux, with a neutral fixture only | T, I | M1 |
| **HMI-020** | hub-lite socket rates stay within §4.3 | T | M1 |
| **HMI-030** | The HMI shows the SIM annunciation on every plate (hub-lite and rbsim RB-01) | D | M1, M3 |
| **ESP-010** | Device lines follow the §4.4 line rules | T | M0, M4 |
| **ESP-020** | `$RBATT` per §4.4 | T | M0, M4 |
| **ESP-030** | `$RBADC` per §4.4 | T | M0, M4 |
| **ESP-040** | Firmware rejects a bad checksum, an overlong line or an out-of-range field; status 0 → INOP | T (host tests) | M4 |
| **ESP-050** | Firmware INOP on staleness: attitude 500 ms, air data 1 s, GPS 3 s | T (host tests), D | M4 |
| **ESP-060** | hub-lite drains and logs device output and warns on a non-SIM build banner | T | M4 |
| **ESP-070** | Device profiles and rates per §4.4, the same from RB-01 and hub-lite | T | M3, M4 |
| **ESP-080** | At most 10 traffic targets per device, nearest first | T | M4 |
| ~~REV-010~~ | *Withdrawn (D16): wrapper reading RB-01 NMEA on TCP :2000; intents now come in the HTTP response* | — | — |
| **REV-020** | The wrapper's coupling mode is `off`, `bug` or `full`, default `off` (§5) | T | M5 |
| **REV-030** | RB-01 returns `hdg_bug_mag` already converted to magnetic; the wrapper writes at most 2 Hz, with a 1° deadband, only when it differs from the read-back | T | M5 |
| **REV-040** | No `cmd` for 3 s → the wrapper stops steering, keeps the bug, and annunciates "RB NAV LOST" | T | M5 |
| **FLT-010** | The failure layer sits in the bridge between SimState and the contract client and is simulator-independent | I, T | M7 |
| **FLT-020** | The §6 MVP failures are implemented | T | M7 |
| **FLT-030** | Failure onset and clear are logged with sim and wall time | T | M7 |
| **IOS-010** | The IOS web page provides freeze, reposition presets, scenario start, failures and session record start/stop | D | M7 |
| **IOS-020** | The IOS shows the configuration ID and per-source/per-sink health (rate, latency, staleness) | D | M7 |
| **IOS-030** | The IOS binds to localhost by default; LAN exposure is an explicit config | T | M7 |
| **PRF-010** | Bridge processing ≤ 5 ms p99 per frame | T | M7 |
| **PRF-020** | End-to-end latency within §7 targets | D (video) | M7 |
| **PRF-030** | Adapter sampling ≥ 20 Hz | T | M2, M6 |
| **TRN-010** | Splash and IOS state "non-credit trainer – not an approved FSTD/ATD" | I | M7 |
| **TRN-020** | Configuration ID (bridge, adapter, sim version, RB build IDs, aircraft profile hash) is shown and logged per session | T | M7 |
| **TRN-030** | A recorded session replays deterministically through the same sinks | T | M7 |
| **PKG-010** | Sim artifacts (Windows bundle, RB-01 SIM image, RB-02 SIM binaries) are separate from flight artifacts in name, version and channel | I | M8 |
