# DeviceControl V2.0 RC — Release acceptance matrix

Version: 2.0-rc. Scope: software simulation only; excludes actual hardware, actuators, safety certification.

| Gate | Evidence this run | Release decision |
|---|---|---|
| Node runtime, workflow, governance, replay, stations, resources | 13 distinct PASS entries in first offline run | PASS (selected scope) |
| SQLite schema, migration, WAL/backup, integrity | five individual scripts returned 0 | PASS |
| Recovery v20 provenance fixture | controlled cross-station backup and symlink failures rejected; original untouched | PASS |
| Browser isolated sandbox V1.9 | Chromium scenario returned 0 | PASS |
| Browser recovery drills V1.8 | Chromium scenario returned 0 | PASS |
| Browser maintenance V1.7 | Chromium scenario returned 0 | PASS |
| Browser phased rollout V1.6 | Chromium scenario returned 0 | PASS |
| Full uninterrupted offline suite | aborted/exceeded execution window before completion; old version assertions patched | UNVERIFIED |
| .NET 10 compilation of backend | dotnet not installed; installation host unavailable | BLOCKED |
| .NET contract runner | cannot run without SDK | BLOCKED |
| HTTP authentication, two-station upgrades, restart recovery | cannot run without SDK | BLOCKED |

**Release approved: NO.** Do not tag V2.0 GA/stable until full release profile returns 0 with zero blocked/failed tests in CI and an explicit reviewed report.

Exact commands:

```bash
python tools/release_gate_v20.py --profile offline
python tools/release_gate_v20.py --profile release
```

The full release profile includes `dotnet build -c Release`, the .NET contract runner, the Python live HTTP suites, and Chromium smoke. Each case has its own bounded execution, log, and JSON status. A fast targeted offline run is diagnostics only and must never set `releaseApproved = true`.

Known technical debt:
- JSON deployment journal updates and separate workflow/recipe SQL publishing are not a cross-store atomic transaction. Operators must review interrupted upgrades manually.
- Journal SHA-256 checks corruption, not an authenticated origin guarantee against privileged filesystem attackers.
- A source-only C# check does not prove successful compilation. CI must be executed in an environment with SDK and NuGet access.
