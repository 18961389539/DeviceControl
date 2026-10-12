# V0.6 Workflow Studio Design and Constraints

## Execution contract

The saved workflow revision is a structurally constrained graph: a sequential ordered list of root nodes. Each root may be a leaf, a `Parallel` fan-out/fan-in (2–4 leaves), or a `Condition` fan-out with TRUE / FALSE leaves. No other nested branching is allowed.

`Parallel` executes concurrently but each leaf uses the FlowEngine's logical resource mutex. It does not imply deterministic low-level timing. `Condition` reads a simulation boolean input once and executes exactly one child. If that child fails, the parent fails. Retry is allowed only for `WaitInput`, `VisionLocate`, and `Delay`, where the author must ensure idempotent behavior.

The browser editor's connectors are **generated** by ordered root nodes and explicit branch-child containment; it is not an unrestricted graph-wiring system. Editing an active revision only modifies the local workspace; Save creates a new immutable draft. Only validated revisions can be published. `ActiveFlow` and `DeviceRuntime` switch together under the device's publish guard.

## Data lifecycle

The C# SQLite class initializes `schema_version=1`, creates version/run/audit/recipe tables, and enforces referential relationships. Workflow publish and rollback run in a SQLite transaction with previous revision superseded and new revision activated. Runtime is switched when the storage operation succeeds. Each run carries its flow revision and recipe revision.

Existing legacy `cycles.jsonl` and `audit.jsonl` are read and inserted by record ID (`INSERT OR IGNORE`). `recipes.json` is migrated into SQLite when the recipe table has no active record. No legacy files are deleted. The standalone browser uses localStorage best-effort, not SQLite; do not equate these storage guarantees.

## Scope / future hardening

1. Build and run actual C# and NuGet CI integration tests. Current C# repository and host code is uncompiled in this environment.
2. Make migrations transactional and versioned for future schema changes; cover read-only databases, checksum/import interruptions, WAL recovery, and backup/restore.
3. Add editable visual edges, branch expression grammar, more than one branch layer, undo/redo, diff and merge only after robust graph validation.
4. Implement version compare and production history queries with pagination and retention; durable replay and compensations should be designed separately from process safety.
5. Harden access control with proper identities, TLS if remotely exposed, secret management, authorization tests, and trusted audit sinks. Demo credentials are strictly for offline simulation.
