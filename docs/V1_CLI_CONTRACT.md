# DogdouSpec v1 CLI Contract

Status: Normative implementation contract

## Quick Task Entry

`task quick` accepts `--title`, repeatable `--scope`, `--done-when`, `--why`, optional repeatable `--origin`, `--depends-on`, and `--term key=value`, plus `--iteration`, `--expected-revision`, `--start`, and `--dry-run`. It auto-selects exactly one active iteration when omitted. Requirement origins are iteration `implements` references; no origin generates exactly one current-iteration `supports` origin for operational work. Operational tasks never satisfy product Requirement coverage, but remain normal non-terminal readiness blockers.

`--start` is one writer transaction and one tasks revision: the stored Task is already `in-progress`, has equal create/update/start timestamps, generated start history, and durable creation receipt. `--dry-run` returns the generated task-add representation and leaves documents, revisions, and transaction temporary state unchanged.

To make a write replayable, supply both `--id` and `--operation-id`; the operation ID must begin with a UTC `YYYYMMDDTHHmmssZ` timestamp, which becomes the generated request time. Dry-run emits canonical XML with `--format xml`; `--format human` emits a stable preview summary and directs callers to XML for the request.

This document defines the first usable repository-local CLI. XML is the
machine-facing request and result format. The CLI is a secure XML/XPath engine
with a small set of schema-aware atomic helpers; it is not an autonomous project
manager.

## 1. Invocation and discovery

The repository wrapper is:

```text
dogdouspec.cmd
```

It invokes the pinned local executable without global installation. From the
working directory, commands walk ancestors to the nearest `.dogdouspec`
directory. `--workspace-root` overrides discovery and must name that exact
directory or its parent project directory.

All persisted document paths in commands and results are normalized `/`
separated paths relative to `.dogdouspec`:

```text
20260823-xpath-core/spec.xml
20260823-xpath-core/tasks.xml
knowledge.xml
backlog.xml
```

Absolute document paths, `..`, alternate data streams, device paths, and paths
escaping through symlinks or junctions are rejected.

## 2. v1 command surface

```text
dogdouspec workspace discover
dogdouspec workspace init

dogdouspec iteration list
dogdouspec iteration create
dogdouspec iteration readiness
dogdouspec iteration confirm --stdin|--file

dogdouspec query
dogdouspec search
dogdouspec validate

dogdouspec schema show
dogdouspec template show
dogdouspec append --stdin|--file
dogdouspec task update --stdin|--file
dogdouspec task review --stdin|--file
dogdouspec task add --stdin|--file
dogdouspec task quick
dogdouspec task revise --stdin|--file
dogdouspec task split --stdin|--file
dogdouspec task next
dogdouspec task scope
dogdouspec requirement propose --stdin|--file
dogdouspec change propose --stdin|--file
dogdouspec change apply --stdin|--file
dogdouspec backlog add|list|schedule|complete|cancel
dogdouspec transaction apply --stdin|--file
```

`transaction apply` is the low-level escape hatch. The Skill normally uses
templates, `task quick`, `task add/revise/split`, `requirement propose`, `change propose/apply`, `append`, `task update`, and `iteration confirm`.

Every mutating command identifies itself as mutating in help output and requires
an expected revision for each existing document it may change.

### 2.1 Read-only task selection and scope verification

`task next` derives one actionable Task without writing managed state:

```powershell
.\dogdouspec.cmd task next --iteration "<ITERATION_ID>" --format xml
```

It returns the first `in-progress` or `verification` Task in document order. If
none exists, it returns the first `pending` Task whose `depends-on` references
resolve to terminal Tasks across the declared `document`, `iteration`, or
`project` boundary. A local XPath pending-task expression cannot establish
cross-document dependency readiness and must not substitute for this helper.

`task scope` compares changed repository-relative paths with a selected Task's
declared `<scope>` and writes nothing:

```powershell
.\dogdouspec.cmd task scope --iteration "<ITERATION_ID>" --task "<TASK_ID>" --path src/DogdouSpec.Core/Tasks/TaskUpdater.cs --format xml
.\dogdouspec.cmd task scope --iteration "<ITERATION_ID>" --task "<TASK_ID>" --git-ref HEAD --format xml
.\dogdouspec.cmd task scope --iteration "<ITERATION_ID>" --task "<TASK_ID>" --git-range main...HEAD --format xml
```

Exactly one input source is required. `--path` is repeatable and names concrete
changed paths. `--git-ref REV` resolves `REV` to one commit and compares that
commit to the current index and working tree; it reports tracked changes only
and deliberately excludes untracked files. `--git-range A..B` uses Git's
two-endpoint diff and `A...B` uses Git's merge-base diff. The command resolves
each user revision to a commit before diffing, accepts neither option-like
revisions nor malformed ranges, uses `--name-only -z --no-renames`, and bounds
its read-only Git child process to 30 seconds. External diff drivers and text
conversion filters are explicitly disabled with `--no-ext-diff --no-textconv`.

A scope repository `path` is a literal repository-relative base (`.` is the
root). Includes and excludes are evaluated relative to that base. `/` and `\`
normalize to `/`; redundant `.` components normalize away; `*` matches within
one segment, `?` one non-separator character, and `**` zero or more complete
path segments. `.` and `**` include every concrete repository path. Use
`directory/**` for recursive directories; a trailing slash is not a recursive
shorthand. On Windows matching is case-insensitive; elsewhere it is ordinal
case-sensitive. If any applicable repository block excludes a path, that
exclusion wins globally over every include. Absolute, traversal, device, ADS,
control-character, and wildcard changed-path inputs are rejected. Output lists
are ordinal-sorted.

A valid scope report exits 0. A completed report containing one or more
out-of-scope paths exits 1; argument, Git, XML, and workspace errors retain
their normal diagnostic exit codes.

### 2.2 Backlog lifecycle helpers

`backlog add`, `schedule`, `complete`, and `cancel` are bounded mutating helpers;
`backlog list` is read-only. Every mutation requires the exact positive
`backlog.xml` revision plus stable `--id`, `--operation-id`, `--actor`, and
`--occurred-at` values. An identical retry at the current or immediately prior
revision returns `already_applied="true"`; changed payload, reused operation ID,
or stale unrelated revision fails closed.

```powershell
.\dogdouspec.cmd backlog add --id <ITEM_ID> --operation-id <OP_ID> --actor <ACTOR> --occurred-at <ISO_TIME> --kind defect --severity p1 --summary <TEXT> --statement <TEXT> --rationale <TEXT> --impact <TEXT> --source-iteration <ITERATION_ID> --source-task <TASK_ID> --review-condition <TEXT> --expected-revision <REV> --format xml
.\dogdouspec.cmd backlog list --status open --kind defect --severity p1 --format xml
.\dogdouspec.cmd backlog schedule --id <ITEM_ID> --operation-id <OP_ID> --actor <ACTOR> --occurred-at <ISO_TIME> --resolving-task <TASK_ID> --expected-revision <REV> --format xml
.\dogdouspec.cmd backlog complete --id <ITEM_ID> --operation-id <OP_ID> --actor <ACTOR> --occurred-at <ISO_TIME> --resolving-task <TASK_ID> --expected-revision <REV> --format xml
.\dogdouspec.cmd backlog cancel --id <ITEM_ID> --operation-id <OP_ID> --actor <ACTOR> --occurred-at <ISO_TIME> --expected-revision <REV> --format xml
```

Every item requires a source iteration or Task, non-empty impact, and exactly
one target iteration or review condition. Defect items additionally require a
`kind=defect` index term and severity `p0` through `p3`. Scheduling permits only
`open -> scheduled`; completion and cancellation permit `open|scheduled` to a
terminal state. Terminal items are immutable. `--resolving-task` writes a
`resolved-by` reference on the backlog receipt as traceable evidence; it never
changes Task origin or any `tasks.xml` document. Actor separation is recorded
provenance, not authenticated identity.

## 3. Result and diagnostic envelope

V1 formats are `xml` and `human`. `xml` is the default when stdout is not an
interactive terminal. Skills explicitly request `--format xml`.

A successful mutation returns only changed documents and idempotency state:

```xml
<mutation command="task update" already_applied="false">
  <document
    path="20260823-xpath-core/tasks.xml"
    previous_revision="12"
    revision="13"/>
</mutation>
```

Failures return diagnostics on stderr:

```xml
<diagnostics command="append">
  <diagnostic
    code="REVISION_CONFLICT"
    severity="error"
    document="20260823-xpath-core/tasks.xml"
    expected_revision="12"
    actual_revision="13">
    Reload the target document and rebuild the update.
  </diagnostic>
</diagnostics>
```

Exit codes:

| Code | Meaning |
|---:|---|
| 0 | Success, including a verified idempotent retry |
| 1 | Read-only scope verification completed with one or more out-of-scope paths |
| 2 | Command, XML request, XPath, or argument error |
| 3 | Schema or semantic validation failure |
| 4 | Revision, lock, cardinality, or idempotency conflict |
| 5 | Protected product decision or authority gate |
| 6 | Filesystem commit or recovery failure |
| 7 | Input, query, projection, or output limit exceeded |

Stable codes are more important than prose wording.

## 4. Workspace and Iteration commands

### 4.1 Discover

```powershell
dogdouspec workspace discover --format xml
```

```xml
<workspace root="L:/dogdou/dogdouspec/.dogdouspec"/>
```

Discovery performs no write.

### 4.2 Initialize

```powershell
dogdouspec workspace init --format xml
```

Initialization creates `.dogdouspec`, `_schema`, `_skill`, `knowledge.xml`, and
`backlog.xml` atomically. It fails without mutation if managed state already
exists. `_schema` receives readable version-matched schema copies; the embedded
CLI schemas remain authoritative.

### 4.3 List and create

```powershell
dogdouspec iteration list --format xml

dogdouspec iteration create `
  --id 20260823-xpath-core `
  --kind feature `
  --format xml
```

`iteration create` (alias: `iteration new`) requires `--id` to be explicit and follow the `TimeFirstIdType` grammar (`YYYYMMDD-name` or `YYYYMMDDTHHmmssZ-name`, e.g., `20260823-feature` or `20260825T143000Z-feature`). `kind` is
`feature` or `research`. Creation atomically creates the directory, `spec.xml`,
and `tasks.xml`; it never chooses a suffix silently.

Iteration listing discovers candidate iteration direct child directories matching the time-first pattern and returns summaries in deterministic ascending chronological order based on `created_at` timestamp (parsed as UTC), with ordinal directory/iteration `id` as the tie-breaker. Both `xml` (as `@created_at` attribute on `<iteration>`) and `human` output formats expose `created_at`. It reports malformed candidate directories as structured diagnostics rather than hiding them.

## 5. XPath query contract

### 5.1 Evaluation

The CLI compiles the full XPath expression through .NET `System.Xml.XPath`.
V1 is XPath 1.0. A custom `XsltContext` provides string variables and the two
projection functions. Standard axes, predicates, unions, conversions, and
composition remain owned by the .NET engine.

The fixed extension binding is:

```text
prefix: ds
URI: urn:dogdouspec:xpath:functions:1
```

Managed documents themselves have no namespace.

Variables are supplied as `--var name=value`. Names match
`[a-z][a-z0-9_]*`, are case-sensitive, and omit the XPath `$`. V1 variables are
XPath strings. Duplicate, invalid, and unbound variables are errors.

### 5.2 Structural projection

```xpath
ds:filter(node-set, member, ...)
ds:filter-out(node-set, member, ...)
```

The first argument must contain only element nodes. At least one string member
is required. A member is exactly:

```text
@attribute-name
direct-child-name
```

Members do not contain paths, predicates, axes, wildcards, functions, or
namespace prefixes.

`ds:filter` preserves each selected root and retains only named direct
attributes and direct children. A retained child keeps its complete subtree.

`ds:filter-out` preserves each selected root and removes named direct attributes
and direct children. Other members remain complete.

A valid absent member is ignored. Duplicate members are coalesced. Input root,
attribute, and child order is preserved. The result is a read-only composable
XPath node-set, not a mutation address.

Examples:

```xpath
ds:filter(//task[@status='pending'], '@id', '@status', 'index')
```

```xpath
ds:filter-out(//task[@id=$task_id], 'records')/context
```

```xpath
ds:filter(//task, '@id', '@status', 'index')
  [index/term[@key='topic' and @value=$topic]]
```

### 5.3 Single-document query

```powershell
dogdouspec query `
  --document 20260823-xpath-core/tasks.xml `
  --var task_id=20260823-task-xpath-projection `
  --xpath '//task[@id=$task_id]' `
  --format xml
```

An all-element node-set uses one wrapper and embeds results directly:

```xml
<results
  document="20260823-xpath-core/tasks.xml"
  revision="8"
  type="node-set"
  derived="false">
  <task id="20260823-task-xpath-projection" status="in-progress">...</task>
</results>
```

`derived="true"` means an extension function produced projected values.
Scalar results use one `result` element. Attribute, text, comment,
processing-instruction, or mixed node-sets use minimal typed item wrappers.

No successful result is silently truncated.

### 5.4 Project search

`search` evaluates the same XPath independently against every document in a
declared boundary:

```powershell
dogdouspec search `
  --scope project `
  --var topic=xpath-projection `
  --xpath "ds:filter(//*[@id and index/term[@key='topic' and @value=$topic]], '@id', 'index')" `
  --format xml
```

Scopes are `iteration` and `project`; iteration scope also requires
`--iteration`. Documents are visited in normalized relative-path order. Each
non-empty document result receives one compact document wrapper. One XPath
never crosses documents.

Search excludes `_schema`, `_skill`, temporary files, and unsupported document
types unless explicitly requested by a future command.

### 5.5 Limits

V1 defaults:

- Managed XML document: 16 MiB.
- Final serialized query output: 4 MiB.
- Final result nodes: 10,000.
- Total intermediate projected nodes: 50,000.

Exceeding a limit fails with no partial success output and recommends a narrower
XPath or structural projection. Limits are checked before expensive
serialization where possible.

## 6. Schema and template discovery

```powershell
dogdouspec schema show --name tasks --version 1.0
dogdouspec template show --name record.discussion --version 1.0
```

Both commands write the exact XML/XSD to stdout and perform no mutation.
Templates are valid examples, not hidden scripts. An Agent copies a template,
replaces example identity and content, validates it, then submits it.

V1 templates include:

- `record.discussion`
- `record.finding`
- `record.verification`
- `task.update`
- `task.review`
- `iteration.confirmation`
- `knowledge.entry`
- `backlog.item`

## 7. Generic append

Most durable history is appended. The generic append command takes one complete
schema-known element from stdin or a file:

```powershell
dogdouspec append `
  --document 20260823-xpath-core/tasks.xml `
  --parent-xpath "//task[@id=$task_id]/records" `
  --var task_id=20260823-task-xpath-projection `
  --expected-revision 8 `
  --stdin
```

stdin:

```xml
<record
  id="20260823T041500Z-record-projection-discussion"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:15:00Z"
  actor="codex">
  <summary>Compared two projection implementations.</summary>
  <outcome>Focused tests are required before selecting one.</outcome>
</record>
```

Rules:

- The parent XPath is evaluated against the current source and must select
  exactly one element.
- The CLI resolves the selected node's schema declaration and validates the
  appended element in the resulting document context.
- The appended root must have a project-unique ID.
- The CLI increments the document revision and updates only schema-defined
  automatic root metadata.
- Append cannot write protected product state.
- Multiple top-level stdin nodes are rejected. Atomic combinations use
  `task update` or `transaction apply`.

Append retry is identity-based. If the submitted root ID already exists and its
canonical content is identical, the command returns success with
`already_applied="true"`. If content differs, it returns
`IDEMPOTENCY_CONFLICT`.

## 8. Atomic Task update

## 8. Atomic Task update

`task update` handles the atomic combination of optional Task-state transition,
Task acceptance criterion result updates, active Task-local record resolution,
context replacement/update, and one or more appended structured records stamped
with the request operation ID.

```powershell
dogdouspec task update `
  --iteration ID `
  --task TASK_ID `
  --expected-revision N `
  (--stdin | --file PATH) `
  [--workspace-root PATH] `
  [--format xml|human]
```

Request schema:

```xml
<task-update
  id="20260823T060000Z-update-complete-projection"
  transition="complete"
  actor="codex"
  occurred_at="2026-08-23T06:00:00Z">
  <acceptance>
    <criterion
      target="20260823-taskaccept-filter-composition"
      result="passed"/>
  </acceptance>
  <resolve-records>
    <record target="20260823T043000Z-record-projection-ordering"/>
  </resolve-records>
  <context_update>
    <summary>Updated context summary.</summary>
    <design_snapshot>Updated design snapshot.</design_snapshot>
  </context_update>
  <records>
    <record
      id="20260823T060000Z-record-projection-completion"
      kind="completion"
      status="informational"
      created_at="2026-08-23T06:00:00Z"
      actor="codex">
      <summary>Implemented and verified deterministic projection.</summary>
      <covers>
        <ref
          scope="document"
          target="20260823-taskaccept-filter-composition"
          relation="covers"/>
      </covers>
    </record>
  </records>
</task-update>
```

Rules:

1. **State Machine Transitions**:
   - `start`: `pending` -> `in-progress`
   - `block`: `in-progress` | `verification` -> `blocked`
   - `resume`: `blocked` -> `in-progress`
   - `verify`: `in-progress` -> `verification`
   - `complete`: `verification` -> `done`
   - `transfer` / `supersede` / `cancel`: `pending` | `in-progress` | `blocked` | `verification` -> `transferred` | `superseded` | `cancelled`
   - Omitted `transition` leaves the state unchanged.
   - Illegal and same-state transitions are rejected as semantic conflicts (`TASK_TRANSITION_CONFLICT`, exit 4).
2. **Timestamps**:
   - Sets `task/@updated_at` to `task-update/@occurred_at`.
   - `start` sets `task/@started_at` to `occurred_at` only if `started_at` was absent.
   - `complete` sets `task/@completed_at` to `occurred_at`.
3. **Record Stamping & Anti-Spoofing**:
   - Every appended record is stamped with `operation_id="task-update/@id"`.
   - Supplying a conflicting `record/@operation_id` is rejected.
   - Generic `append` rejects any fragment containing `record/@operation_id` to prevent receipt spoofing.
4. **Durable Idempotency**:
   - Operations are derived directly from Task records carrying `operation_id = request.id`.
   - An exact retry with either the original pre-commit expected revision or the current revision returns `already_applied="true"` with exit code 0 if the Task state, criteria, record resolutions, context, timestamps, and stamped records match canonically.
   - Partial receipts, mismatched content, cross-Task collisions, or cross-document collisions are rejected as `IDEMPOTENCY_CONFLICT` (exit 4).
5. **Prospective Validation & Authority Gate**:
   - Root revision of `tasks.xml` is incremented exactly once per successful commit.
   - Whole-project prospective validation enforces all schema constraints, time-first grammar, ID uniqueness, and completion predicates before live file replacement.
   - `task update` mutates only `tasks.xml` under the target iteration and never mutates `spec.xml` or product decision state.
   - Terminal tasks (`done`, `transferred`, `superseded`, `cancelled`) are immutable; metadata changes, transitions, and non-informational record appends fail with `TASK_IMMUTABLE` (exit 4).
   - When iteration `status="replanning"`, execution transitions (`start`, `resume`, `verify`, `complete`) fail closed with `ITERATION_REPLANNING_EXECUTION_FROZEN` (exit 5).

## 8.1 Structured Task review gate

Tasks may opt in with immutable implementer attribution and a review gate:

```xml
<task id="20260825-task-example" agent="implementation-agent" status="verification" ...>
  ...
  <review required="true"/>
  <records>...</records>
</task>
```

Submit the `task.review` request template while the Task is in `verification`:

```powershell
dogdouspec task review --iteration ID --task TASK_ID --expected-revision N (--stdin | --file PATH)
```

An `approved` submission is accepted only when its actor differs from Task
`@agent`, no active finding remains, and the operation ID is unused across the
workspace. A `changes-requested` submission appends an active finding, moves the
Task to `in-progress`, and requires correction, a new verification transition,
and a fresh approval. Completion readiness reports missing approvals and active
review findings. Legacy Tasks without `<review>` retain their existing lifecycle.

Actor separation is durable provenance, not authenticated or cryptographic
identity. DogdouSpec does not prove who controls an actor string; repositories
needing authenticated review must enforce that in their surrounding Git/CI or
identity system.

## 8.2 Task addition, revision, and splitting

```powershell
dogdouspec task add `
  --iteration 20260823-xpath-core `
  --expected-revision 12 `
  (--stdin | --file PATH)

dogdouspec task revise `
  --iteration 20260823-xpath-core `
  --task 20260823-task-xpath-projection `
  --expected-revision 12 `
  (--stdin | --file PATH)

dogdouspec task split `
  --iteration 20260823-xpath-core `
  --task 20260823-task-xpath-projection `
  --expected-revision 12 `
  (--stdin | --file PATH)
```

1. `task add`: Appends a pending task to `tasks.xml`. Requires `<origin>` referencing an existing requirement in `spec.xml`. Newly added task must have `status="pending"`.
2. `task revise`: Elaborates `rationale`, `scope`, `add_dependencies`, `add_constraints`, `add_acceptance`, and appends discussion records on active/pending tasks. Fails with `TASK_IMMUTABLE` if target task is in terminal status.
3. `task split`: Sets a parent task terminal disposition (`superseded`, `transferred`, `cancelled`) with rationale and records, and atomically appends 2 or more pending subtasks.

These helper requests are bounded by the normal XML document-size limit before parsing or reading any managed input. Their `occurred_at` must not precede the `updated_at` of every task or document they modify. A newly added successor/task must be pending, stamped with `created_at` and `updated_at` exactly equal to request `occurred_at`, and must not supply `started_at` or `completed_at`.

## 8.2 Requirement proposal

```powershell
dogdouspec requirement propose `
  --iteration 20260823-xpath-core `
  --expected-revision 5 `
  (--stdin | --file PATH)
```

Appends a new requirement to `spec.xml` under `<product><requirements>`. Requires `status="proposed"`. Attempting to supply non-proposed statuses fails with `OWNER_DECISION_REQUIRED` (exit 5).

## 8.3 Mid-flight change proposal and application

```powershell
dogdouspec change propose `
  --iteration 20260823-xpath-core `
  --expected-spec-revision 5 `
  --expected-tasks-revision 12 `
  (--stdin | --file PATH)

dogdouspec change apply `
  --iteration 20260823-xpath-core `
  --expected-spec-revision 6 `
  --expected-tasks-revision 13 `
  (--stdin | --file PATH)
```

1. `change propose`: A recovery-backed multi-document mutation across `spec.xml` and `tasks.xml`, valid only while the iteration is `active`. It requires at least one active `finding` record, freezes target tasks to `blocked`, persists each freeze reason, and adds proposed requirements to `spec.xml`. The first finding task receives a deterministic receipt containing the change summary and a canonical-request `request-sha256` fingerprint. An identical immediate replay returns `already_applied`; any semantic payload difference or later revision drift fails.
2. `change apply`: Applied during iteration `status="replanning"`. It must resolve a finding, dispose a task, or add a successor (no-op requests fail); it resolves active finding records, persists every disposition rationale, sets terminal task dispositions (`superseded`/`transferred`/`cancelled`), adds successor tasks, and appends one deterministic receipt with the canonical-request fingerprint. Fails with `CHANGE_APPLICATION_INVALID` (exit 4) if iteration status is not `replanning`.

`request-sha256` is an idempotency fingerprint over canonical request XML. It does not provide a signature, authenticity guarantee, or evidence integrity claim.

### 8.3.1 Deterministic public-CLI replanning smoke

Run this drill only in a disposable workspace. It demonstrates the public
command sequence; it does not authorize an Agent to make the two owner
confirmations. Start from the `change.propose`, `change.apply`, and
`iteration.confirmation` templates, replace every placeholder, and re-query
the returned revisions before each subsequent write.

1. In an **active** iteration with an approved requirement and a started Task,
   submit `change propose --expected-spec-revision <N> --expected-tasks-revision <N> --file change-propose.xml`. The request creates an active finding, freezes each affected Task, and may propose a replacement requirement.
2. After an explicit owner decision, submit `iteration confirm --file owner-replan.xml` with `action="replan"`, the current revisions, and decisions that supersede/approve the relevant requirements. The iteration becomes `replanning`; execution transitions are frozen.
3. Submit `change apply --expected-spec-revision <N> --expected-tasks-revision <N> --file change-apply.xml`. Resolve the finding, dispose affected Task(s), and add successor Task(s) with origins pointing to approved requirements.
4. Run the read-only gate and require `technically_ready="true"` with required action `continue`:

   ```powershell
   dogdouspec iteration readiness --iteration <ITERATION_ID> --phase activation --format xml
   ```

5. Only after a further explicit owner decision, submit `iteration confirm --file owner-continue.xml` with `action="continue"` and the current revisions. Confirm that a successor can be selected or started through the normal public Task commands.

`TaskChangeWorkflowCliTests.ChangeProposeAndApply_Cli_EndToEndLifecycle_Succeeds`
is the deterministic integration counterpart: it performs this public CLI
sequence against an isolated workspace and verifies the readiness gate before
continuation.

## 9. Readiness and product confirmation

```powershell
dogdouspec iteration readiness `
  --iteration 20260823-xpath-core `
  --phase activation|completion `
  --format xml
```

Readiness is read-only. It reports exact source revisions, technical facts,
pending product decisions, and the required owner action. “Ready for review” is
never serialized as “completed.”

After explicit owner review:

```powershell
dogdouspec iteration confirm --stdin
```

The confirmation request names expected source revisions and every decision to
apply. The CLI cannot authenticate a human owner; `actor` is attribution and Skill
invocation after current owner instruction is the authority boundary. The command
validates inputs and gates, updates only protected `spec.xml` state, appends confirmation
provenance, and commits once (leaving `tasks.xml` byte-identical). It never infers
product acceptance from Task state.

An Agent may prepare or display a confirmation template. The Skill must not
invoke it without an explicit owner instruction in the current interaction.

## 10. Low-level transaction

`transaction apply` remains available for schema migrations and atomic
combinations not covered by v1 helpers:

```powershell
dogdouspec transaction apply `
  (--stdin | --file PATH) `
  [--workspace-root PATH] `
  [--format xml|human]
```

The request root is `<transaction operation_id="...">`. Its operation ID is a
time-first correlation ID for the commit and recovery marker. The request and
operation ID are not retained as a durable business ledger after a successful
commit, so a retry using stale pre-commit revisions fails with
`REVISION_CONFLICT`. A request that is a semantic no-op at the exact current
revisions returns `already_applied="true"` without rewriting a file.

The engine supports:

- Expected document revisions.
- String variables.
- Boolean XPath 1.0 assertions.
- `append-child`, `replace-node`, `set-attribute`, and `remove-node`.
- Pre-commit whole-workspace validation.
- One recovery-backed commit for all changed documents.

Documents are processed in request order. Operations within one document run
sequentially against that document's working tree, so later XPath expressions
observe earlier operations. Every mutating selector must return a real node-set
from the current managed document and match its non-negative `expect` exactly.
`append-child` and `set-attribute` target elements; `replace-node` targets
non-root elements; `remove-node` targets non-root elements or attributes.
Projection results from `ds:filter` and `ds:filter-out` are read-only clones and
cannot be used as mutation addresses. Assertions may use ordinary XPath scalar
or node-set effective-boolean semantics.

The engine owns root `revision`: requests cannot set, remove, or replace it.
Each semantically changed document increments once; assert-only and no-op
documents do not increment and are omitted from the mutation document list.
Payloads cannot contain `operation_id`, and existing Task-update receipt records
cannot be modified or removed by the low-level transaction path.

The transaction engine resolves all selected nodes before checking protected
state. It compares the actual before/after XML trees, not the XPath spelling.
Iteration lifecycle and confirmations remain protected. Decided Requirements,
Research questions, design decisions, product acceptance, verified Knowledge,
and disposed Backlog items cannot be changed through this helper. Draft and
proposed planning content may be edited, and structured history records may be
appended, without turning the edit into product approval.

The Skill does not choose low-level transaction XML when an equivalent v1
template or helper exists.

## 11. Atomic filesystem behavior

Writers acquire one `.dogdouspec` project lock. They load and validate expected
revisions, stage complete replacement files in a workspace-local temporary
directory, flush them, and replace targets.

Single-document mutations must leave either the complete old file or complete
new file after interruption. During a low-level multi-document publish, readers
may temporarily observe a mix of complete old/new document revisions. The next
writer's startup recovery converges the transaction to one complete valid old or
new set. Iteration creation and low-level multi-document transactions use a
minimal recovery marker under a CLI-owned temporary area; transaction requests
are not retained as project history.

Startup recovery completes or rolls back an interrupted prepared commit before
serving another write. Reads do not take the writer lock but must observe a
complete old or new document.

## 12. Secure input behavior

All XML requests and managed documents use secure readers with DTD and external
resolution disabled. XPath is read-only. Extension functions have no file,
network, environment, process, or mutation access.

Malformed XML, unsupported schemas, invalid XPath, cardinality mismatch,
protected writes, and limit violations fail closed without modifying managed
documents.
