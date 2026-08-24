# Authority & Lifecycle Reference

DogdouSpec maintains a strict, unbypassable boundary between technical task automation and product authority.

## Authority Matrix

| Domain | Technical Agent Authority | Product Owner Authority | Schema Dispositions |
| :--- | :--- | :--- | :--- |
| **Tasks & Code** | Autonomous: start/verify/complete tasks, update task acceptance criteria, capture context, and record findings/discussions. | Sets task priorities, reviews milestones. | `pending`, `in-progress`, `blocked`, `verification`, `done`, `transferred`, `superseded`, `cancelled` |
| **Requirements** | Propose new requirements (`status="proposed"`). Cannot approve, supersede, or withdraw. | Explicit owner disposition (`approved`, `superseded`, `withdrawn`) via `iteration confirm`. | `proposed`, `approved`, `superseded`, `withdrawn` |
| **Design Decisions** | Propose new design decisions (`status="proposed"`). Cannot accept, reject, or supersede. | Explicit owner disposition (`accepted`, `rejected`, `superseded`) via `iteration confirm`. | `proposed`, `accepted`, `rejected`, `superseded` |
| **Research Questions** | Open new research questions (`status="open"`). Cannot answer, defer, or withdraw. | Explicit owner disposition (`answered`, `deferred`, `withdrawn`) via `iteration confirm`. | `open`, `answered`, `deferred`, `withdrawn` |
| **Product Criteria** | Technical criteria on tasks. Cannot decide product criteria (`decision="pending"`). | Explicit owner decision (`accepted`, `rejected`, `waived`) via `iteration confirm`. | `pending`, `accepted`, `rejected`, `waived` |
| **Iteration Lifecycle** | Evaluate read-only readiness (`iteration readiness`). Never auto-complete iterations. | Explicit owner confirmation action via `iteration confirm`. | `draft`, `active`, `replanning`, `completed`, `cancelled`, `superseded` |

## Protected State Guards

The core engine enforces protected state rules during all low-level mutations (`append`, `transaction apply`):
- Attempting to set or change `iteration/@status` or `iteration/@completed_at` returns `OWNER_DECISION_REQUIRED` (exit code `5`).
- Attempting to add non-proposed requirements, non-proposed design decisions, non-open research questions, or non-pending product criteria returns `OWNER_DECISION_REQUIRED`.
- Attempting to modify decided requirements, design decisions, research questions, or acceptance criteria returns `OWNER_DECISION_REQUIRED`.
- Generic mutations cannot append confirmation records.

## Surprise & Replanning Protocol

When encountering unexpected technical findings or scope discrepancies during implementation:

1. **Local Planning Elaboration**:
   - If missing files, test fixtures, or auxiliary modules are required to fulfill an approved task objective, elaborate the task's repository scope (via `transaction apply`) and append a `discussion` record explaining the technical necessity.
   - Proceed with task execution.
2. **Material Product Divergence**:
   - If the implementation reveals that an approved requirement is impossible, needs new scope, or requires architectural changes, **STOP IMMEDIATELY**.
   - Append an active `finding` record (`status="active"`, `kind="finding"`) to the active task capturing the discovery.
   - Present the finding clearly to the human product owner and wait for explicit guidance.

## Iteration Readiness (`iteration readiness`)

`dogdouspec iteration readiness` is a deterministic, **read-only** assessment tool:

```powershell
.\dogdouspec.cmd iteration readiness --iteration "<ITERATION_ID>" --phase activation --format xml
.\dogdouspec.cmd iteration readiness --iteration "<ITERATION_ID>" --phase completion --format xml
```

- Reports `technically_ready="true|false"`, current revisions, passed/failed technical checks, and pending product decisions.
- `technically_ready="true"` indicates technical gating conditions are satisfied. It is a necessary prerequisite for owner review, **not** product acceptance.

## Iteration Confirmation (`iteration confirm`)

`iteration confirm` is the authoritative write command for lifecycle state changes and product approvals:

- Must only be invoked when the human product owner has given explicit instruction in the **current interaction**.
- The `actor` attribute records provenance (e.g. `actor="owner-instruction"`), not cryptographic identity.
- Every confirm request verifies expected document revisions, deep live-state consistency, and timestamp alignment.
- The command mutates and increments `spec.xml` only; `tasks.xml` is checked for revision/readiness consistency and remains byte-identical.

### Supported Confirmation Actions (`ConfirmationActionType`)

| Action | Transition / Effect | Description |
| :--- | :--- | :--- |
| `activate` | `draft` -> `active` | Activates a draft iteration; approves baseline requirements and accepts baseline design decisions. |
| `accept-design-change` | (active state preserved) | Formally accepts an updated or newly proposed design decision during active execution. |
| `replan` | `active` -> `replanning` | Moves active iteration into replanning to adjust requirements, architecture, or scope. |
| `continue` | `replanning` -> `active` | Resumes active iteration execution after owner replanning review. |
| `complete` | `active` -> `completed` | Marks iteration completed upon owner acceptance of product criteria. Sets `completed_at`. |
| `cancel` | `draft|active|replanning` -> `cancelled` | Cancels the iteration. |
| `supersede` | `draft|active|replanning` -> `superseded` | Supersedes the iteration with a successor iteration. |