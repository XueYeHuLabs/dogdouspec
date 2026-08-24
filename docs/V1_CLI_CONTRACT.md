# DogdouSpec v1 CLI Contract

Status: Normative implementation contract

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
dogdouspec transaction apply --stdin|--file
```

`transaction apply` is the low-level escape hatch. The Skill normally uses
templates, `append`, `task update`, and `iteration confirm`.

Every mutating command identifies itself as mutating in help output and requires
an expected revision for each existing document it may change.

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

`--id` is explicit and must follow the Work-directory grammar. `kind` is
`feature` or `research`. Creation atomically creates the directory, `spec.xml`,
and `tasks.xml`; it never chooses a suffix silently.

Iteration listing reads date-prefixed direct child directories in normalized
name order. It reports malformed candidate directories as diagnostics rather
than hiding them.

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
