# V0.8 Architecture Delta (simulation-only)

## Quality gate pipeline

```
FlowEditor workspace -> static validation -> WorkflowQuality.runSuite
                       -> report (JSON) -> WorkflowRevisions.validate/publish (browser)
```

No step in the quality gate calls the actual runtime or any hardware. The gate is deterministic for a fixed flow, config, and set of typed scenarios. The report contains expected and observed model results, estimated wall time, warnings, structural errors and timestamps. Scenario instances are client-side demo data, not authoritative acceptance certificates.

## Version consistency

Workspace save includes `expectedActiveVersionId`. Draft source recorded at creation. At publish, server and browser check `sourceId === activeId`, rejecting stale drafts. SQLite active pointer and prior revision status change in a transaction. Browser localStorage is a demo implementation and is not cross-user concurrency storage. Legacy request without expected version is still temporarily accepted for compatibility; must be removed before production adoption.

## Evidence model

Browser mock captures node variable snapshots and per-node traces. C# engine exposes `CompletedTraces()` returning copied completed nodes, which is stored on the cycle and normalized to `run_node_traces`. No variable snapshots are available from the C# engine in this version. Legacy rows without `Trace` remain readable.

Schema v2 is additive and uses `CREATE TABLE IF NOT EXISTS`; it does not destructively rewrite legacy cycle rows. Each cycle write and its node traces are inserted in a SQLite transaction. Tests extract DDL and execute with actual Python sqlite3, but are **not** .NET integration tests.

## Performance measurement

Browser runtime observed `durationMs` forms cycle P50/P95. The sum of leaf traces groups by human-readable node label and represents cumulative node occupancy, not critical-path cycle wall time; parallel leaf durations are therefore not added to estimate wall time. Estimates from the dry-run use configured speed and nominal simulated durations and are displayed separately.

## Limitations and security

- C# .NET compilation unverified.
- No true multi-user server-authenticated concurrency UI; fake demo credentials.
- No hardware, no safety-rated controls.
- No actual race exploration or extensive model-based property testing.
- Server-side scenario-gate parity not implemented (current server protects static structure and workflow version conflicts).
- C# node traces have no variable snapshots; browser mock snapshots should not be treated as evidence from the server.
