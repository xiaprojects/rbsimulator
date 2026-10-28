# CLAUDE.md — RB Simulator (`rbsimulator`)

Guidance for Claude Code working in this repository.

## What this project is

RB Simulator reuses the RB-Avionics instruments as cockpit displays driven by a flight
simulator instead of aircraft sensors. The instruments are **RB-01**, the Pi primary
flight display (`../roastbeef`), and the **RB-02** family, the ESP32-S3 round instruments
in `../rb-esp`: RB-02, plus RB-04, RB-05 and RB-07 from the same firmware. There are three
scenarios:

| | Scenario | Who | Source of flight data |
|---|---|---|---|
| **A** | Microsoft Flight Simulator 2020 / 2024 | builders, home cockpits, RB owners rehearsing their avionics | MSFS via SimConnect |
| **B** | FBO / flight school: the **standalone RB Simulator** for **student mission review** | instructors, students | **The school's own flights**: the RB-01 flight log imported from its training aircraft and replayed through the RB instruments for debrief. No third-party simulator and no simulator licence (decisions D11, D13) |
| **C** | Other simulators and games | integrators, third parties | X-Plane, FlightGear, Prepar3D, any ForeFlight-UDP producer, or the open RB-SIM-IF |

The standalone replay simulator (B) is also the first deliverable. It needs nothing but
this repo, a roastbeef checkout and a flight log, so it runs and is tested end to end on
this Linux box. MSFS (A) and the other simulators (C) plug into the same bridge later.

**Status, 2026-10-26: planning done, no bridge code yet.** The roastbeef `Makefile` already
has the `stratuxrun-sim` and `check-sim` targets. Work the plan milestone by milestone
([docs/plan.md](docs/plan.md)).

## Read first

| File | Use it for |
|---|---|
| [docs/integration-targets.md](docs/integration-targets.md) | **Step 1.** Which simulators and software, their APIs, school licensing, priority |
| [docs/minimal-features.md](docs/minimal-features.md) | **Step 2.** Minimal feature set per device and scenario (MVP / R2 / later) |
| [docs/protocol.md](docs/protocol.md) | **Step 3.** RB-SIM-IF v1: canonical state, wire formats, rates, numbered requirements |
| [docs/plan.md](docs/plan.md) | **Step 3.** Milestones and self-contained task briefs, ready to execute |
| [docs/make-or-buy.md](docs/make-or-buy.md) | Make or buy: open-source connectors (MobiFlight and others) against the wrapper |
| [docs/rb-knowledge-base.md](docs/rb-knowledge-base.md) | What we reuse from RB-01 and RB-02, where it lives (file:line), and the gotchas |
| [docs/wrapper.md](docs/wrapper.md) | The simulator wrapper: Go, built on Linux; Linux + Windows standalone + Windows SDK builds; layout, make targets, testing, running |
| [docs/decisions.md](docs/decisions.md) | Decisions taken or proposed, and the open questions for the maintainer |
| [wrapper-cs/README.md](wrapper-cs/README.md) | The C# MSFS wrapper (D19): SimConnect via P/Invoke, HTTP + WebSocket avionics status; built and tested on the Windows test machine |
| [docs/handoff/](docs/handoff/) | Implementation instructions for the sibling repos: `rb01-roastbeef.md` (SIM hub), `rb02-rb-esp.md` (SIM firmware) |

Background on the whole RB suite is in `../rbavionicswalkthrough/`. Its
`docs/integration-map.md` is the contract table for every RB repository.

## Architecture

```
 Simulator / source         WRAPPER = rbsim-wrapper (Go) or        RB-SIM-IF/1, HTTP      HUB                    devices
                            the Python replay (this repo)
 ──────────────────         ──────────────────────────────────     ─────────────────      ───                    ───────
 MSFS ── SimConnect ───────▶ sources ─▶ SimState (canonical, SI)   POST /sim/state  ───▶  RB-01 stratuxrun-sim ─┬─▶ HMI, autopilot, alerts,
 X-Plane ─ Web API ────────▶          + failure layer, recorder    POST /sim/traffic───▶  (or hub-lite on a PC) │   datalog, GDL90 to EFBs
 FlightGear, FF-UDP ───────▶          ─▶ contract client           GET  /sim/status        │                    └─▶ RB-02/04/05/07: NMEA
 RB flight logs (replay) ──▶                                       ◀── response: AP intents┘                        ($RBATT $RBADC $GPRMC
        ▲                                                                                                           $PFLAA $RBEMS…)
        └──── heading bug / HDG / AP on, applied by the wrapper with read-back (coupling off|bug|full)
```

**RB-01 is the hub; RB-SIM-IF/1 is the only contract (decision D16).** All simulator
knowledge lives in the wrapper.

- Hubs expose three HTTP endpoints: `POST /sim/state` 10–20 Hz, `POST /sim/traffic` 1 Hz,
  and `GET /sim/status`. The JSON uses RB-01's own field names and units.
- RB-01 (`stratuxrun-sim` only) fans each message out to its real internal paths: the NMEA
  parser, the EMS store and the traffic store. It feeds the ESP32 devices on its existing
  outputs. hub-lite (Python, on a PC) offers the same contract when there is no RB-01.
- The messages are the contract. The transport stays HTTP until there is a reason to
  upgrade.
- Teams work in parallel against the contract kit: the replay as reference sender, and
  `python -m rbsim check` as reference receiver.

## Ground rules

1. **Simulator code never reaches an aircraft.** All sim input on the RB side is gated at
   **compile time**, never by a runtime switch alone. In roastbeef that is the Go build tag
   `rbsim` and a separate `stratuxrun-sim` target. In rb-esp it is `RB_ENABLE_SIM` in
   dedicated `BuildMachine-Template-*-SIM-*` templates. A sim build must:
   - show a permanent **SIM** annunciation, and the splash "SIMULATOR – NOT FOR FLIGHT";
   - carry a `-SIM` version suffix;
   - use its own OTA component name and RB-Cloud device IDs, so sim and aircraft packages
     and logs never mix.

   Don't propose any design that weakens this.
2. **A stale or invalid value must not look valid.** If sim data stops, the instrument shows
   INOP or invalid within the stated timeout. A sim build never falls back silently to the
   real IMU or baro: a bench unit sitting still would draw a convincing level attitude.
3. **The sibling repo's own guide wins.** Before editing `../roastbeef` or `../rb-esp`, read
   its AGENTS.md or CLAUDE.md. Points that bite: rb-esp `main/` is **CRLF** and has a
   deliberate `#error` guard. You cannot build its firmware here, and you must back up
   `BuildMachine.h` before touching it. roastbeef `web/RB-01/**/*.js` is CRLF while Go is
   LF, and `make check` plus the `Covers:` traceability must stay green. Some roastbeef
   behaviour falls inside its certified-core scope (`docs/certification/`), so leave flight
   code paths untouched: put sim code in new build-tagged files.
4. **Dirty sibling trees are the maintainer's WIP.** Never `git add -A` or `commit -a`
   anywhere. Stage only files you created or changed, one repo per commit. Commit only when
   asked.
5. **Reuse contracts; don't reinvent them.** Wire field names and units follow the RB
   contracts: roastbeef `SituationData`, EMS keys, `$RBEMS` field order, `$PFLAA`. A change
   to a shared contract means checking every consumer. See the contract table in
   `../rbavionicswalkthrough/docs/integration-map.md`.
6. **This repo is public, and schools' data is personal.** Don't commit real
   registrations, Mode-S codes, pilot or student names, mission records, aircraft
   snapshots or un-anonymised flight logs. Student and mission data live only on the
   school's PC; the simulator never uploads them (RB-SIM-RPL-110). Use the placeholders `X-ABCD` / `PILOT`,
   as the website does. roastbeef's `test/display/fixtures/rb01-device.json` is a real
   aircraft snapshot: never copy it here.
7. **Licensing.** This repo is dual-licensed like the rest of RB: AGPL-3.0 (`LICENSE`) or a
   commercial agreement with XIAPROJECTS SRL (decision D9). Every new source file carries
   the header below, in the file's comment syntax. Keep simulator SDK binaries
   (`SimConnect.dll`) out of git.

   ```
   This file is part of RB.

   Copyright (C) 2026 XIAPROJECTS SRL

   This program is free software: you can redistribute it and/or modify
   it under the terms of the GNU Affero General Public License as published
   by the Free Software Foundation, version 3.

   This program is distributed in the hope that it will be useful,
   but WITHOUT ANY WARRANTY; without even the implied warranty of
   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
   GNU Affero General Public License for more details.

   You should have received a copy of the GNU Affero General Public License
   along with this program. If not, see <https://www.gnu.org/licenses/>.

   This source is part of the project RB: RB Simulator
   (github.com/xiaprojects/rbsimulator).

   Community edition will be free for all builders and personal use as defined by the licensing model
   Dual licensing for commercial agreement is available
   Please join Discord community

   <file>.py: <one line on what the file does>
   ```
8. **Flight-adjacent honesty.** Say what you did not verify. Nothing here can run MSFS (it
   is Windows-only), build ESP32 firmware, or reach a Pi or an RB device. Mark unit and sign
   conventions you have not checked against the SDK docs as `(verify)`. Never guess them
   silently.

## Dev environment (this machine)

- Linux x86-64, user `stefano`, no sudo assumed. There is no Pi, no ESP32 toolchain, no
  MSFS, and X-Plane is not known to be installed.
- Python 3.14 has no third-party packages. Use a venv in the session scratchpad if you need
  any. The bridge core is stdlib-only by design.
- Go 1.27.1 is at `/usr/local/bin/go`, which is not on the non-login PATH:
  `export PATH=$PATH:/usr/local/bin:/usr/local/go/bin`.
- roastbeef runs non-root on x86 on `:8000` (or `-port N`). FLARM NMEA TCP `:2000` is a
  fixed port. See `../roastbeef/test/integration/replay.py` for the end-to-end pattern.
- The roastbeef simulator build targets were added 2026-10-26 and are not yet committed
  there. `make stratuxrun-sim` builds `go build -tags rbsim` with version `<ver>-SIM`;
  `make check-sim` runs vet and the `main/` tests with the tag on. Neither is part of
  `all`, `dpkg` or `check`. Until `main/siminput*.go` exists, both behave exactly like the
  flight build.
- The RB-01 HMI simulator can serve the HMI from the working tree:
  `cd ../roastbeef/test/display && ./simulator.py --idle`, then open
  http://localhost:8000/RB-01/ and use the `/sim/` controls.
- Real flight logs (datalog SQLite) live in `~/Desktop/win10/`, `~/Desktop/<YYYYMMDD>/` and
  `../roastbeef/test-data/`. They are 100+ MB and identify the aircraft, so never commit
  them. Cut fixtures with `flightlog.py extract` and anonymise them.

## Working the plan

- Take the next task in [docs/plan.md](docs/plan.md) whose dependencies are done. Each brief
  lists what to read first, the steps, the acceptance checks, and what cannot be verified
  here.
- Tasks marked **STOP** need the maintainer's approval before you continue: cross-repo
  changes, licence questions, anything touching flight code.
- Requirements are `RB-SIM-<AREA>-<nnn>` in [docs/protocol.md](docs/protocol.md) §8, the
  same scheme as roastbeef's requirements standard. Tag each test with
  `# Covers: RB-SIM-ESP-030`. Never renumber; withdraw instead.
- When you decide something, add it to [docs/decisions.md](docs/decisions.md). When a task
  finishes, update its status in docs/plan.md.

## If this session runs on the test machine

The Windows test machine (MSFS, the SDK, and later RB hardware) is used from a **separate
Claude session** (decision D12). If that is you:

1. Run `git pull`, then read this file, [docs/plan.md](docs/plan.md) §"Test-machine
   session" and the **Cannot verify here** lists of the tasks marked done.
2. Use the wrapper executables built by CI (`rbsim-wrapper-sdk.exe`,
   `rbsim-wrapper.exe`), not local rebuilds, so the binary under test is the one the Linux
   side produced ([docs/wrapper.md](docs/wrapper.md)). Do the live checks and captures
   listed in the plan: the T2.3 checklist, the T2.4 captures
   (`rbsim-wrapper-sdk.exe capture …`), and later the T3.4 and T4.4 bench procedures.
   Record the sim version, SDK version, OS build, and the wrapper version and variant.
3. Commit captures as fixtures under `wrapper/testdata/<sim>/` only after checking they
   hold no identifying data. Write the results into the task's section in docs/plan.md, so
   the Linux session can pick them up.
4. Don't change wrapper or bridge logic to make a live check pass without a failing test
   that shows why. Fix it on the Linux side, where the tests run.

## Conventions for the code to come

There are two code bases, one contract (D17):

- **`wrapper/`, Go (the simulator wrapper):** see [docs/wrapper.md](docs/wrapper.md).
  - Go 1.27, standard library only, `CGO_ENABLED=0`, cross-compiled from Linux into four
    artifacts: Linux amd64 and arm64; Windows standalone `rbsim-wrapper.exe`; Windows SDK
    `rbsim-wrapper-sdk.exe` with build tag `simconnect_sdk`.
  - Build tags appear only in `wrapper/internal/simconnect/`. Every tagged file has a stub
    with the negated constraint.
  - Keep the simulator mapping (`internal/sources/<sim>/map.go`, pure, tested on Linux)
    separate from the transport (`internal/simconnect`, the Web API client, sockets).
  - `make wrapper-check` (gofmt, vet of all four combinations, tests, symbol check) must
    stay green. Windows binaries can't be run here (no Wine); the Windows CI job runs the
    tests.
- **`rbsim/`, Python (the school tools):** the flight-log replay, hub-lite, the validator
  `rbsim check`, the instructor page and the mission tools.
  - Python ≥ 3.11, CLI `python -m rbsim`, stdlib-only core. `pyserial` is an optional
    extra for hub-lite's USB output.
  - Tests use stdlib `unittest` (`python -m unittest`) and also run under pytest.
- **Both:** inside each program, units are SI and angles are in degrees
  (`docs/protocol.md` §2). Conversions live in one place per code base
  (`wrapper/internal/units`, `rbsim/units.py`), tested against the same golden values.
  Programs convert to RB-01's units only at the contract edge. Golden contract messages
  live in `tests/fixtures/contract/` and are shared by the Go wrapper, the Python tools and
  roastbeef's `siminput_test.go`.
- **Live-sim captures** are recorded with `rbsim-wrapper capture` on the sim machine, and
  committed under `wrapper/testdata/<sim>/` after a check for identifying data.
- New source files carry the RB licence header block, as in the sibling repos.
- Commit messages: imperative summary, plus `#<issue>` when an issue exists.
