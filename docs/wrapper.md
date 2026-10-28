# RB Sim Wrapper — development and build guide

Snapshot: **2026-10-26** (decision D17). The wrapper is the program in the chain
**FS ⇄ SDK ⇄ wrapper → RB-01 → RB-02**. It reads an interactive simulator and sends
RB-SIM-IF/1 to a hub ([protocol.md](protocol.md) §4.1). It is developed and built on
Linux, and runs on Linux and Windows.

Scope:

- **The wrapper:** simulator adapters (MSFS first, then X-Plane, FlightGear, ForeFlight-UDP),
  the contract client, and autopilot coupling with read-back.
- **Not the wrapper:** the hub (RB-01's `siminput.go`, or hub-lite), the flight-log replay,
  the instructor page and the mission tools. Those stay in Python under `rbsim/` (D1, D17).

## 1. Build matrix

One Go module, `wrapper/`, cross-compiled from Linux with **`CGO_ENABLED=0`**. No SDK, no
mingw and no Windows machine are needed to build.

| Artifact | `GOOS/GOARCH` | Build tag | SimConnect transports | Runs on | Typical use |
|---|---|---|---|---|---|
| `dist/rbsim-wrapper-linux-amd64` | `linux/amd64` | — | pure protocol over TCP | the dev box, any Linux PC | MSFS on another PC (LAN); X-Plane/FlightGear on Linux |
| `dist/rbsim-wrapper-linux-arm64` | `linux/arm64` | — | pure protocol over TCP | **the RB-01 Pi** (SIM image) | nothing installed on the MSFS PC except a `SimConnect.xml` edit |
| `dist/rbsim-wrapper.exe` — **standalone** | `windows/amd64` | — | pure protocol over named pipe or TCP | Windows **without** the MSFS SDK | consumers, nothing to install |
| `dist/rbsim-wrapper-sdk.exe` — **SDK** | `windows/amd64` | `simconnect_sdk` | the official `SimConnect.dll` (+ pure protocol) | Windows **with** the MSFS SDK installed | the reference build; the differential test |

**Verified 2026-10-26 on this box** with a scratch module (Go 1.27.1):

- All four artifacts built from Linux: two ELF statically linked, two PE32+.
- `GOOS=windows go vet -tags simconnect_sdk ./...` passed.
- Only the SDK `.exe` contains a `SimConnect_Open` reference.

**What the two Windows builds share:** everything but the transport files.

- Sources and mappings talk to a `simconnect.Conn` interface, so the MSFS adapter is
  identical in both builds.
- The SDK build adds the DLL transport. It is the default there; `--simconnect net|pipe`
  still selects the pure protocol, which is how the differential test RB-SIM-MSF-140 runs
  both against the same sim session.

**"With the SDK" means at run time, not at build time.** The SDK build binds
`SimConnect.dll` when it starts (`syscall.NewLazyDLL`) and needs no headers or `.lib`
files to compile. The user's MSFS SDK install provides the DLL. We never ship it (Q14).

**Standalone MSFS support depends on Q16.** The pure-protocol client can only be written
once it is settled how: clean-room, or with node-simconnect as a reference. Until then the
standalone and Linux builds run the synthetic, X-Plane, FlightGear and ForeFlight-UDP
sources. `--source msfs --simconnect net|pipe` reports "not available in this version".

## 2. Repository layout

```
wrapper/
  go.mod                          module github.com/xiaprojects/rbsimulator/wrapper (go 1.27)
  Makefile                        the targets in §3
  cmd/rbsim-wrapper/main.go       CLI, flags, wiring, version/variant
  internal/model/                 SimState (protocol §2) and staleness
  internal/units/                 conversions; golden values shared with rbsim/units.py
  internal/contract/              RB-SIM-IF/1 types, validate, client (keep-alive, latest-wins, 404/409)
  internal/coupling/              autopilot intents → adapter writes: modes, deadband, ≤ 2 Hz, read-back, 3 s loss rule
  internal/sources/synthetic/     scripted profiles (level, turn, climb, roll step)
  internal/sources/msfs/          map.go (pure, protocol §3.1 table) + source.go (uses simconnect.Conn)
  internal/sources/xplane/        M6
  internal/sources/flightgear/    M6
  internal/sources/ffudp/         M6
  internal/simconnect/
    conn.go                       Conn interface: Open, AddToDataDefinition, RequestData…, Subscribe…, Transmit…, Next()
    recv.go                       SIMCONNECT_RECV_* decoding (documented layouts), shared by both transports
    proto.go                      pure-protocol client (all builds; gated on Q16)
    net.go                        TCP transport (all OSes)
    pipe_windows.go               named-pipe transport (Windows)
    dll_windows.go                //go:build windows && simconnect_sdk   — SimConnect.dll transport
    dll_stub.go                   //go:build !(windows && simconnect_sdk) — returns ErrNotInThisBuild
  internal/status/                optional local status page (--status 127.0.0.1:8090)
  testdata/                       MSFS captures (L2), protocol byte fixtures
../tests/fixtures/contract/       golden contract messages, shared with rbsim/ and roastbeef
```

**Build-tag rules** (RB-SIM-WRP-020, WRP-030):

- Windows-only code uses the `_windows.go` suffix.
- The DLL transport needs **both** `windows` and `simconnect_sdk`.
- Every tagged file has a stub with the negated constraint, so all four combinations
  compile.
- Nothing outside `internal/simconnect/` may carry a build tag.

## 3. Build and check (Linux)

The Go toolchain is at `/usr/local/bin/go` (Go 1.27.1), which is not on the non-login
PATH:

```sh
export PATH=$PATH:/usr/local/bin:/usr/local/go/bin
cd wrapper
make wrapper-all      # the four artifacts in dist/
make wrapper-check    # gofmt, vet for every combination, Linux tests, the transport symbol check
```

| Target | Command it runs |
|---|---|
| `wrapper-linux` | `CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -trimpath -ldflags "$(LDFLAGS)" -o dist/rbsim-wrapper-linux-amd64 ./cmd/rbsim-wrapper` |
| `wrapper-linux-arm64` | same with `GOARCH=arm64` |
| `wrapper-windows` | `GOOS=windows GOARCH=amd64 … -o dist/rbsim-wrapper.exe` (`variant=standalone`) |
| `wrapper-windows-sdk` | `GOOS=windows GOARCH=amd64 … -tags simconnect_sdk -o dist/rbsim-wrapper-sdk.exe` (`variant=sdk`) |
| `wrapper-all` | the four above |
| `wrapper-check` | `gofmt -l` must be empty; `go vet` for linux/amd64, linux/arm64, windows, and windows + `simconnect_sdk`; `go test ./...`; the symbol check (`SimConnect_Open` present in the SDK `.exe` only) |

`LDFLAGS = -s -w -X main.version=$(git describe --tags --always --dirty) -X main.variant=<sdk|standalone>`.
The binary reports both values in `rbsim-wrapper version`, in the status page, and in
the contract's `sim.wrapper` field (RB-SIM-WRP-040).

**Dependencies:** the standard library only (`syscall` for the DLL and the pipe). Anything
added later, such as `golang.org/x/sys/windows`, is recorded here with its licence, and
must be compatible with AGPL + commercial.

## 4. Testing

| What | Where it runs | How |
|---|---|---|
| units, model, MSFS mapping (signs, units, profile) | Linux | `go test`; fixtures from L2 captures replace the synthetic ones |
| contract client | Linux | against `httptest` fake hubs **and** the golden messages; against `python -m rbsim check` in an integration test |
| coupling logic | Linux | fake adapter with scripted read-back; the roastbeef autopilot golden vectors for the intents |
| `recv.go` decoding | Linux | byte-level fixtures of the documented `SIMCONNECT_RECV_*` layouts |
| pure-protocol client (after Q16) | Linux | a fake SimConnect server in `testdata/`, built from captured exchanges (L1/L11) |
| Windows-only files | compiled and vetted on Linux (`wrapper-check`); **run** by the Windows CI job (`go test ./...` on `windows-latest`): pipe transport against a fake pipe server, DLL-not-found error path | |
| live MSFS | test-machine session (plan L1, L2, L3, L7, L10, L11) | |

There is no Wine on this box, so Windows binaries are not executed here. Say so; don't
claim otherwise.

## 5. Running

```
rbsim-wrapper --source msfs --hub http://192.168.10.1                      # RB-01 SIM build on the LAN
rbsim-wrapper --source msfs --hub http://127.0.0.1:8000                    # hub-lite on the same PC
rbsim-wrapper --source synthetic:turn --hub http://127.0.0.1:8000          # no simulator
rbsim-wrapper capture --source msfs --seconds 20 --out capture.json        # T2.4 sign captures
rbsim-wrapper version
```

| Flag | Default | Meaning |
|---|---|---|
| `--source` | — | `msfs`, `xplane`, `flightgear`, `ffudp`, or `synthetic:<profile>` |
| `--hub` | `http://127.0.0.1:8000` | hub URL; 404 → "not a SIM build", stop (RB-SIM-IF-080) |
| `--simconnect` | SDK build: `dll`; standalone Windows: `pipe`; Linux: `net` | the transport |
| `--simconnect-addr` | — | `host:port` for `net`, matching the sim PC's `SimConnect.xml` |
| `--dll` | discovery (below) | path to `SimConnect.dll` (SDK build only) |
| `--coupling` | `off` | `off`, `bug` or `full` (protocol §5) |
| `--profile` | built-in default | aircraft profile JSON (tanks, bus, ball scale, cylinder spread, EMS units) |
| `--rate` | 20 | state messages per second (10–20) |
| `--traffic-radius` | 37040 | metres, ≤ 200000 |
| `--status` | off | `127.0.0.1:8090` serves the status page |

**Windows, SDK build:**

- **DLL discovery order:** `--dll`, then `%MSFS2024_SDK%` or `%MSFS_SDK%` plus
  `\SimConnect SDK\lib\SimConnect.dll`, then the default `C:\MSFS 2024 SDK\…`. The exact
  path is confirmed in L1.
- When the DLL is missing, the wrapper names every path it searched and suggests
  `rbsim-wrapper.exe` (RB-SIM-WRP-050).

**Windows, standalone build:**

- It connects over the local named pipe. The pipe name for MSFS 2020/2024 is confirmed in
  L1.
- Or it connects over TCP with `--simconnect net --simconnect-addr`, when `SimConnect.xml`
  enables IPv4.

**Linux and the RB-01 Pi:**

- **On the MSFS PC:** `SimConnect.xml` gets an IPv4 server, global scope, address
  `0.0.0.0`, and a port (5111 in the docs' example). Allow that port in the Windows
  firewall on the private network only.
- **On the Pi:** the wrapper runs as a service of the SIM image only, with
  `--hub http://127.0.0.1` (the daemon serves `:80` as root).

**Unsigned executables:** Windows SmartScreen warns about unsigned `.exe` files. Code
signing is an M8 item.

## 6. Licensing

- Go files carry the AGPL + commercial header (CLAUDE.md rule 7).
- **SDK build:** it loads Microsoft's `SimConnect.dll` at run time. Before distributing
  it, the copyright holder decides on an AGPL section 7 additional permission (Q15).
- **No build bundles `SimConnect.dll`** (Q14).
- **Pure protocol:** written as Q16 decides. No LGPL code is copied into this dual-licensed
  module.

## 7. Requirements

The wrapper's build requirements are RB-SIM-WRP-010…060 in [protocol.md](protocol.md) §8.
Its MSFS behaviour is RB-SIM-MSF-*, its contract behaviour RB-SIM-IF-*, and its coupling
RB-SIM-REV-*.
