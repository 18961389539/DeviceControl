# V0.5 Workflow Runtime — Design notes

## Execution boundary

The DeviceRuntime machine state (`Manual`, `Auto`, `Running`, `Stopping`, `Fault`) remains the admission and recovery authority. FlowEngine has an independent software execution status (`Idle`, `Running`, `Paused`). A breakpoint does not change the machine state to `Idle`, so a new cycle cannot start while a debugging session is paused.

The workflow is defined by `station.json`, as a sequence of top-level nodes. A `Parallel` node has two to four child actions. Children begin concurrently, and a join waits for all children before the next top-level node. A failure signals cancellation to the other children. Running commands cooperate with cancellation; no unsafe process/thread kill is attempted.

## Logical resources

Leaf nodes acquire mutexes by symbolic names: `Axis:X`, `Axis:Y`, `Cylinder:Clamp`, and `Camera:Main`. Multiple independent axes can run simultaneously; the same name is never owned by two nodes simultaneously. Locks are always released in a `finally` block. This locking is about **application scheduling**, not protection against physical hazards.

V0.5 restricts leaf nodes to one physical resource each. Multi-resource composite nodes require an ordered lock-acquisition policy in a future version.

## Error policies

`Abort` propagates an exception to DeviceRuntime. `Retry` repeats an action at most `1 + maxRetries` times; `WaitForOperator` pauses with an error and requires an Engineer to select Retry or Abort. Only `WaitInput`, `VisionLocate` and `Delay` are retryable in this prototype. This prevents the software from accidentally re-issuing positioning and pneumatic actions automatically.

A debugging `pause` means **pause before the next top-level node**, not freeze an action already in progress. A `step` executes one top-level node, including all children of a Parallel group, and pauses at the next boundary. Breakpoints are in-memory and keyed by top-level index, so changing graph structure requires clearing breakpoints.

## Version and history

A cycle carries the fixed `flowVersion` from the config and the active recipe revision ID. Workflow release administration (draft -> validate -> publish -> rollback), content hashes, immutable snapshots, and migration of JSONL/JSON stores to SQLite have not yet been implemented.

## Security and validation

Backend `GET /api/flow` requires a session and debugger mutation `POST /api/flow/debug` requires Engineer or Admin. Browser controls mirror the server authorization but are not the security boundary. Credentials in this sample are intentionally demonstration-only and loopback-hosted. The system is not designed for industrial safety control.

## Next hardening tasks

1. Compile and run .NET 10 backend integration tests in a .NET-capable environment.
2. Implement workflow graph validation for full conditional/loop nodes, reference equality, resource hazards and cancellation coverage.
3. Put workflow versioning and run/audit/recipe data into transactional SQLite.
4. Persist operator confirmations as audit events and restrict retries to explicitly reviewed failure classifications.
5. Add scheduler soak tests, concurrency fuzz tests, exception injection, and deterministic virtual-time simulation.
