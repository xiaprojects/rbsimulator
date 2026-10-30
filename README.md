# RB Simulator

Drive the RB-Avionics instruments from a flight simulator. The instruments are
[RB-01](https://github.com/xiaprojects/roastbeef), the Pi primary flight display, and the
[RB-02 family](https://github.com/xiaprojects/rb-esp), the ESP32 round instruments, which
are the same hardware and software flown in experimental aircraft.

Three uses:

- **Microsoft Flight Simulator:** use your RB panel in a home cockpit, or rehearse your
  aircraft's avionics on the ground.
- **Flight schools and FBOs:** the **standalone RB Simulator** replays the school's own
  training flights, taken off the RB-01 in its aircraft, through the RB instruments for
  the student's debrief. The instructor plays, pauses, jumps to take-off, the steepest
  turn or final, and goes through the events where the aircraft's limits were exceeded.
  No third-party simulator is needed, and student data stays on the school's PC.
- **Other simulators:** X-Plane, FlightGear, any ForeFlight-UDP producer, or your own
  adapter through the open RB-SIM-IF protocol.

> **Not for flight.** Simulator builds of RB software display a permanent SIM annunciation.
> They are never installed on an aircraft. RB Simulator is not an approved flight
> simulation training device, and time on it is not loggable training.

## Status

Planning done; the first deliverable is the standalone replay simulator (October 2026). See:

- [docs/integration-targets.md](docs/integration-targets.md): the supported simulators and
  their interfaces;
- [docs/minimal-features.md](docs/minimal-features.md): what the first release does;
- [docs/protocol.md](docs/protocol.md): the RB-SIM-IF v1 protocol and requirements;
- [docs/plan.md](docs/plan.md): the implementation plan.

## Community

Website: https://www.rbavionics.com. Join the RB-Avionics Discord community for support.

## License

Community edition will be free for all builders and personal use, as defined by the
licensing model. Dual licensing for commercial agreement is available.

This project is licensed under a dual licence:

1. the GNU AGPLv3. See the [LICENSE](LICENSE) file for details.
2. a commercial agreement with XIAPROJECTS SRL.
