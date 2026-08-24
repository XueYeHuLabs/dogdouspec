# DogdouSpec v1 Iteration-First Demo

This review demo applies the iteration-first model. It contains no `project.xml`, `goal.xml`, independent Evidence document, persisted transaction directory, or Task lease.

The `.dogdouspec` directory in this demo is the project boundary.

The complete creation-through-completion walkthrough is in
`WORKFLOW_SIMULATION.md`.

## 1. Directory discovery

```text
.dogdouspec/
|-- _schema/
|   `-- README.md
|-- _skill/
|   `-- README.md
|-- knowledge.xml
|-- backlog.xml
`-- 20260823-xpath-core/
    |-- spec.xml
    `-- tasks.xml
```

One ordinary listing reveals Iterations and special project content:

```powershell
Get-ChildItem .dogdouspec
```

An Iteration directory matches `YYYYMMDD-name` and contains `spec.xml`. The `iteration/@id` in `spec.xml` equals the directory name.

## 2. Read the Iteration overview

```powershell
dogdouspec query `
  --document 20260823-xpath-core/spec.xml `
  --format xml `
  --xpath "ds:filter(/iteration, '@id', '@kind', '@status', 'index', 'product')"
```

To inspect only the design:

```xpath
/iteration/design
```

`spec.xml` owns product requirements, deliverables, scope, overall acceptance, design boundaries, design decisions, and explicit product confirmations. A Requirement is proposed, approved, superseded, or withdrawn; it is not technically “completed.” Task verification may support product acceptance decisions, but it cannot make them.

## 3. Read unfinished Task indexes

```powershell
dogdouspec query `
  --document 20260823-xpath-core/tasks.xml `
  --format xml `
  --xpath "ds:filter(//task[not(@status='done' or @status='transferred' or @status='superseded' or @status='cancelled')], '@id', '@status', '@agent', 'index')"
```

Expected compact shape:

```xml
<results
  document="20260823-xpath-core/tasks.xml"
  revision="9"
  type="node-set"
  derived="true">
  <task
    id="20260823-task-xpath-projection"
    status="in-progress"
    agent="codex">
    <index>...</index>
  </task>
  <task id="20260823-task-task-history" status="pending">
    <index>...</index>
  </task>
  <task id="20260823-task-atomic-update" status="pending">
    <index>...</index>
  </task>
</results>
```

There is no persisted current-Task or next-Task pointer. XML document order is the default order.

## 4. Read the first unfinished Task

Index only:

```xpath
ds:filter(
  (//task[not(
    @status='done'
    or @status='transferred'
    or @status='superseded'
    or @status='cancelled'
  )])[1],
  '@id',
  '@status',
  '@agent',
  'index'
)
```

Complete Task context:

```powershell
dogdouspec query `
  --document 20260823-xpath-core/tasks.xml `
  --format xml `
  --var task_id=20260823-task-xpath-projection `
  --xpath '//task[@id=$task_id]'
```

The complete Task contains objective, rationale, scope, necessary origin and dependency references, constraints, Task acceptance, a local design snapshot, and durable execution records.

## 5. Exclude Task history temporarily

```xpath
ds:filter-out(
  //task[@id=$task_id],
  'context',
  'records'
)
```

Missing direct members are ignored. The selected root and all other direct members remain.

## 6. Find Task verification and completion provenance

Verification is part of Task history:

```xpath
//task[@id=$task_id]/records/record[
  @kind='verification' or @kind='completion'
]
```

The completed layout Task demonstrates an integrated completion record with checks and acceptance coverage. No independent `evidence.xml` is required.

## 7. Resolve and reverse references

Reference lookup scopes are filesystem boundaries:

| Scope | Search boundary |
|---|---|
| `document` | Current XML document. |
| `iteration` | Managed XML files in the current Iteration directory. |
| `project` | Managed XML files under the current `.dogdouspec` directory. |

Forward reference:

```xml
<ref
  scope="iteration"
  target="20260823-req-structural-projection"
  relation="implements"/>
```

Reverse lookup:

```xpath
//ref[@target=$target_id]
```

The CLI evaluates the reverse lookup over the declared search boundary in deterministic path order. References do not persist a target document path because managed IDs are project-unique.

## 8. Start a Task from a schema template

There is no `task-claim.xml` or persisted start request. The Skill first reads
the `task.update` template, fills it, and submits one schema-aware update:

```powershell
dogdouspec template show --name task.update --version 1.0

dogdouspec task update `
  --iteration 20260823-xpath-core `
  --task 20260823-task-task-history `
  --expected-revision 9 `
  --stdin
```

Illustrative stdin payload:

```xml
<task-update
  id="20260823T042100Z-update-start-task-history"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T04:21:00Z">
  <records>
      <record
        id="20260823T042100Z-record-task-history-start"
        kind="start"
        status="informational"
        created_at="2026-08-23T04:21:00Z"
        actor="codex">
        <summary>Started Task execution.</summary>
      </record>
  </records>
</task-update>
```

The request is not copied into `.dogdouspec`. The helper owns Task lookup,
transition validation, current timestamps, revision checks, and atomic commit.
Git records the mechanical diff and the appended record preserves semantic
context.

## 9. Complete a Task with the same mechanism

A completion Task update atomically:

1. Asserts the expected `tasks.xml` revision and Task status.
2. Marks Task acceptance criteria `passed` or `not-applicable`.
3. Appends verification and completion records, including checks and coverage.
4. Sets `status="done"`, `completed_at`, and `updated_at`.
5. Validates the resulting XML before replacing `tasks.xml`.

No completion document, Evidence manifest, lease, or duplicate lifecycle object is created.

Task completion is a technical state transition and may be automated by the
Skill. It does not accept an Iteration-level product criterion and does not
complete the Iteration. When all technical work is ready, the Agent runs the
read-only `iteration readiness` command and stops at the product review gate.
Only `iteration confirm` may update protected decision fields in `spec.xml`.

## 10. Git review surface

Ordinary execution updates only:

```text
.dogdouspec/20260823-xpath-core/tasks.xml
```

Material product or design changes require an explicit owner confirmation in
`spec.xml`. After that decision commits, an Agent may reconcile affected Tasks
through `task update`. The generic transaction remains an escape hatch for
unsupported combinations. Git provides line-level mechanical history; Task
records preserve the semantic explanation needed by the next Agent.
