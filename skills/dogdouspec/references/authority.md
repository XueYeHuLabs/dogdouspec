# Authority & Lifecycle Reference

DogdouSpec maintains a strict, unbypassable boundary between technical task automation and product authority.

## Authority Matrix

| Domain | Technical Agent Authority | Product Owner Authority | Schema Dispositions |
| :--- | :--- | :--- | :--- |
| **Tasks & Code** | Autonomous: start/verify/complete tasks, add/revise/split tasks, update acceptance criteria, capture context, and record findings/discussions. | Sets task priorities, reviews milestones. | `pending`, `in-progress`, `blocked`, `verification`, `done`, `transferred`, `superseded`, `cancelled` |
| **Requirements** | Propose new requirements (`status="proposed"` via `requirement propose` or `change propose`). Cannot approve, supersede, or withdraw. | Explicit owner disposition (`approved`, `superseded`, `withdrawn`) via `iteration confirm`. | `proposed`, `approved`, `superseded`, `withdrawn` |
| **Design Decisions** | Propose new design decisions (`status="proposed"`). Cannot accept, reject, or supersede. | Explicit owner disposition (`accepted`, `rejected`, `superseded`) via `iteration confirm`. | `proposed`, `accepted`, `rejected`, `superseded` |
| **Research Questions** | Open new research questions (`status="open"`). Cannot answer, defer, or withdraw. | Explicit owner disposition (`answered`, `deferred`, `withdrawn`) via `iteration confirm`. | `open`, `answered`, `deferred`, `withdrawn` |
| **Product Criteria** | Technical criteria on tasks. Cannot decide product criteria (`decision="pending"`). | Explicit owner decision (`accepted`, `rejected`, `waived`) via `iteration confirm`. | `pending`, `accepted`, `rejected`, `waived` |
| **Iteration Lifecycle** | Evaluate read-only readiness (`iteration readiness`). Never auto-complete or continue iterations. | Explicit owner confirmation action via `iteration confirm`. | `draft`, `active`, `replanning`, `completed`, `cancelled`, `superseded` |

## Protected State Guards

The core engine enforces protected state rules during all low-level mutations (`append`, `transaction apply`):
- Attempting to set or change `iteration/@status` or `iteration/@completed_at` returns `OWNER_DECISION_REQUIRED` (exit code `5`).
- Attempting to add non-proposed requirements, non-proposed design decisions, non-open research questions, or non-pending product criteria returns `OWNER_DECISION_REQUIRED`.
- Attempting to modify decided requirements, design decisions, research questions, or acceptance criteria returns `OWNER_DECISION_REQUIRED`.
- Generic mutations cannot append confirmation records.
- Tasks in terminal status (`done`, `transferred`, `superseded`, `cancelled`) are immutable; metadata edits or non-informational appends return `TASK_IMMUTABLE` (exit code `4`).
- Execution transitions (`start`, `resume`, `verify`, `complete`) require every task origin requirement to exist and be `approved`. Planning may still add pending tasks for proposed requirements during replanning.

## Surprise, Change, & Replanning Protocol

When encountering unexpected technical findings, requirements evolution, or scope changes during an iteration:

1. **Local Technical Elaboration**:
   - If constraints, test criteria, dependencies, or scope need elaboration for an existing task without changing product requirements, use `dogdouspec task revise`.
   - If a large task should be broken into multiple focused subtasks, use `dogdouspec task split`.
   - If a new technical task is needed for an existing approved requirement, use `dogdouspec task add`.

2. **Material Product Requirement / Architecture Change**:
   - If implementation reveals a missing requirement, an invalid requirement, or an architectural replan:
     1. Invoke `dogdouspec change propose` (or `requirement propose`) to attach an active finding record (`status="active"`, `kind="finding"`), freeze affected tasks to `status="blocked"`, and propose the new requirement in `spec.xml`.
     2. Stop execution progress and present the discovery to the product owner.
     3. The product owner instructs `dogdouspec iteration confirm` with `action="replan"`, approving/superseding/withdrawing requirements.
     4. During `status="replanning"`, technical execution is frozen (`ITERATION_REPLANNING_EXECUTION_FROZEN`). Use `dogdouspec change apply` to resolve active findings, supersede/transfer blocked tasks, and add successor tasks. A replacement requirement must include `sources/ref relation="supersedes"` to the old requirement.
     5. The product owner instructs `dogdouspec iteration confirm` with `action="continue"` to resume active execution.

## Continuation Gate Conditions (`action="continue"`)

When the product owner instructs `dogdouspec iteration confirm` with `action="continue"`, the engine verifies:
1. **No Proposed Requirements**: Every requirement in `spec.xml` has been decided (`approved`, `superseded`, or `withdrawn`). Returns `OWNER_DECISION_REQUIRED` if proposed requirements remain.
2. **No Active Findings**: No task in `tasks.xml` contains an unresolved active finding record (`kind="finding"` with `status="active"`). Returns `ITERATION_COMPLETION_PREDICATE_FAILED` if unresolved findings exist.
3. **Requirement Successor Alignment**: Every superseded requirement must have a finally approved successor with `sources/ref relation="supersedes"`; every such provenance target must itself be finally superseded; every non-terminal task origin must be finally approved; and an approved proposed successor must have non-terminal task coverage. Returns `REQUIREMENT_SUCCESSOR_MISSING` on any mismatch.

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
| `replan` | `active` -> `replanning` | Moves active iteration into replanning to adjust requirements, architecture, or scope. Freezes technical execution progress. |
| `continue` | `replanning` -> `active` | Resumes active iteration execution after verifying no proposed requirements, no active findings, and full task alignment. |
| `complete` | `active` -> `completed` | Marks iteration completed upon owner acceptance of product criteria. Sets `completed_at`. |
| `cancel` | `draft|active|replanning` -> `cancelled` | Cancels the iteration. |
| `supersede` | `draft|active|replanning` -> `superseded` | Supersedes the iteration with a successor iteration. |
