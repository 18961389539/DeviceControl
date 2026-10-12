# V0.6 SQLite implementation and remaining verification

The V0.5 plan is partially implemented in `backend/DeviceControl.Api/SqliteStore.cs` and integrated into the V0.6 simulation host.

- `devicecontrol.db` created in `backend/DeviceControl.Api/data/` on the first C# run.
- `schema_version = 1`, WAL, busy timeout, foreign keys.
- Workflow/recipe revision snapshots and active version pointers.
- Production run and audit event records in SQLite.
- Initial idempotent import from historical `cycles.jsonl` / `audit.jsonl`; import of old `recipes.json` if SQLite has no active recipe.
- Atomic publication transaction at repository level. Software/runtime switch occurs only after committed publication, while the runtime is idle.

**Only the embedded SQLite DDL has been run and validated here.** The authoring environment has no .NET SDK, so the C# SQLite repository, imports, runtime API and NuGet dependency require CI compilation and integration tests. Legacy data corruption and multi-process scenarios are not hardened. The browser-only HTML uses an entirely different best-effort localStorage store and is not evidence of C# SQLite operation.
