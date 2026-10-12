# V0.7 – Flow Editor Productivity and Read-only Replay

## Scope

Simulation-only software changes. No physical-device communication, no field bus,
no motion card SDKs and no machine-safety claims. V0.6 workflow validation,
publishing, rollback, command authorization, and flow runtime rules are preserved.

## Undo/redo and node duplication

`frontend/workflow-edit-history.js` contains a bounded `EditHistory` (80 states by
default). It stores full JSON snapshots of the **working draft only**, not the
published workflow, and clears redo on a new edit. Loading another revision or
resetting workspace creates a new history baseline. Every structural or property
mutation goes through `FlowEditor.mutate`.

`cloneFresh` recursively generates new IDs for a copied top-level node **and all
its branch children**. Each copied definition must still pass the existing
`validateFlow` before it can be saved. The demo clipboard lives inside the editor
and does not access the operating-system clipboard.

`diffFlows` compares the active Published definition to the current local
workspace by immutable node identity. It separately reports added/removed nodes,
attribute changes and index/parent changes. It is a structural diff, not a
semantic equivalence proof, and it does not resolve simultaneous multi-user edits.

## Read-only replay

The offline simulation workflow engine now records `variablesBefore` and
`variablesAfter` for each executed node: allowlisted simulated DI values, XY
positions, cylinder states, visual result and recipe X offset. A completed run
stores the execution traces alongside its existing step timing records.

`frontend/workflow-replay.js` projects those persisted-in-memory records into a
read-only step timeline. It NEVER calls the Runtime, and the replay UI has no
endpoint capable of dispatching IO, axis or cylinder commands. It uses saved
per-node variables only. If a run contains only aggregate step timing data,
variables appear as **unavailable**; values from the current machine are never
used to fill historical gaps.

**Limitation:** the C# CycleRecord / SQLite run-history schema does not yet store
per-node variable snapshots. In server-backed mode, the replay UI falls back to
step-only records until the C# trace persistence contract is implemented. In the
standalone browser demo, historical data lives in memory for this page session;
revision records may persist in localStorage, but run history does not.

## SQLite maintenance

`tools/sqlite_check_backup.py` is a separate Python maintenance CLI. It first
checks `integrity_check` and `foreign_key_check` and then makes a consistent copy
using SQLite's online backup API. It refuses to overwrite an existing file and
performs post-backup integrity checks. The tool does not alter the live source,
is not scheduled, and is not yet integrated with C# runtime lifecycle.

`tests/test_sqlite_reliability.py` uses **the DDL extracted from actual C# source**
and then tests transaction rollback on a failed FK change, an online backup from
an open WAL database, backup verification and restore. It does NOT compile or
invoke Microsoft.Data.Sqlite, and does not constitute a C# migration CI suite.

## Known gaps / suggested V0.8

- Compile C# and execute real API/database integration tests with .NET 10 CI.
- Add server CycleRecord trace snapshots with schema migration and bounded size.
- Database retention, integrity checks at startup, deployment rollback plan.
- Workflow editor local drafts / conflict detection / approval policies.
- Workflow-level deterministic regression tests using saved event fixtures.
- Improve canvas navigation, large-flow performance, accessibility and keyboard focus.
