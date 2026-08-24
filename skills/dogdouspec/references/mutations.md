# Mutation Operations Reference

DogdouSpec enforces single-source-of-truth document integrity through structured, schema-validated mutations. Direct file editing of `.dogdouspec/*.xml` is prohibited.

## Mutation Decision Matrix

| Operation | Command | Primary Use Case | Concurrency & Idempotency |
| :--- | :--- | :--- | :--- |
| **Task Update** | `dogdouspec task update` | Task state machine transitions (`start`, `verify`, `complete`, etc.), acceptance criteria updates, context snapshots, active record resolution, and appending execution records. | Single-document atomic commit. Persists durable `operation_id` receipts. Replays are deeply verified for idempotent success. Execution transitions fail closed when iteration is `replanning`. |
| **Task Add** | `dogdouspec task add` | Appending a new pending task referencing an existing requirement in `spec.xml`. | Single-document atomic commit. Revision-checked. Verified origin reference. Durable `operation_id` stamping. |
| **Task Quick** | `dogdouspec task quick` | Compact bounded work intended to execute now. Inputs expand to a normal Task; no second task type or file exists. | `--start` creates the final in-progress Task, start history, and receipt in exactly one `tasks.xml` revision. `--dry-run` writes nothing. |
| **Task Revise** | `dogdouspec task revise` | Elaborating constraints, dependencies, acceptance criteria, or scope on active/pending tasks. A started task cannot replace rationale and may only expand scope. Rejects terminal tasks (`TASK_IMMUTABLE`). | Single-document atomic commit. Revision-checked. Durable `operation_id` stamping. |
| **Task Split** | `dogdouspec task split` | Transitioning a parent task to a terminal disposition (`superseded`/`transferred`/`cancelled`) and atomically adding 2+ pending subtasks. | Single-document atomic commit. Revision-checked. Durable `operation_id` stamping. |
| **Requirement Propose** | `dogdouspec requirement propose` | Proposing a new requirement with `status="proposed"`. Rejects non-proposed statuses (`OWNER_DECISION_REQUIRED`). | Single-document atomic commit to `spec.xml`. Revision-checked. Durable `operation_id` stamping. |
| **Change Propose** | `dogdouspec change propose` | Attaching one or more active finding receipts to tasks, freezing target tasks to `blocked`, and proposing requirements across documents. | 2-document atomic commit (`spec.xml` + `tasks.xml`). Requires `active` iteration status. Immediate identical replay is durable; later revision drift is rejected. |
| **Change Apply** | `dogdouspec change apply` | Resolving active findings, setting terminal task dispositions, and adding successor tasks during `status="replanning"`. | Recovery-backed commit to `tasks.xml`; a deterministic informational receipt is appended to the first impacted task. No-op application is rejected; immediate identical replay is durable. |
| **Generic Append** | `dogdouspec append` | Appending a single valid child element to a managed document container (e.g. adding knowledge entries or backlog items). | Single-document atomic commit. Revision-checked. Performs identity-based deduplication for elements with unique `@id`. |
| **Transaction Apply** | `dogdouspec transaction apply` | Low-level multi-document escape hatch, structural replacements, batch initialization. | Multi-document staged publish with crash recovery. Correlated by `operation_id`. Non-durable (no separate business receipt container); stale retry conflicts if revision advanced. |
| **Iteration Confirm** | `dogdouspec iteration confirm` | Owner-gated product decisions: iteration activation, design change acceptance, replanning, continuation, completion, cancellation, or supersession. | Protected single-document write to `spec.xml`. Reads `tasks.xml` for consistency/readiness verification without modifying it. Requires current-interaction human owner instruction. |

## Terminal Task Immutability

Tasks in `done`, `transferred`, `superseded`, or `cancelled` statuses represent historical facts:
- Attempts to transition terminal tasks or modify their acceptance criteria, constraints, or scope return `TASK_IMMUTABLE` (exit code `4`).
- Attempts to append non-informational records (`completion`, `start`, or active `finding` records) return `TASK_IMMUTABLE`.
- Appending informational discussion or handoff records to terminal tasks remains permitted.

## Replanning Execution Freeze

When an iteration enters `status="replanning"`:
- Technical execution progress commands (`task update` with `transition="start"`, `"resume"`, `"verify"`, or `"complete"`) fail closed with `ITERATION_REPLANNING_EXECUTION_FROZEN` (exit code `5`).
- Planning and disposition operations (`task add`, `task split`, `task update` with `transition="supersede"|"transfer"|"cancel"`, `change apply`) remain enabled so agents can construct the new plan.

Generated operation receipts store a canonical-XML `request-sha256` value and readable reasons. This is only an idempotency fingerprint: it is not a signature, evidence hash, or authentication mechanism.

## Time and Input Bounds

The task/change/requirement helper requests are rejected before parsing when their UTF-8 payload exceeds the configured XML document limit; any managed document they read is checked against the same bound before it is opened. A write request cannot backdate a modified task or document: `occurred_at` must be at least its current `updated_at`. New tasks are pending except `task quick --start`, which atomically creates a normal in-progress task with `created_at=updated_at=started_at` and a start record.

## Document Revisions

Every managed document root contains an authoritative `revision="N"` attribute owned by the engine.

- All mutating CLI commands require the caller to provide `--expected-revision <N>`, `--expected-spec-revision <N>`, or `--expected-tasks-revision <N>`.
- If the document has been modified concurrently (`actualRevision != expectedRevision`), the command fails with `REVISION_CONFLICT` (exit code `4`).
- Callers must re-query the current document state and recalculate the mutation.

## Multi-Document Transaction Visibility & Filesystem Semantics

As specified in `docs/V1_CLI_CONTRACT.md` (Section 11):

1. **Project Locking**: Writers acquire a single `.dogdouspec` project lock. Readers do not acquire the writer lock.
2. **Individual Whole-File Atomic Replacements**: Writers stage complete replacement files in a workspace-local temporary directory, flush them, and replace target files individually via atomic rename operations.
3. **Unlocked Reader Visibility During Publish**: During a multi-document publish across multiple files, concurrent readers may temporarily observe a mix of complete old and complete new revisions across different files. Readers always observe a complete valid document (never a partial or corrupted file). Simultaneous multi-file visibility across the entire filesystem is not claimed.
4. **Crash Recovery Convergence**: If a process terminates mid-publish, a minimal recovery marker in the CLI temporary area allows startup recovery to complete or roll back the prepared transaction to a single valid set before serving any subsequent write.
5. **No Success on Partial Commit**: The engine never returns success on a partial commit.

## Request XML Templates

Use `dogdouspec template show --name <NAME>` to view exact XML templates for public requests:

- `task.update`: Task update request template.
- `task.add`: Task add request template.
- `task.revise`: Task revise request template.
- `task.split`: Task split request template.
- `requirement.propose`: Requirement propose request template.
- `change.propose`: Mid-flight change propose request template.
- `change.apply`: Replanned change apply request template.
- `transaction.apply`: Multi-document transaction request template.
- `iteration.confirmation`: Iteration confirmation request template.
- `record.discussion`: Discussion record template for task records.
- `record.finding`: Finding record template for task records.
- `record.verification`: Verification record template for task records.
- `knowledge.entry`: Knowledge entry template for `knowledge.xml`.
- `backlog.item`: Backlog item template for `backlog.xml`.

Example:
```powershell
.\dogdouspec.cmd template show --name task.add
.\dogdouspec.cmd template show --name change.propose
.\dogdouspec.cmd template show --name change.apply
```
