# DogdouSpec v1 Skill Workflow

Status: Normative workflow contract

This document defines how an Agent uses the v1 CLI. A concrete environment
Skill may adapt command invocation and prompt wording, but must preserve these
reads, writes, authority gates, and stop conditions.

The checked-in repository implementation of this skill is located at
[`.agents/skills/dogdouspec/SKILL.md`](../.agents/skills/dogdouspec/SKILL.md), with bootstrap
entry guidance for coding agents in [`AGENTS.md`](../AGENTS.md).

The Skill has no hidden project state. Managed XML, the repository, and the
current user interaction are its only authorities.

## 1. Bootstrap

From any repository subdirectory:

```powershell
dogdouspec workspace discover --format xml
dogdouspec iteration list --format xml
```

If discovery fails, the Skill reports that no workspace exists. It does not
initialize unless the user requested initialization or project setup.

The directory listing is the coarse Work index. The Skill does not require a
project catalog.

## 2. Select a Work directory

Selection order:

1. An explicit Iteration ID from the user or current handoff.
2. A uniquely matching normalized directory name.
3. Index search by component, topic, kind, or tag.
4. A full-text discovery search only when structured lookup is insufficient.

The Skill never silently chooses between multiple plausible active Iterations.

After selecting a Work directory, read only its compact SPEC surface:

```xpath
ds:filter(/iteration, '@id', '@kind', '@status', 'index')
```

Then read product or research indexes as needed. Large Task and record bodies
remain unloaded.

## 3. Select actionable Task state

First query the compact active Task index:

```xpath
ds:filter(
  //task[@status='in-progress' or @status='verification'][1],
  '@id',
  '@status',
  '@agent',
  'index'
)
```

Task selection is derived each time:

1. Resume the first `in-progress` or `verification` Task.
2. If that query is empty, invoke the public read-only helper:
   ```powershell
   dogdouspec task next --iteration "<ITERATION_ID>" --format xml
   ```
   It selects the first ready `pending` Task only after resolving dependency
   state across document, iteration, and project boundaries. A local XPath
   pending-task expression cannot prove cross-document readiness and must not
   substitute for this helper.
3. Report `blocked` Tasks separately.
4. Stop the automatic Task loop when no actionable Task remains.

There is no persisted next-Task pointer.

After selecting one Task, load the complete Task by ID:

```xpath
//task[@id=$task_id]
```

Do not load unrelated Task bodies unless a verified reference or investigation
requires them.

## 4. Understand before editing

Before repository edits, the Skill must identify:

- Task objective and rationale.
- Scope and exclusions.
- Origin Requirement or research question.
- Dependencies.
- Constraints and technical acceptance.
- Key points and current context snapshot.
- Active questions, findings, and previous attempts.
- Applicable project knowledge discovered through explicit index terms or
  references.
- Product and authority stop conditions.

If the Task lacks enough context, append a question or discussion record rather
than guessing a product decision.

## 5. Template-first recording

For a new durable record:

1. Request the closest template.
2. Replace all example identities, timestamps, index terms, and content.
3. Remove irrelevant optional sections.
4. Validate the fragment.
5. Append it with Task/document ID and expected revision.

Example:

```powershell
dogdouspec template show --name record.discussion --version 1.0

dogdouspec append `
  --document 20260823-xpath-core/tasks.xml `
  --parent-xpath "//task[@id=$task_id]/records" `
  --var task_id=20260823-task-xpath-projection `
  --expected-revision 8 `
  --stdin
```

The Skill does not hand-author low-level transaction XML when a template or
Task update can express the change.

## 6. Discussion summary quality

Conclusions without reasoning are not sufficient handoff context. A durable
discussion or decision summary records:

- Trigger and relevant prior assumption.
- Question or disagreement.
- Material options considered.
- Why important rejected options were rejected.
- Selected outcome and rationale.
- Remaining uncertainty or follow-up.
- Relevant index terms and stable references.

Do not persist greetings, repetition, brainstorming noise, or the complete raw
conversation. Preserve the reasoning needed to avoid repeating the discussion.

When a discussion changes product meaning, the Agent records the proposal and
stops at the owner confirmation gate. It must not label its own conclusion as an
accepted product decision.

## 7. Technical Task updates

Use `task update` when one logical action combines record append and current
Task state:

```powershell
dogdouspec task update `
  --iteration ID `
  --task TASK_ID `
  --expected-revision N `
  (--stdin | --file PATH) `
  [--workspace-root PATH] `
  [--format xml|human]
```

- **Start**: transition `start` (`pending` -> `in-progress`), sets `started_at` and `updated_at`.
- **Block**: transition `block` (`in-progress` | `verification` -> `blocked`), appends Finding/blocker record.
- **Resume**: transition `resume` (`blocked` -> `in-progress`), resolves blocking records.
- **Verify**: transition `verify` (`in-progress` -> `verification`), updates criteria results to `passed`.
- **Review, when required**: submit `task review` in `verification`. Approval actor must differ from Task `@agent`. `changes-requested` appends an active finding and moves the Task back to `in-progress`; resolve it, verify again, and obtain fresh approval.
- **Complete**: transition `complete` (`verification` -> `done`), sets `completed_at`, appends completion record.
- **Transfer / Supersede / Cancel**: transition `transfer`, `supersede`, or `cancel`.
- **State-preserving record append / context update**: omit `transition`.

The Skill supplies one request `@id` and one or more `<record>` elements. Every
appended record is automatically stamped with `operation_id="task-update/@id"`.

On retry, the Skill submits the identical request XML with either the original
pre-commit expected revision or the current revision; the CLI recognizes the
already-applied receipt from the Task records and returns `already_applied="true"`.
The Skill never generates new operation IDs merely because a response was lost.

Task review actor separation is provenance only. It does not authenticate the
actor string or replace repository identity, branch protection, or CI policy.

A revision conflict (`REVISION_CONFLICT`, exit 4) means the source changed
concurrently. The Skill reloads the Task state and revision, reconciles changes,
and submits a new request with updated revision and a new operation ID.

### 7.1 Low-level multi-document escape hatch

Use `transaction apply` only when one logical change must atomically touch
multiple managed documents or no specific v1 helper expresses the patch:

```powershell
dogdouspec transaction apply `
  --file prepared.transaction.xml `
  --workspace-root PATH `
  --format xml
```

Before invoking it, the Skill loads the request schema, supplies exact document
revisions, uses variables for repeated selectors, and assigns `expect` to every
mutating XPath. Operations are sequential within each document. Projection
functions are for reading only and must not be used as mutation addresses. The
Skill never edits root `revision`, writes `operation_id` into a payload, changes
a durable Task receipt, or attempts to change protected product decisions.

Start from `dogdouspec template show --name transaction.apply` rather than
composing a
large request from an empty stdin buffer. Save the returned XML to a temporary
file, replace its explicit placeholders, inspect it, and then pass it with
`--file`.

Unlike `task update`, a low-level transaction has no durable managed receipt.
After an uncertain response, the Skill reloads all target revisions and content.
It may resubmit only when the exact current-revision request is a semantic no-op;
a stale pre-commit retry is a conflict and must be reconciled.

## 8. Index maintenance

When creating or materially changing an indexed node:

1. Write a one-sentence `summary` that is meaningful without the body.
2. Add the smallest stable set of global dimensions.
3. Add `tag` terms for useful project-local concepts.
4. Avoid synonyms that do not improve future selection.
5. Do not use priority as a substitute for Task order or dependency state.

Primary lookup:

```xpath
//*[@id and index/term[@key=$key and @value=$value]]
```

Fallback discovery:

```xpath
//*[contains(string(.), $text)]
```

After a fallback repeatedly finds the same concept, update the relevant index.
The CLI validates term syntax; the Skill owns semantic index quality.

## 9. Unexpected-problem protocol

An implementation surprise first becomes a Task-local Finding record. The
Agent records observed fact, triggering attempt, impact, and relevant indexes
before deciding its disposition.

| Condition | Action |
|---|---|
| Local implementation detail; Requirement and accepted design remain valid | Add or refine technical work inside the current Task |
| Work is required by current Task acceptance but is independently executable | Add a new Task or split the Task, retaining origin and dependency context |
| Answer is unknown and blocks design selection | Create a Research Work directory and block the originating Task with one reference |
| Requirement, product acceptance, or accepted material design may change | Keep affected Tasks blocked; prepare discussion and confirmation input; stop for Owner |
| Issue does not block current acceptance | Propose a backlog item with impact and review condition |
| Existing Iteration is no longer a useful execution boundary | Propose a successor Iteration; do not erase the current one |

An Agent may recommend a disposition. Product change, risk acceptance,
deferral of required work, supersession, and Iteration completion remain owner
decisions.

### 9.1 Governance boundaries

A Task exclusion is a current-scope boundary, not a deferred obligation or
implicit risk acceptance. Create a backlog item only when a credible,
non-blocking obligation remains: it must state its source, impact, why it is
outside current acceptance, and a review or scheduling condition. Do not add a
backlog item for a resolved concern, a purely excluded path, or an unknown
future possibility. Deferring required work or accepting product risk remains
an owner decision.

Use the public `backlog add`, `backlog list`, and
`backlog schedule|complete|cancel` helpers with the exact `backlog.xml`
revision and stable operation ID. Defects record `kind=defect`, severity,
source iteration or Task, impact, and a target iteration or review condition.
An optional resolving Task is recorded as backlog evidence only; it does not
rewrite Task origin authority.

Knowledge is optional and belongs in `knowledge.xml` only for a stable,
reusable fact that is likely to affect later Tasks or Iterations and has a
stated source. Command transcripts, one-off attempts, and Task-local status do
not qualify.

`context/design_snapshot` is optional. Add it only when a later executor or
verifier needs concise Task-local technical context to resume safely: the
chosen approach, its constraint, and the next consequence. Do not add it as
handoff or verification boilerplate, and do not restate the Task. A material
choice that changes product behavior, an external/compatibility or security
boundary, or materially constrains other Tasks requires a formal proposed
design decision and owner disposition; a snapshot is never a substitute.

## 10. Product review gates

The automatic loop stops before:

- Activating a draft Iteration.
- Approving, superseding, or withdrawing a Requirement.
- Finalizing a Research question disposition that affects product direction.
- Accepting or rejecting a material design decision.
- Waiving product acceptance.
- Deferring currently required work.
- Replanning, cancelling, or superseding an Iteration.
- Completing an Iteration.

At a gate, the Skill:

1. Runs `validate` and read-only `iteration readiness` when applicable.
2. Queries only the affected product/decision surface and supporting records.
3. Presents outcome, reasoning, alternatives, technical evidence, risks, and
   unresolved points.
4. Waits for explicit owner instruction.
5. Uses `iteration confirm` only after that instruction.

`actor="owner"` is provenance, not authentication. The Skill does not fabricate
an owner instruction.

## 11. Iteration completion

When no actionable Task remains, the Skill does not conclude that the Iteration
is complete. It runs:

```powershell
dogdouspec iteration readiness `
  --iteration 20260823-xpath-core `
  --phase completion `
  --format xml
```

If technical readiness is false, it reports the exact remaining Tasks,
Findings, coverage gaps, or invalid references and resumes only actionable
technical work.

If ready for product review, it presents every pending product criterion and
the records that cover it, then stops. Owner rejection or replanning creates new
work; it is not converted into technical success automatically.

## 12. Validation and handoff

After each successful mutation:

1. Read the returned revision.
2. Run scoped validation for the changed Work.
3. Re-query the compact selected node to confirm current state.
4. Continue the Task loop or stop at the relevant gate.

Before handing off, append a handoff record when the Task remains non-terminal.
It summarizes current state, last successful action, failed attempts, next
technical step, and stop conditions. It does not duplicate the complete Task.
Add `design_snapshot` only when those fields cannot preserve material technical
context needed for safe resumption or verification; it is not mandatory
handoff boilerplate.

## 13. Prohibited Skill behavior

The Skill must not:

- Directly edit managed XML with a text editor.
- Use full-text search as the normal Task selector.
- Load all Task bodies by default.
- Persist a global next-Task pointer.
- Treat a record as product approval.
- Invoke `iteration confirm` automatically.
- Retry a failed append with a new ID before checking whether the old ID
  committed.
- Use raw transaction XML when a v1 helper expresses the operation.
- Convert unresolved current acceptance into backlog merely to complete work.
