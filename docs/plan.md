# Step 3b — Implementation plan

Snapshot: **2026-10-26** (revised the same day after decisions D9–D12). The plan is a
list of task briefs, each written so a Claude session can execute it on its own. Run them
in dependency order. **STOP** marks a point where the maintainer must approve before work
continues.

## Milestones

Milestone numbers are stable identifiers, also used in the requirements table. **Order**
is the sequence to work them in.

| Order | M | Goal | Scenario | Depends | Status |
|---|---|---|---|---|---|
| 1 | **M0** | Bridge skeleton: model, units, NMEA builders, flight-log reader, synthetic source, CLI | all | — | not started |
| 2 | **M1** | **Standalone replay simulator on a PC**: player as contract sender, hub-lite with the RB-01 HMI, instructor page. **First deliverable** | **B** (also demo A/C) | M0 | not started |
| 3 | **M3** | RB-01 hub: `siminput.go` implements RB-SIM-IF, fans out to the real paths, returns autopilot intents, emits `$RBATT`/`$RBADC` | B, A | M0 | Makefile targets done (D10) |
| 4 | **M4** | RB-02 / RB-04 / RB-05 over USB (`RB_ENABLE_SIM` firmware for RB-02) | B, A | M0 | not started |
| 5 | **M7** | Student mission review: import from the school's aircraft (with WAL consolidation), local mission records, event list, track overview, bookmarks, instructor guide | **B** | M1 (M3, M4 for hardware) | not started |
| 6 | **M2** | The wrapper (Go: Linux, Windows standalone, Windows SDK) and the MSFS adapter; live checks in the test-machine session | **A** | M0 (T0.6) | T2.1 (doc verification) done |
| 7 | **M5** | Reverse channel: RB-01 autopilot flies an interactive sim | A, C | M2 or M6, M3 | not started |
| 8 | **M6** | X-Plane 12, ForeFlight-UDP and FlightGear adapters | C | M0 | not started |
| 9 | **M8** | Packaging and distribution | all | M7 | not started |

**Releases:**

| Release | Milestones |
|---|---|
| **R-B1**, standalone replay on a PC | M0 + M1 |
| **R-B2**, student mission review: import from the aircraft, plus RB hardware on the desk | + M7, M3, M4 |
| **R-A1**, MSFS | M0, M1, M2, M3, M4, M5 |
| **R-C1**, other simulators | + M6 |

## Definition of done (every task)

- Tests pass: `python -m unittest` here, plus the sibling repo's own gate (`make check`,
  and `make check-sim` for rbsim files, in roastbeef; the host tests in rb-esp).
- New tests carry `# Covers: RB-SIM-…` tags. docs are updated, and this file's status
  column too.
- Nothing is claimed that was not run. The task's **Cannot verify here** list is repeated
  in the final message, and anything live goes into §"Test-machine session" below.
- New source files carry the AGPL + commercial header (CLAUDE.md rule 7).
- No commit unless asked. Never stage files outside the task.

---

## M0 — Bridge skeleton

### T0.1 Scaffold the package
- **Read first:** CLAUDE.md (conventions and header), [protocol.md](protocol.md) §1–2.
- **Do:**
  1. Create the layout below, each file with the licence header.

     ```
     rbsim/ __init__.py __main__.py model.py units.py bus.py config.py nmea.py
     rbsim/sources/ flightlog.py synthetic.py
     rbsim/sinks/
     tests/ fixtures/
     tools/
     ```

  2. Add `pyproject.toml`: licence `AGPL-3.0-only`, no runtime dependencies, extras
     `serial = ["pyserial"]`.
  3. Add `.gitignore`: venvs, `__pycache__`, `*.sqlite`, `*.sqlite-wal`, `*.sqlite-shm`,
     `SimConnect.dll`, `shots/`, `missions/`, `flights/`. Student data must never be
     committed.
  4. Add GitHub Actions running `python -m unittest` on ubuntu-latest and windows-latest,
     Python 3.11 and 3.13.
- **Done when:** `python -m rbsim --help` runs, and an empty test suite passes locally.

### T0.2 Canonical model and units
- **Covers:** MOD-010, MOD-020, MOD-040, MOD-060.
- **Do:**
  1. `model.py`: dataclasses for every §2.2 group, each with `valid` and `t_mono`, plus
     `sim.source` and `sim.rb_raw`. `SimState.stale(now)` applies the §2.3 timeouts.
     `paused` keeps groups valid.
  2. `units.py`: kt↔m/s, ft↔m, ft/min↔m/s, inHg/hPa/psi↔Pa, °F↔°C, gal↔L, kg/s↔L/h
     (density argument), and ISA pressure altitude ↔ static pressure.
  3. Cylinder synthesis from the profile spread.
- **Done when:** golden-value tests pass, including ISA at 0, 5000 and 10000 ft against
  tabulated values, and the staleness and pause behaviour tests.

### T0.3 NMEA builders
- **Covers:** ESP-010, ESP-020, ESP-030.
- **Read first:** protocol §4.2 (the sentences RB-01 synthesises), §4.4; roastbeef `main/flarm-nmea.go` (`$PFLAA` at
  :156) and `main/ems.go:82`/`:98`; rb-esp `main/RB/RB04_NMEA_Parser.c` (field order,
  `int16` tokens).
- **Do:** `nmea.py` builds `$GPRMC`, `$GPGGA`, `$PFLAU`, `$PFLAA`, `$RBEMS`, `$RBCYL`,
  `$RBATT` and `$RBADC`, with checksums. It enforces the line rules and raises on
  violations instead of truncating.
- **Done when:**
  - The protocol.md example sentences are reproduced byte for byte.
  - A `$PFLAA` for a known geometry matches what roastbeef's `makeFlarmPFLAAString`
    produces (derive the expected string from the Go code).
  - Out-of-range values are rejected.

### T0.4 Sources: flight-log reader and synthetic
- **Covers:** ADP-060, RPL-020, MOD-030 (synthetic part).
- **Read first:** roastbeef `test/display/README.md` ("Developing the HMI against the
  simulator"), `flightlog.py` (`FlightLog`, `events`, `situation_at`, `traffic_at`,
  `_kv_state_before`, `checkpoints`, `resolve`), `simulator.py`;
  [rb-knowledge-base.md](rb-knowledge-base.md) (I-RVDX data facts).
- **Do:**
  1. `sources/flightlog.py`: import roastbeef's `flightlog` from
     `$RB_ROASTBEEF/test/display` (default `../roastbeef`) with `sys.path` insertion,
     isolated in one function.
  2. Convert frames to SimState in both directions, keeping the originals in
     `sim.rb_raw`.
  3. `state_at(t)` rebuilds the full state, using the three `*_at` helpers.
  4. `sources/synthetic.py`: scripted profiles (level, a standard-rate right turn, a climb,
     a roll step for latency), deterministic from a seed.
- **Done when:**
  - The synthetic turn proves the signs: + roll, + turn rate, increasing track.
  - On `test-data/flightlog-sample.sqlite`, `state_at(150)` matches
    `situation_at(150)` field by field after the round trip RB → SimState → RB, within
    unit-conversion tolerance.
  - The tests skip with a printed reason when the roastbeef checkout or the log is absent.

### T0.5 Bus, config and CLI
- **Do:**
  1. `bus.py`: one source, many sinks, each sink with its own rate scheduler; a recorder
     hook that writes JSONL.
  2. `config.py`: a JSON file plus CLI overrides covering source, sinks, device profiles,
     aircraft profile and the roastbeef path.
  3. `__main__.py`, for example `python -m rbsim replay sample --hub log` (a debug hub that
     prints instead of serving).
- **Done when:** replay → log sink prints 1 Hz frames, and Ctrl-C exits cleanly.

### T0.6 Contract kit: schema, client, validator, golden messages
- **Covers:** IF-010, IF-020, IF-030, IF-040, IF-050, IF-060, IF-100.
- **Read first:** protocol §1 and §4.1; decision D16.
- **Do:**
  1. `rbsim/contract.py`: the RB-SIM-IF/1 message schema (required groups, field names,
     units, ranges) as one table in code. It holds `validate(msg) -> error | None`, used
     by hub-lite and the validator, and the SimState ↔ contract-message conversion. The
     conversion is the single place RB units appear on the sender side.
  2. `rbsim/client.py`, the contract client (sender) for every source:
     - one persistent `http.client` connection, one request in flight, a 500 ms timeout;
     - latest-wins: if a send is still running, the newer state replaces the queued one;
     - state at 10–20 Hz and traffic at 1 Hz;
     - it returns the response's `cmd` to the source;
     - it detects 404 ("not a SIM build") and 409 (another owner).
  3. `python -m rbsim check [--port N] [--intents script.json]`, the reference receiver.
     It serves §4.1, validates every message, and reports rates, staleness, sequence gaps
     and units. It returns scripted autopilot intents, so wrapper teams can test coupling
     without RB-01.
  4. Golden messages in `tests/fixtures/contract/`:
     - valid ones: full; all groups `null`; paused; with and without `ems` and `ap`;
     - invalid ones, each with its expected 400 error text.

     These are the files the roastbeef `siminput_test.go` also reads (T3.2).
- **Done when:**
  - The validator accepts every valid golden message and rejects every invalid one with
    the expected error.
  - The client against the validator holds 20 Hz on localhost, drops rather than queues
    when the receiver stalls, and stops on 404.

---

## M1 — Standalone replay simulator on a PC (first deliverable, scenario B)

The target command:

```
python -m rbsim replay <log|mission> [--from S] [--to S] [--speed X] [--loop] [--hub lite|http://<rb01>]
```

With the default `--hub lite`, the same process starts hub-lite and sends to it over HTTP
on localhost, so the contract is exercised from day one. It opens:

- http://localhost:8000/RB-01/, the RB-01 HMI, with **SIM** on every plate;
- http://localhost:8000/instructor/, with play, pause, seek, speed and the checkpoints.

With `--hub http://<rb01>`, the same replay drives a real RB-01 SIM build instead (M3).

### T1.1 Player
- **Covers:** RPL-010, RPL-040, MOD-040.
- **Read first:** protocol §3.5; roastbeef `rbserver.py` `Replay` (`step`, `play`,
  `SNAPSHOT_GAP` and per-socket coalescing), which is the behaviour to match but driving
  SimState instead of sockets.
- **Do:**
  1. `rbsim/player.py` over the T0.4 reader: a clock scaled by speed, with pause as freeze,
     seek (rebuilds state, then sends one snapshot), step, loop and hold-at-end. Output goes
     through the T0.6 contract client. Fields from `rb_raw` that the contract allows (other
     `SituationData` fields of the same group, such as satellite counts) are passed
     through.
  2. Thread-safe control methods (the instructor page calls them).
  3. The display-only interpolation option.
- **Done when:** deterministic tests with a fake clock show:
  - seek to t sends exactly one snapshot equal to `state_at(t)`;
  - at 4× speed, 10 s of log takes 2.5 s;
  - pause keeps the groups valid;
  - interpolation is never applied across a seek or a gap > 3 s.

### T1.2 hub-lite: the contract server and the RB-01 HMI over FakeStratux
- **Covers:** IF-010, IF-030, IF-040, IF-050, HMI-010, HMI-020, RPL-020.
- **Read first:** protocol §4.1 and §4.3; roastbeef `test/display/rbserver.py`
  (`FakeStratux`, `push`, `device=`, `settings_overrides=`, `files`), `plates.py` (`WEB`).
- **Do:**
  1. `rbsim/hub.py`, `python -m rbsim hub`, serves §4.1 with `contract.validate`, single
     source ownership and the staleness timers. It never returns `cmd`.
  2. It starts `FakeStratux(WEB, port, device=None)` with neutral `status` (Version
     `RB-SIM`) and settings.
  3. It turns accepted messages into the `/situation`, `/traffic`, `/ems` and `/status`
     frames at the §4.3 rates. A `null` group or a stale group yields the invalid values
     the daemon would send.
  3. Never use roastbeef's real-aircraft fixture.
  4. When a mission carries the aircraft's settings capture, serve it through FakeStratux
     `files`, so the plates show that aircraft's gauges and arcs. That part belongs to
     T7.2; leave the hook here.
- **Done when:**
  - `python -m rbsim replay sample` shows the flight on http://localhost:8000/RB-01/.
  - The T0.6 golden messages give the same accept/reject results against hub-lite as
    against the validator.
  - A test with roastbeef's `plates.Display` and `cdp.py` (if Chromium is on PATH) checks
    that the attitude plate's speed and altitude readouts match `state_at(t)` after a
    seek. Use the `[ui-view]` selectors from the roastbeef memory.
- **Cannot verify here:** whatever needs Chromium, if it is not installed. Say so.

### T1.3 SIM annunciation addon
- **Covers:** HMI-030, SAF-030 (HMI part).
- **Read first:** roastbeef `web/RB-01/js/main.js` (how `/addons` entries are injected),
  `web/RB-01/addons/` examples. Line endings: the RB-01 JS is **CRLF**.
- **Do:**
  1. Write `rb01-addon/sim.js` and `sim.html` here (CRLF, to match the RB-01 JS) that add a
     fixed "SIM" banner on every plate.
  2. The HMI sink lists them in `/addons` and serves them.
  3. T3.1 decides whether they also move into roastbeef for the `rbsim` build.
- **Done when:** a screenshot sweep shows the banner on every plate.

### T1.4 Instructor page
- **Covers:** RPL-030, RPL-080, IOS-020 (partial), IOS-030, TRN-010.
- **Do:**
  1. A static page plus a JSON API on the same server, under `/instructor/` and
     `/instructor/api/*`. No build step, consistent with RB web code. It offers:
     - a flight or mission picker (`flightlog.available()` plus the local mission store
       from M7);
     - play, pause, speed, seek slider, step and loop;
     - the checkpoint list (`flightlog.checkpoints()`; bookmarks and events join it in
       M7);
     - the connected sinks with their rates and staleness;
     - the configuration ID;
     - the non-credit, not-flyable statement.
  2. Bind to localhost by default; LAN is an explicit option.
  3. Keep the API independent of the page, so tests and scripts drive the same calls.
- **Done when:** API tests drive load, seek, play and pause, and the checkpoint jump.
  A screenshot of the page is attached in the final message.

### T1.5 End-to-end demo check
- **Do:** run the target command on `flightlog-sample`, then:
  1. jump to the "turn" checkpoint;
  2. pause, and check the instruments hold;
  3. play at 4×;
  4. stop the bridge, and check the HMI shows the daemon-lost state.

  Record a short screen capture or screenshots under the scratchpad.
- **Done when:** the checks pass and are written up in this file's M1 status.

---

## M3 — RB-01 hub (`stratuxrun-sim`, decision D16)

RB-01 implements RB-SIM-IF/1 (protocol §4.1–§4.2). Any sender (the replay, the MSFS
wrapper, the validator's golden messages) drives the real daemon, and the daemon drives
the ESP32 devices. Done already (D10): the `stratuxrun-sim` and `check-sim` targets in roastbeef's
`Makefile`, plus `.gitignore` and `clean`. They are uncommitted in roastbeef.

### T3.1 Investigate the RB-01 sim input, then STOP
- **Repo:** roastbeef, read only. **Covers:** groundwork for SAF-010, RB1-*.
- **Handoff:** [handoff/rb01-roastbeef.md](handoff/rb01-roastbeef.md) holds the evidence
  gathered from GitHub (2026-10-28) and the proposed design for T3.1–T3.2.
- **Read first:** roastbeef AGENTS.md; `docs/certification/fha.md` and
  `standards/requirements.md` (what is certified core); [rb-knowledge-base.md](rb-knowledge-base.md)
  §RB-01.
- **Answer, with file:line evidence:**
  1. Does the HTTP server use `http.DefaultServeMux`? If so, a `//go:build rbsim` file can
     register `/sim/state`, `/sim/traffic` and `/sim/status` in `init()` without touching
     `managementinterface.go`. Do keep-alive connections hold at 20 Hz through that
     server's settings (timeouts, handler cost)?
  2. When are settings loaded relative to `init()`? How can an rbsim build keep the
     hardware senders off without editing `sensors.go`? Options: force
     `IMU_Sensor_Enabled`, `BMP_Sensor_Enabled` and `MS4525DO_Enabled` false in memory,
     without saving; or use the StratuxPlugin registration path. Find the exact setting
     names.
  3. GPS fan-out: can the rbsim file call `processNMEALine` with synthesised
     `$GPRMC`/`$GPGGA`, with the GPS type set as `handleNmeaInConnection` does
     (`network.go`)? Does a serial GPS still feed it at the same time, and which setting
     disables that?
  4. EMS fan-out: is the `/setEMS` handler's store update a callable function, or does the
     rbsim file need its own small copy? Traffic fan-out: which function takes external
     traffic into the store (the FLARM `$PFLAA` parser, CoT, or a lower one), how are
     alarm levels computed, and how is `Alt` treated (pressure or `AltIsGNSS`)? Pick the
     pseudo-address range for sim objects.
  4b. Device output: can an rbsim file emit `$RBATT`/`$RBADC` through the existing NMEA
     output (`sendNetFLARM` and its serial/BLE/TCP fan-out) at 20/10 Hz without touching
     other files? Is BLE throughput enough at that rate?
  4c. Autopilot intents: where does `main/autopilot.go` hold active, heading-to-steer and
     nav validity, and can an rbsim file read them safely (mutex)? Which magnetic
     variation source should convert them? (Since 2026-10-25 the HMI uses
     `settings/deviation.json` for variation.)
  5. What are the `AHRSStatus` bits and `ahrs.Invalid` handling, and which value should
     `BaroSourceType` SIM take? Where does the HMI read it?
  6. What sign convention does `AHRSSlipSkid` use? What units does `AHRSTurnRate` use?
  7. Which EMS keys and units do the gauges in `web/RB-01/settings/ems.json` read?
  8. How should the sim addon ship: in roastbeef `web/RB-01/addons/` and installed only by
     the sim package, or from this repo?
- **Output:**
  - Fill in the answers in [rb-knowledge-base.md](rb-knowledge-base.md).
  - Propose the minimal-touch design in [decisions.md](decisions.md): ideally no existing
    roastbeef `.go` file changes, only new `main/siminput*.go` files.
  - Close the `(verify)` items in protocol §4.1 and §4.2.
- **STOP:** the maintainer approves the design. D10 approved adding the files; the design
  of flight-adjacent code still gets a review.

### T3.2 `main/siminput.go` (rbsim build)
- **Repo:** roastbeef. **Covers:** SAF-010, SAF-040, SAF-050, IF-010…IF-080 (hub side),
  RB1-050, RB1-060, RB1-080, RB1-090.
- **Do, per the approved design:**
  1. `main/siminput.go` and `main/siminput_test.go` with `//go:build rbsim`.
  2. Add `POST /sim/state`, `POST /sim/traffic` and `GET /sim/status`, with strict
     validation (400 naming the field), single-source ownership (409) and the staleness
     timers.
  3. Fan out to the real paths (§4.2).
  4. Emit `$RBATT`/`$RBADC`.
  5. Return autopilot intents in the response. That part lands with M5 (T5.1); leave the
     hook.
  6. Read the T0.6 golden messages from this repo (path by env variable, skipped when
     absent) so both hubs are tested against the same files.
  7. Write tests in the existing requirements style. **Don't** add RB-SIM IDs to
     roastbeef's `requirements/`; reference them in comments (`RB-SIM-RB1-050`).
- **Done when:**
  - `GOWORK=off make check` is green, so the default build is unaffected.
  - `make check-sim` is green.
  - The symbol check shows no `siminput` code in the default `stratuxrun`.
- **Cannot verify here:** real hardware sensors being suppressed on a Pi.

### T3.3 End-to-end: replay → contract → real daemon
- **Repo:** rbsimulator. **Covers:** IF-080, IF-100, RB1-080, RB1-090.
- **Read first:** protocol §4.1–§4.2; the roastbeef memory "local daemon smoke test"
  (non-root `:8000` or `-port`, scratch HOME); `../roastbeef/test/integration/replay.py`.
- **Do:** write an integration test that:
  1. runs `make stratuxrun-sim` and starts the daemon non-root on a free port with a
     scratch HOME;
  2. points `python -m rbsim replay flightlog-sample --hub http://127.0.0.1:<port>` at it;
  3. reads the daemon's FLARM NMEA output on TCP :2000 (fixed port; skip if taken).

  The sender needs no RB-01-specific code; it is the T0.6 client.
- **Done when:**
  - `GET /getSituation` follows the log: GPS through the real parser, AHRS, baro, IAS,
    EMS, traffic.
  - `$RBATT`/`$RBADC` appear on :2000 at their rates.
  - Groups go invalid after their timeouts when the replay stops.
  - Against a plain `stratuxrun` the sender stops on 404.
- **Cannot verify here:** a hardware RB-01 (bench procedure in T3.4).

### T3.4 Bench procedure (test-machine session)
- Write `docs/bench-rb01.md`:
  1. Install the SIM package on a Pi.
  2. Unplug or disable the GPS.
  3. Connect it to the PC by Ethernet or Wi-Fi.
  4. Run `python -m rbsim replay sample --hub http://<ip>`, and connect an RB-02 SIM
     build to the RB-01 by USB (then BLE).
  5. Checklist: SIM banner; attitude, EMS and traffic on RB-01 *and* RB-02; INOP on
     both when the sender stops; BLE latency and rate for `$RBATT`.

---

## M4 — ESP32 devices over USB

### T4.1 Firmware sim parser (rb-esp, host-tested)
- **Repo:** rb-esp. **Covers:** ESP-020, ESP-030, ESP-040, ESP-050, SAF-020.
- **Handoff:** [handoff/rb02-rb-esp.md](handoff/rb02-rb-esp.md) holds the evidence gathered
  from GitHub (2026-10-28), the wiring proposal for T4.2, and the bench checklist.
- **Read first:** rb-esp CLAUDE.md **all of it**: CRLF, never `git add -A`, the `#error`
  guard, the `BuildMachine.h` backup, no stdlib parsing. Also `test/host/README.md`,
  `main/RB/RB_Usb.c`, `RB04_NMEA_Parser.c` (the `parse_int_token` pattern), and
  `RB02_DriverFactory.c/.h` (attitude and baro globals, the driver loops).
- **Do:**
  1. `main/RB/RB02_SimInput.c/.h`, wrapped in `#ifdef RB_ENABLE_SIM`.
  2. A parser for `$RBATT`/`$RBADC` using hand-rolled integer scanning in the existing style,
     with checksum verification. It stores fresh values plus a timestamp.
  3. `RB02_SimInput_IsFresh(kind)` for the staleness check.
  4. Host test `test/host/test_sim_parser.c`: valid lines, a bad checksum, an overlong
     line, out of range, empty fields → invalid, and the staleness boundaries.
- **Done when:** ctest is green with the new suite, and the existing 5 suites are unchanged.
- **Cannot verify here:** a firmware build. Say so.

### T4.2 Firmware wiring, then STOP
- **Read first:** `RB02.c` (where the screens read `AttitudePitch`/`Roll`/`Yaw`,
  `GFactor`, baro pressure and IAS), `InopWarning.c`, the splash code.
- **Do:**
  1. Propose the minimal `#ifdef RB_ENABLE_SIM` hooks:
     - the `RB_Usb.c` dispatch;
     - skipping `Driver_Attitude_Loop`/`Driver_Barometer_Loop` hardware reads and writing
       sim values instead;
     - INOP on stale data;
     - the SIM label and splash.
  2. Propose templates `BuildMachine-Template-RB-02-TOUCH-30hz-SIM-2.8.h` (and 2.1)
     derived from the `-USB-` templates.
  3. **STOP** for maintainer approval. The `RB_LICENSE_TYPE` and template naming are the
     maintainer's call (Q3).
  4. Then implement the approved hooks, with CRLF preserved and `file` checked.
- **Done when:**
  - Host tests are green.
  - `grep -l RB_ENABLE_SIM main/RB/BuildMachine-Template-*.h` lists only `*-SIM-*`
    templates; add this check to the host CMake or CI.
- **Cannot verify here:** compiling and flashing (test-machine session or the maintainer).

### T4.3 hub-lite USB output
- **Repo:** rbsimulator. **Covers:** ESP-060, ESP-070, ESP-080.
- **Scope:** the ESP32 output of **hub-lite** (protocol §4.3–§4.4). With a real RB-01,
  the RB-01 feeds the devices itself (RB1-090, T3.2). Both use the T0.3 sentence builders'
  rules, and RB-01's Go output is checked against the same golden sentences.
- **Do:**
  1. `rbsim/hubusb.py`, using `pyserial` when present. On Linux it falls back to raw
     `/dev/ttyACM*` with termios.
  2. Profiles `rb02`, `rb04`, `rb05` and `rb07` with the §4.4 rates.
  3. Port auto-detect by USB VID:PID (Espressif `303A:1001`, verify), plus a manual port
     option.
  4. A continuous reader that logs device output and detects the build banner.
  5. Nearest-10 traffic selection.
  6. RB-04 and RB-05 work with **today's** firmware (`$RBEMS`/`$RBCYL`, `$PFLAA`), so the
     standalone replay can drive them before T4.2 lands.
- **Done when:** tests over a Linux pty pair check rates (±10 %), line content and
  nearest-first selection.
- **Cannot verify here:** a real device.

### T4.4 Bench procedure (test-machine session)
- Write `docs/bench-esp32.md`: flash the SIM template, connect it, run
  `python -m rbsim replay sample --hub lite --usb auto:rb02` (hub-lite), then repeat with
  the RB-02 on a SIM RB-01 (`--hub http://<rb01>`). Checklist:
  - the SIM label;
  - attitude and altimeter follow the replay;
  - the QNH knob changes the altitude as it would in the aircraft;
  - INOP within 0.5 s of unplugging the bridge.

---

## M7 — Student mission review (scenario B, decision D13)

The school imports each training flight from its own aircraft and reviews it with the
student on the RB instruments.

### T7.1 Import from the aircraft
- **Covers:** RPL-090, RPL-100.
- **Read first:**
  - protocol §3.5 "Import";
  - roastbeef `main/managementinterface.go`: `handleFlightLogsGet` (around :1129) and
    `viewLogs` (:2622). Note the `.sqlite` special case and the HTTP 200 text on a missing
    file;
  - roastbeef `test/display/capture.py` (its GET endpoint list and secret blanking:
    import it, don't copy it);
  - the roastbeef memory on flight recordings (power-cut log shape: a 4 KB main file,
    everything in the `-wal`).
- **Do:**
  1. `rbsim/importer.py` and `python -m rbsim import <url|path>`: list, skip the log in
     progress, download the database plus `-wal`, validate magic bytes, consolidate
     (`wal_checkpoint(TRUNCATE)`, `integrity_check`), record the SHA-256, and capture the
     settings.
  2. A file or USB import path with the same consolidation.
- **Done when:** tests against a stdlib fake RB-01 HTTP server (with the 200-on-missing
  quirk) pass:
  - A power-cut log, generated in the test by a writer process that exits without
    closing, imports with every row readable through roastbeef `FlightLog`, the
    `immutable=1` reader.
  - A clean log with no WAL imports.
  - A growing log is skipped.
  - A text body in place of a WAL is rejected.
  - Nothing other than GET is ever sent.
- **Cannot verify here:** a real school RB-01 (queue L9).

### T7.2 Mission store
- **Covers:** RPL-050, RPL-110.
- **Do:**
  1. A local folder layout: `missions/<id>.rbmission.json`, `flights/` and the settings
     captures. Its root is set in config, outside the repo by default.
  2. Create, list and delete; deleting removes the mission's copies.
  3. Check the SHA-256 on open.
  4. Date missions from the log's GPS time.
  5. Serve the settings capture through the HMI sink (the T1.2 hook).
  6. Keep student data out of logs and the recorder output, and keep no upload path.
- **Done when:** tests show:
  - round trip of a mission;
  - a missing or changed log is refused with a reason;
  - delete leaves nothing behind;
  - a grep of the recorder JSONL finds no student field.

### T7.3 Debrief aids: events, track overview, bookmarks
- **Covers:** RPL-120, RPL-130, RPL-030.
- **Read first:** roastbeef `web/RB-01/settings/ems.json` and the speed-arc settings (find
  where the speed plate reads its arcs); the traffic frame's alarm field (verify the name
  in `TrafficInfo`); protocol §3.5 "Event list".
- **Do:**
  1. `rbsim/events.py` computes the event list over the whole log at load, with debounce.
  2. On the instructor page add:
     - the event list and bookmarks, merged with the checkpoints, all as seek targets;
     - an SVG plan view and altitude profile with the cursor, click to seek;
     - a bookmark button that stores the current time and a note in the mission.
- **Done when:**
  - On `flightlog-sample` with its aircraft settings, the EMS events match a hand check of
    the arcs.
  - No `bank` or `g` events appear without configured limits.
  - Clicking the track seeks to the nearest sample.

### T7.4 Instructor guide
- **Do:** write `docs/instructor-guide.md` for schools, not developers:
  - the school setup checklist: `ReplayLog` on, default `ReplayLogPath`, the debrief PC
    able to reach the aircraft's Wi-Fi;
  - import after the flight, and creating the mission;
  - a suggested debrief flow: checkpoints, then events, then bookmarks;
  - privacy: student data stays on the school PC; how to delete it;
  - the limitations: not flyable, 1 Hz attitude as RB displayed it, not a flight training
    device.

### T7.5 Performance and latency
- **Covers:** PRF-010, PRF-020.
- **Do:** benchmark bridge processing, and write the 240 fps video latency procedure for
  the test-machine session.

### R2 follow-ups (not scheduled yet)

| Item | Covers | Note |
|---|---|---|
| Anonymised export when a flight leaves the school | RPL-060 | `python -m rbsim anonymise`, protocol §3.5 |
| "What-if" failure overlay on the instructor page | FLT-*, RPL-070 | protocol §6 |
| Import from RB-Cloud | — | needs an additive download route in rb-cloud-php (Q12, **STOP**) |
| Cabin audio sync (RB-08 recordings) | — | file names carry the Pi clock; align on GPS time |
| 20 Hz attitude from `sensors_*.csv` | — | join on the `GPSTime` column, not the file name |
| Debrief report (HTML or PDF) for the student file | — | local only |

---

## M2 — The wrapper (Go) and the MSFS adapter (scenario A)

**Shape** (decision D17, [wrapper.md](wrapper.md)):

- One Go module `wrapper/` produces four artifacts from Linux: Linux amd64, Linux arm64
  (RB-01 Pi), Windows standalone `rbsim-wrapper.exe`, and Windows SDK
  `rbsim-wrapper-sdk.exe`.
- It reads the simulator and sends RB-SIM-IF/1 to a hub: the RB-01 SIM build or hub-lite.
- The SDK build carries MSFS first, through the official DLL. The standalone builds carry
  the synthetic source from day one, and MSFS once the pure-protocol client exists (T2.6,
  after Q16).
- Live checks run in the test-machine session (D12). Everything else is tested here.

### T2.0 Scaffold the wrapper module and the build matrix
- **Covers:** WRP-010…WRP-060, IF-060.
- **Depends:** T0.6 (golden contract messages and `python -m rbsim check`).
- **Read first:** [wrapper.md](wrapper.md) §1–§4; protocol §4.1; CLAUDE.md rule 7 (the
  header for Go files).
- **Do:**
  1. Create `wrapper/go.mod` (`go 1.27`, standard library only), the layout in
     wrapper.md §2, and the `Makefile` targets in §3. The simconnect transports start as
     stubs.
  2. `internal/contract`: types, validation against `../tests/fixtures/contract/`, and a
     client with keep-alive, one request in flight, a 500 ms timeout, latest-wins, and
     404/409 handling.
  3. `internal/sources/synthetic` and `cmd/rbsim-wrapper` (flags, `version` with variant).
  4. CI:
     - ubuntu-latest runs `make wrapper-check` and `make wrapper-all`, and uploads the
       four artifacts;
     - windows-latest runs `go test ./...` natively.
- **Done when:**
  - `make wrapper-all` produces the four artifacts and `make wrapper-check` is green.
  - `rbsim-wrapper-linux-amd64 --source synthetic:turn --hub http://127.0.0.1:<port>`
    against `python -m rbsim check` is reported valid at 20 Hz.
  - Against a 404 hub the wrapper stops with "not a SIM build".
- **Cannot verify here:** running the `.exe` files (no Wine; the Windows CI job runs the
  tests).

### T2.1 Verify the SimConnect mapping against the SDK docs — **done 2026-10-26**
- [protocol.md](protocol.md) §3.1 now carries, for every variable: the SDK page, the
  default unit, the request unit, settable, and the canonical target. It also covers the
  traffic request limits, the system events, the write-back events (explicit-state only),
  the reposition structure, the 2020/2024 compatibility notes, and the sources.
- **Facts that change the design:**
  - Most "DEGREES" variables are radians by default, and temperatures are Rankine. Always
    request explicit units.
  - MSFS has no per-cylinder CHT or EGT, and no coolant temperature (`RECIP ENG RADIATOR
    TEMPERATURE` is the nearest).
  - Fuel and main-bus variables are legacy-only, so the modern ones need a per-aircraft
    tank and bus mapping.
  - `PAUSE_SET` is undocumented.
  - Pause events are unreliable, and MSFS 2024 omits `SimStop` on the main menu.
  - Multiplayer traffic by type is buggy.
  - Time is an environment variable, with an early-2024 bug.
- **Still open, live only:** the pitch, bank, turn-rate, ball, MAGVAR and velocity-axis
  signs; whether `GPS GROUND TRUE TRACK` needs a powered GPS; whether time needs `E:`; the
  COM unit conversion; the `INITPOSITION` datum name; the DLL path; and the pipe name. All
  are in the test-machine queue (L1, L2, L7, L10).

### T2.2 MSFS mapping and aircraft profile (pure Go, tested here)
- **Covers:** ADP-010, ADP-020, MOD-030, MOD-050, MSF-030, MSF-080.
- **Read first:** protocol §3.1; `wrapper/internal/units`.
- **Do:**
  1. `internal/sources/msfs/map.go`: the data-definition list (datum, request unit,
     SimConnect datatype) as one table in code. A test diffs it against protocol §3.1, so
     the docs and the code can't drift.
  2. `frameToState(values, profile)`, applying the sign rules as named constants that L2
     will confirm.
  3. Teleport detection from `IS SLEW ACTIVE`, position jumps and `PositionChanged`.
  4. Traffic frames → SimState, excluding the user aircraft, with track from velocity or
     successive positions.
  5. The MSFS part of the aircraft profile: tank names, bus name, ball full-scale, cylinder
     spread, and a single-piston check.
  6. Synthetic fixtures in `wrapper/testdata/msfs/`, replaced by L2 captures.
- **Done when:**
  - Sign tests pass on the synthetic fixtures (pitch up → +, right bank → +, right turn →
    +, ball right → +), plus unit tests and the docs/code table diff.
  - An unmapped tank or bus gives an invalid field, not zero.

### T2.3 SDK build: the `SimConnect.dll` transport
- **Covers:** MSF-010, MSF-020, MSF-040, MSF-050, MSF-060, MSF-070, MSF-130, WRP-050,
  ADP-030, PRF-030.
- **Read first:** protocol §3.1 ("Time", "Traffic", "System events", "Requests and rates",
  "Versions"); the SDK pages for `SimConnect_Open`, `AddToDataDefinition`,
  `RequestDataOnSimObject`, `RequestDataOnSimObjectType`, `SubscribeToSystemEvent`,
  `GetNextDispatch`, `MapClientEventToSimEvent`, `TransmitClientEvent` and
  `SetDataOnSimObject`, plus the `SIMCONNECT_RECV_*` structures and
  `SIMCONNECT_RECV_EXCEPTION`.
- **Do:**
  1. `internal/simconnect/dll_windows.go` (`//go:build windows && simconnect_sdk`) binds
     the DLL with `syscall.NewLazyDLL` from the discovery order (WRP-050), and implements
     `Conn` with a polling dispatch goroutine on `GetNextDispatch`.
  2. `recv.go` decodes `OPEN` (the MSFS version), `QUIT`, `EXCEPTION` (mapped back to its
     datum), `SIMOBJECT_DATA`, `SIMOBJECT_DATA_BYTYPE`, `EVENT` and `EVENT_FILENAME`.
  3. `internal/sources/msfs/source.go`, transport-independent:
     - subscribe to `Sim`, `Pause_EX1`, `FlightLoaded`, `AircraftLoaded` and
       `PositionChanged`;
     - infer pause and disconnection (MSF-050, MSF-060);
     - poll traffic once a second (MSF-070);
     - reconnect with backoff (≤ 5 s).
  4. Write `docs/setup-msfs.md`: installing the SDK for the DLL, the SDK build versus the
     standalone build, local versus remote (`SimConnect.xml` / `.cfg`, firewall on the
     private network only), and the known issues from §3.1.
- **Done when:**
  - `GOOS=windows go vet -tags simconnect_sdk` is clean.
  - `recv.go` is tested on byte fixtures of the documented layouts.
  - The source is tested on Linux against a fake `Conn` with scripted messages: pause
    inference, disconnect, exceptions and traffic.
  - The Windows CI job exercises the DLL-not-found path.
- **STOP before release:** Q15.
- **Cannot verify here:** anything live (L1, L3, L10).

### T2.4 Capture subcommand and sign captures
- **Covers:** MSF-030.
- **Do:**
  1. `rbsim-wrapper capture --source msfs --seconds N --out file.json` records every §3.1
     variable, raw (default units *and* request units), plus the system events. Each
     capture is tagged with the sim version, aircraft title and location. The SDK build
     comes first.
  2. Write the L2 script for the test-machine session.
  3. Commit the captures as fixtures after checking for identifying data. Re-run the T2.2
     tests on them and flip any sign constant a capture disproves, with the capture as the
     test.

### T2.5 Status page
- **Covers:** MSF-120, WRP-040.
- **Do:** `internal/status` (`--status 127.0.0.1:8090`) shows:
  - the hub URL and response codes;
  - the sim connection, MSFS version, transport (`dll`, `pipe` or `net`) and DLL path;
  - the rates achieved and the traffic count, with the multiplayer caveat;
  - the pause state and its source;
  - the last SimConnect exception;
  - profile warnings and coupling state;
  - the wrapper version and variant.

### T2.6 Standalone builds: the pure-protocol transport — **STOP** on Q16
- **Covers:** MSF-140, WRP-030.
- **Do,** once Q16 is answered:
  1. `internal/simconnect/proto.go`, `net.go` and `pipe_windows.go` implement the messages
     `Conn` needs, sharing `recv.go`.
  2. Test on Linux against a fake SimConnect server built from captured exchanges.
  3. Run the differential test against the SDK build on the same sim session (L11).
  4. Only then enable `--source msfs` in the standalone and Linux builds, including on the
     RB-01 Pi.

---

## M5 — Reverse channel (interactive simulators only)

### T5.1 Autopilot intents (RB-01) and coupling (wrapper)
- **Covers:** IF-070, REV-020, REV-030, REV-040, MSF-090, MSF-100.
- **Read first:** protocol §5; the T3.1 answer 4c; roastbeef `main/autopilot.go` (where
  heading-to-steer and nav validity live, and the golden vectors in `autopilot_test.go`).
- **Do:**
  1. **RB-01 side** (`siminput.go`): build the level-triggered intent (magnetic heading
     bug, HDG mode, AP on) from the autopilot state and return it in the `/sim/state`
     response. Omit it when the autopilot is inactive or nav is invalid.
  2. **Wrapper side** (`wrapper/internal/coupling`, shared by every adapter): coupling
     modes, the write rate limit and deadband, applying only on difference from the
     read-back, and the 3 s loss rule.
  3. Adapter hooks `set_heading_bug(mag_deg)`, `set_hdg_mode(on)` and `set_ap(on)`. For
     MSFS they use explicit-state events, each verified by read-back (protocol §3.1,
     RB-SIM-MSF-090, MSF-100; decision D15). X-Plane follows in M6.
  4. The validator (`rbsim check --intents`) replays scripted intents, so wrapper coupling
     is tested without RB-01.
- **Done when:**
  - Intent unit tests pass on the roastbeef golden vectors.
  - A closed-loop test (a synthetic aircraft with a first-order heading response, sending
    to a `stratuxrun-sim` with a direct-to) converges on the waypoint track.
- **Cannot verify here:** MSFS live (test-machine session).

---

## M6 — X-Plane 12, ForeFlight-UDP, FlightGear (scenario C)

### T6.1 X-Plane mapping verification
- Same as T2.1, for protocol §3.2, using developer.x-plane.com (Web API v1, v2, v3 pages,
  dataref list).

### T6.2 X-Plane adapter
- **Covers:** ADP-010, ADP-030, ADP-040, PRF-030, MOD-050.
- **Do:**
  1. `wrapper/internal/sources/xplane/` (`map.go` and `source.go`): Web API (resolve
     dataref ids, subscribe over WebSocket at 20–30 Hz, write datarefs and commands).
  2. A UDP `RREF` fallback behind the same mapping.
  3. `tools/capture_xplane.py`.
- **Done when:** mapping tests pass on fixtures. If X-Plane is installed on this Linux box,
  run the live checklist here and say which version was used.

### T6.3 ForeFlight-UDP and FlightGear adapters
- **Covers:** ADP-050.
- **Do:**
  1. `wrapper/internal/sources/ffudp/`. Use roastbeef `xplane.go`'s producers as the
     oracle, by copying their format strings into a test helper (same licence holder).
  2. `wrapper/internal/sources/flightgear/` plus `rbsim.xml`, with the property list per
     protocol §3.4.
- **Done when:** unit tests pass. A FlightGear live run is possible on Linux if installed.

---

## M8 — Packaging

- **T8.0 Wrapper release:** the four CI artifacts from T2.0, code-signed for Windows,
  with a short README per build. No `SimConnect.dll` (Q14).
- **T8.1 Python tools bundle:** a PyInstaller one-folder build of the replay, hub-lite and the
  instructor page. For the standalone replay it vendors a pinned roastbeef `web/` and
  `test/display` subset, with its commit hash and AGPL notice (D8). No `SimConnect.dll`;
  that comes from the user's SDK.
- **T8.2 RB-01 SIM image:** `distro-rb-01/make-rbsim-image.sh`, modelled on
  `make-rb03-image.sh`. It installs `stratuxrun-sim` and the sim addon, disables the GPS
  and sensor services as T3.1 decided, uses SIM RB-Cloud credentials, and names the OTA
  component `avionics-sim`. distro-rb-01 is not git: write the script, and the maintainer
  runs it.
- **T8.3 RB-02 SIM binaries:** add the SIM templates to the BuildMachine scripts and the
  release naming.
- **T8.4 Lesson library distribution:** a folder or RB-Cloud channel under SIM device IDs
  (Q6).
- **Covers:** PKG-010, SAF-040.

---

## Test-machine session

Live checks happen in a **separate Claude session** on the maintainer's test machine
(D12). That session reads CLAUDE.md §"If this session runs on the test machine", works
the queue below, and writes results into the table.

| # | From task | Needs | Check | Ready when |
|---|---|---|---|---|
| L1 | T2.3 | Windows, MSFS 2024 (then 2020), SDK; `rbsim-wrapper-sdk.exe` from CI | connect (local pipe); DLL path and the env variable actually set by the SDK installer; values sane at rest; achieved `SIM_FRAME` rate and the interval for ≤ 30 Hz; reconnect after a sim restart within 5 s; `E:` prefix needed for `ZULU TIME`/`SIMULATION TIME`?; COM frequency in `hertz` converts?; `INITPOSITION` datum name; `PAUSE_SET` 1/0 works; pause inferred for Esc, Active Pause, photo mode; disconnect detected when leaving to the main menu | T2.3 done |
| L2 | T2.4 | same | the scripted sign captures (T2.4): parked heading north; parked heading east (the velocity-axis signs come from taxiing); level 100 kt cruise; 30° right bank held; 10° nose-up climb; skid right (ball right); a place of known magnetic declination; the same cruise with the avionics master off (`GPS GROUND TRUE TRACK`?); paused | T2.4 done |
| L3 | T2.3 | same, second PC on the LAN | remote SimConnect (`SimConnect.xml` global IPv4 on the sim PC, `SimConnect.cfg` beside the bridge), firewall on the private network only | T2.3 done |
| L4 | T3.4 | RB-01 Pi with the SIM package | bench procedure `docs/bench-rb01.md` | T3.3 done |
| L5 | T4.4 | RB-02 with a SIM firmware build | bench procedure `docs/bench-esp32.md` | T4.2 approved and built |
| L6 | T4.3 | RB-04 or RB-05, current firmware | standalone replay drives them over USB | T4.3 done |
| L7 | T5.1 | Windows, MSFS + RB-01 | RB-01 direct-to steers the MSFS heading bug (`bug` mode), then `full`. Run the **aircraft acceptance matrix**: default C172 (G1000), default C172 (steam gauges), one default modern GA, one third-party GA. For each, record whether `HEADING_BUG_SET` / `AP_HEADING_BUG_SET_EX1` / `AP_HDG_HOLD_ON` / `AUTOPILOT_ON` are honoured (read-back), and its fuel tank and bus names for the profile | T5.1 done |
| L8 | T7.5 | camera at 240 fps | end-to-end latency per protocol §7 | T7.5 done |
| L10 | T2.3 | MSFS 2020 and 2024 on the same or two PCs | the 2024 SDK DLL against MSFS 2020 and the 2020 DLL against 2024: which combinations connect and deliver the §3.1 set | T2.3 done |
| L11 | T2.6 (R2) | as L1 | differential test, pure-protocol vs DLL transport, same session: values and rates match (MSF-140) | T2.6 done |
| L9 | T7.1 | a school (or I-RVDX) RB-01 with `ReplayLog` on, after a flight ended by switching the master off | import over Wi-Fi: the power-cut log is consolidated and replays in full; the log in progress is skipped | T7.1 done |

Results:

| # | Date | Sim / SDK / OS / firmware versions | Result | Notes / captures |
|---|---|---|---|---|
| | | | | |

## Verification matrix

| Can be verified on this Linux box | Needs the test-machine session |
|---|---|
| model, units, NMEA, flight-log reader, player, HMI sink, instructor page (Chromium if installed), importer against a fake RB-01 (including power-cut WAL logs), mission store, event list, the `stratuxrun-sim` integration test, rb-esp host tests, pty USB tests, the closed-loop autopilot with a synthetic aircraft, the X-Plane and FlightGear adapters (if installed) | MSFS live (L1–L3, L7), RB-01 on a Pi (L4), RB-02 SIM firmware (L5), real RB-04/RB-05 (L6), latency video (L8), import from a real aircraft (L9) |
