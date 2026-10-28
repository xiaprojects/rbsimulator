# RB-02 family (rb-esp): simulator firmware, implementation instructions

**For:** whoever implements simulator input in `rb-esp` (RB-02, and RB-07 which shares the
firmware), the maintainer or an agent session working in that repo.

**From:** RB Simulator (`rbsimulator`), 2026-10-28.

**Plan tasks:** T4.1 → T4.2 (STOP) → T4.4 bench, in [../plan.md](../plan.md).

**Contract:** [../protocol.md](../protocol.md) §4.4. The requirement IDs are `RB-SIM-ESP-*`
and `SAF-*`, listed in protocol §8.

The file:line references below are from the public `xiaprojects/rb-esp` at **394d675**
(2026-10-27). Several files are **stubs on GitHub**: `RB_Usb.c`, `RB05_FlarmParser.c`, and
`RB04_NMEA_Parser.c`'s parser body. The maintainer's tree is ahead, so re-check every
reference there before editing.

---

## 1. Goal

A **SIM firmware build** of RB-02 (and RB-07) shows the simulator's flight state instead of
its own sensors. It receives these lines on the USB console, from the RB Simulator
wrapper on a PC or from an RB-01 SIM build:

| Line | Rate | Already parsed today? |
|---|---|---|
| `$GPRMC`, `$GPGGA` | 5 Hz | yes, with `RB_ENABLE_USB_NMEA` (`RB02.c:930`, `:979`, `:1074`) |
| `$PFLAA`, `$PFLAU` | 1 Hz | yes, with `RB_ENABLE_USB_FLARM` (RB-07, RB-05) |
| `$RBATT` attitude | 20 Hz | **new**, SIM builds only |
| `$RBADC` air data | 10 Hz | **new**, SIM builds only |

GPS and FLARM already work with today's USB templates, using the wrapper's `nmea-flarm`
profile. This task adds attitude and air data, and the safety rules that make a SIM build
unmistakable.

## 2. Hard rules (read before writing code)

From rb-esp's own CLAUDE.md (read **all** of it) and the RB suite rules:

- **`main/` is CRLF.** Edit in binary, write `\r\n`, and check with `file`.
- **Never `git add -A` / `git add .`.** The tree carries the maintainer's WIP; stage by name.
- **`RB02.h` ends with a deliberate `#error`.** Leave it alone; host tests define
  `RB_HOST_TEST`.
- **Back up `main/RB/BuildMachine.h` before touching it,** and restore it from that copy,
  never with git.
- **No stdlib parsing** (`strtol`, `atoi`, `atof`, `sscanf`). Use hand-rolled integer
  scanning in the existing style.
- **Compile-time gating only:** all sim code is under `#ifdef RB_ENABLE_SIM`, which only
  `BuildMachine-Template-*-SIM-*.h` templates define (RB-SIM-SAF-020). A runtime setting
  alone is never enough.
- **Identification.** A SIM build shows:
  - a permanent **SIM** label on every screen;
  - the splash **"SIMULATOR – NOT FOR FLIGHT"**;
  - a version suffix **`-SIM`**;
  - its own OTA component name and RB-Cloud device IDs.
- **No fallback to the real IMU or baro** (RB-SIM-SAF-050). When sim data is not fresh, the
  instrument shows **INOP**: a bench unit sitting still would otherwise draw a convincing
  level attitude.
- You cannot build or flash firmware without ESP-IDF; say so. Passing host tests is not the
  same as compiling.

## 3. What the code looks like today (evidence)

| Topic | Finding | Where |
|---|---|---|
| USB templates | `RB-02-TOUCH-30hz-USB-2.8/2.1`: `RB_ENABLE_GPS`, `RB_ENABLE_USB_NMEA`, AAT/ALT/SPD/VAR/TRN/GMT…, `RB_LICENSE_TYPE_COMMERCIAL`. `RB-07-…-USB-*` adds `RB_ENABLE_USB_FLARM` | `main/RB/BuildMachine-Template-*-USB-*.h` |
| USB task | created when `RB_ENABLE_USB_FLARM`, `RB_ENABLE_USB_NMEA` or `RB_ENABLE_EMS` is set: `usb_console_input_thread`, 4096 stack, core 1. `RB_Usb.c` is `#include`d from `main.c` | `main/main.c:193–220` |
| USB line reader | **stub on GitHub** (empty body). Per CLAUDE.md: a 256-byte buffer including NUL, printable 0x20–0x7E only, each line dispatched to the FLARM, NMEA and EMS parsers by compile flag | `main/RB/RB_Usb.c:48` |
| GPS parser | `NMEA_ParseBuffer` → `nmea_RMC_mini_parser` / `nmea_GGA_mini_parser`; each field goes to `nmea_*_UpdatedValueFor(csv, finalNumber, decimals)`. RMC: time `hhmmss.ss`, lat/lon via `nmea_to_decimal`, speed in kt (→ km/h), track. GGA: fix quality, satellites, HDOP, altitude in m | `main/RB/RB02.c:692–1170` |
| GPS INOP | `Operative_GPS = false` after **5 s** without RMC (`GPSLastSpeedKmhReceivedTick`) | `main/RB/RB02.c:2255–2261` |
| INOP overlay | `OperativeWarning` (`InopWarning` image) is shown when `Operative_Attitude`, `Operative_BMP280` or GPS is 0 | `main/RB/RB02.c:283–284`, `:1895`, `:2264–2275` |
| Attitude globals | `AttitudePitch`, `AttitudeRoll`, `AttitudeYaw`, `AttitudeYawDegreePerSecond`, `GFactor`, `GFactorMax/Min`, `GFactorDirty` | `main/RB/RB02_DriverFactory.c:45–52`, `.h:13–26` |
| Attitude source | `Driver_Loop` (main.c) calls `Driver_Attitude_Loop()` → `QMI8658_Loop()` / `BMI270_Loop()`. QMI writes `AttitudeRoll`/`AttitudePitch` with output filtering | `main/main.c:89–112`; `main/RB/RB02_DriverFactory.c:202–212`; `main/QMI8658/QMI8658.c:882–887` |
| Baro source | `Driver_Barometer_Loop()` → `Get_BMP280()` at about 1 Hz: `bmp280Pressure` (Pa); `Altimeter = (QNH*100 − bmp280Pressure) * 30` (hundredths of feet); `Variometer` (0.01 ft/s, filtered); `Operative_BMP280` from a range check | `main/BMP280/BMP280.c:100–135`; `main/RB/RB02.c:313`, `:1710–1713` |
| Speed | the speed tape uses GPS `NMEA_DATA.speed` (km/h ×10), or the MS4525DO pitot under `RB_ENABLE_NavCore` | `main/RB/RB02.c:1715–1745` |
| Ball | the turn-slip ball angle comes from `Accel.x`, with its sign from `Accel.y` | `main/RB/RB02.c:1704–1707` |
| Version | `"Version " RB_VERSION` label; boot banner `$RB,<line>,<display>,<RB_VERSION>,<model>,<mac>,<licence>,<status>` printed on the console | `main/RB/RB02.c:3528`, `:620` |
| Helpers | `nmeaChecksum`, `nmea_to_decimal` in `RB02_GUIHelpers.c`; `parse_int_token()` (static) in `RB04_NMEA_Parser.c` is "the only correct numeric parser in the tree" | CLAUDE.md module map |
| Traffic store | `FlarmObstacle { int16 x (east m), y (north m), z (vertical m), bearing; uint32 now; int16 speed; uint8 identifier[6]; char tail[8]; }`; 10 entries | `main/RB/RB05_FlarmParser.h:36–55` |

## 4. Wire format to implement (protocol §4.4)

Line rules (RB-SIM-ESP-010):

- the format is `$<ID>,<fields>*<hh>\r\n` with the NMEA XOR checksum, which must be
  **verified**;
- ASCII 0x20–0x7E only, at most **250 characters** before the line end;
- numeric fields: an optional `-`, digits, at most one `.`, no exponent, and
  `|value| × 10^decimals ≤ 32767` (it fits an int16);
- an **empty field means unknown**.

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

Test vectors (checksums checked by the wrapper's unit tests):

```
$RBATT,2.1,-15.3,271.4,-3.0,0.4,1.03,1*4D
$RBADC,98.4,908.4,-250,11.5,1*7A
$RBATT,,,,,,,0*4D
$RBADC,,,,,0*4A
```

**Why static pressure rather than altitude:** RB-02 computes altitude from its own baro
reading and the pilot's QNH. Injecting pressure keeps the QNH knob, Auto-QNH and the
altimeter logic unchanged.

**A live source of lines** for testing, from the rbsimulator C# wrapper (Windows):

```
rbsim-msfs --source synthetic --serial COM5=rb02          # straight into the device
rbsim-msfs --source synthetic --serial file:rb02.nmea=rb02 # capture for host tests
```

`rb07` adds `$PFLAA`/`$PFLAU`; `nmea-flarm` sends only GPS and FLARM, for today's USB
firmware.

## 5. Tasks

### T4.1: sim parser, host-tested (no hardware, no STOP)

1. `main/RB/RB02_SimInput.c/.h`, wrapped in `#ifdef RB_ENABLE_SIM`, with logic separate from
   LVGL so it can be tested on the host:
   - `uint8_t RB02_SimInput_ParseLine(const uint8_t *line, uint16_t len, int64_t now_us)`
     returns 1 if it consumed `$RBATT` / `$RBADC`, else 0.
   - `bool RB02_SimInput_IsFresh(RB02_SimKind kind, int64_t now_us)`, with the attitude
     limit **500 ms** and air data **1 s** (RB-SIM-ESP-050).
   - A struct holding the last valid values (scaled integers or floats) and the time each
     was received.
2. Integer scanning in the `parse_int_token` style: sign flag, digits, one decimal point,
   decimals counted, and range-checked on delivery. Verify the checksum (`nmeaChecksum`).
   **Reject the whole line**, without updating the timestamp, for:
   - a bad checksum, or more than 250 characters;
   - a non-printable byte, or the wrong number of fields;
   - a field out of range, or a field that doesn't fit int16.
3. `status` 0, or any required field empty → that kind becomes invalid **immediately**
   (clear its freshness), not after the timeout.
4. Host test `test/host/test_sim_parser.c` (see `test/host/README.md` for adding a suite):
   - the four vectors above;
   - a bad checksum; 251 characters; a stray byte; out-of-range values (`91.0` pitch,
     `1100.1` hPa); an int16 overflow; empty fields;
   - status 0 → invalid;
   - the freshness boundaries at 499/501 ms and 999/1001 ms;
   - lines with CRLF, LF only, or no terminator.
5. **Done when** ctest is green with the new suite, and the existing suites are unchanged
   (5 suites, the one known `TEST_IGNORE`).

### T4.2: wiring (propose, **STOP for approval**, then implement)

Propose these `#ifdef RB_ENABLE_SIM` hooks as a short design note. Wait for the
maintainer's approval, then implement them with CRLF preserved.

1. **Templates:**
   - `BuildMachine-Template-RB-02-TOUCH-30hz-SIM-2.8.h` and `-2.1.h`, copied from the
     `-USB-` ones, plus `#define RB_ENABLE_SIM 1`;
   - `BuildMachine-Template-RB-07-TOUCH-30hz-SIM-2.8.h`, from RB-07 USB.
   - The licence type and naming are the **maintainer's call** (open question Q3).
   - In `RB02_Defines.h`, add `#if defined(RB_ENABLE_SIM) && !defined(RB_ENABLE_USB_NMEA)`
     `#error`, so a SIM build always has the USB reader.
2. **Version suffix:** the build scripts append `RB_VERSION` after the template, so add the
   suffix at the point of use. For example, in `RB02_Defines.h`:
   `#ifdef RB_ENABLE_SIM` `#define RB_VERSION_SHOWN RB_VERSION "-SIM"` `#else` …. Use it in
   the version label (`RB02.c:3528`) **and** the boot banner (`RB02.c:620`). The wrapper
   reads that banner to tell a SIM build from a flight one (RB-SIM-ESP-060).
3. **USB dispatch** (`RB_Usb.c`): under `RB_ENABLE_SIM`, offer each complete line to
   `RB02_SimInput_ParseLine` first. If it returns 0, the existing NMEA and FLARM dispatch
   continues unchanged.
4. **Sensors off, sim values in.** In `Driver_Loop` (`main.c:89`), under `RB_ENABLE_SIM`,
   call `RB02_SimInput_Apply(now)` instead of `Driver_Attitude_Loop()` and
   `Driver_Barometer_Loop()`. The hardware is never read. Apply writes:

   | Global | From | Notes |
   |---|---|---|
   | `AttitudePitch`, `AttitudeRoll` | `$RBATT` pitch, roll | **(verify) the sign mapping**: the screens use `4.0 * AttitudePitch` and `-AttitudeRoll` (`RB02.c:1597–1614`). Settle it with a host test or a bench tilt of the real IMU, before trusting either sign |
   | `AttitudeYaw`, `AttitudeYawDegreePerSecond` | hdg, turn | the gyro screen uses `-AttitudeYaw` (arbitrary origin) plus `AttitudeYawCorrection` under `RB_ENABLE_TRK`. Choose the mapping so the card shows the sim's true heading |
   | `GFactor`, `GFactorMax/Min`, `GFactorDirty` | g | keep the max/min logic |
   | ball | slip | the ball is drawn from `Accel.x` and `Accel.y` (`RB02.c:1704`). Add a sim override for the ball angle rather than faking `Accel` |
   | `bmp280Pressure` (Pa) | static × 100 | then **the same formula** as `Get_BMP280`: `Altimeter = (QNH*100 − bmp280Pressure) * 30`. The QNH knob keeps working |
   | `Variometer` (0.01 ft/s) | vs ft/min × 100 / 60 | replaces the 1 Hz difference |
   | speed tape | ias (kt → km/h ×10) | the same path `RB_ENABLE_NavCore` uses for the pitot |
   | OAT, where shown | oat | |
   | `Operative_Attitude`, `Operative_BMP280` | `IsFresh(ATT)`, `IsFresh(ADC)` | 0 the moment data is stale or status 0 → INOP |

5. **INOP everywhere:** today the overlay logic sits in the AAT tab case
   (`RB02.c:2264–2275`). Check every SIM-enabled screen (ALT, SPD, VAR, TRN, GMT, gyro, map)
   shows INOP when its data is stale. Add the check where it's missing, under
   `RB_ENABLE_SIM`.
6. **GPS staleness:** the firmware uses **5 s** (`RB02.c:2258`), and protocol §4.4 says
   **3 s**. Propose 3 s in SIM builds only, or record 5 s as the decision.
7. **The SIM label and splash:**
   - a permanent label on `lv_layer_top()`, so it stays above every screen;
   - the splash text "SIMULATOR – NOT FOR FLIGHT", see `V2_VendorSplashScreen.c` and the
     splash code in `RB02.c`.
8. **OTA and cloud identity:** find the OTA component name and RB-Cloud device ID in the
   current tree (not visible in the public code), and give SIM builds their own.
9. **A CI or host CMake check:** `grep -l RB_ENABLE_SIM main/RB/BuildMachine-Template-*.h`
   must list **only** `*-SIM-*` templates.

**Done when:**

- host tests are green;
- the template check passes;
- a SIM template compiles in the maintainer's ESP-IDF (not verifiable here);
- a flight template compiles with no `RB_ENABLE_SIM` code, checked with `idf.py size` or a
  symbol diff.

## 6. Findings to check in the current tree

These come from the public code at 394d675. The maintainer's tree may already differ.

1. **The minus sign in the GPS scanners.** Publicly, `'-'` does
   `finalNumber = finalNumber - 1` (`RB02.c:965`, `:1013`), so `-3.0` parses as `-7.0`. A
   GGA altitude below sea level (e.g. EHAM −3 m) is misread, from RB-01 in the aircraft
   too. rb-esp CLAUDE.md describes a `sign` flag, so this may already be fixed. If not,
   pin it as a `test_known_issue_*` and fix it. Keep the hemisphere letter trick working
   (CLAUDE.md, "Conventions").
2. **RMC status is ignored.** Publicly, field 2 (`A`/`V`) is never checked. The wrapper
   therefore stops sending RMC/GGA while position data is stale, rather than sending `V`.
   If the current tree checks `V`, say so; nothing needs changing on the wrapper side.
3. **The `$PFLAA` ID with `!CALLSIGN`.** RB-01 and the wrapper send `F0ABCD!CALLSIGN`
   (roastbeef `flarm-nmea.go`), and the wrapper cuts the callsign to 7 characters to fit
   `tail[8]` with its NUL. Confirm in `ParsePFLAA` how `identifier[6]` and `tail[8]` are
   filled, and that a longer tail is truncated, not overflowed.
4. **USB reader limits:** 256 bytes including NUL, so a 250-character line plus CRLF fits.
   Also confirm whether the ESP32-S3 USB-Serial-JTAG console needs **DTR** asserted to
   receive. The wrapper defaults DTR and RTS off because of the auto-reset circuit, and can
   switch either on in its config.

Report the answers back. They go into `rbsimulator/docs/rb-knowledge-base.md`, and the
wrapper adjusts its output if needed.

## 7. Bench (T4.4, test-machine session)

This needs a SIM build flashed by the maintainer. Connect RB-02 by USB to the Windows PC and
run `rbsim-msfs --source synthetic --serial COMx=rb02`, then MSFS (`--source msfs`).

Checklist:

- [ ] the SIM label is on every screen; the splash says NOT FOR FLIGHT; the banner shows
  `-SIM` in `/api/status` → `last_device_line`
- [ ] attitude follows: a right turn draws right wing down; the nose-up pitch sign is right
- [ ] the altimeter follows, and **the QNH knob changes the indicated altitude** as it does
  in the aircraft
- [ ] IAS, VSI, turn rate, ball, g-meter (with min/max) and the heading card are right
- [ ] unplug USB or stop the wrapper: attitude INOP within **0.5 s**, air data within
  **1 s**, GPS within its limit
- [ ] the board IMU is never used: tilt the unit with the wrapper stopped and the display
  stays INOP
- [ ] RB-07: traffic from `rb07` appears on the radar, with callsigns
- [ ] a flight template flashed on the same unit ignores `$RBATT`/`$RBADC` completely

Record the firmware version, template, wrapper version and results in
`rbsimulator/docs/plan.md` §"Test-machine session" (item L5).
