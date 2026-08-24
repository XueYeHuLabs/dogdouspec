# DogdouSpec v1 XML Core Contract - Review Draft

Status: Superseded Review Draft

Superseded by `V1_XML_SCHEMA_CONTRACT.md` and its executable `schemas/v1`
artifacts.

This draft predates the iteration-first filesystem demo and is retained only
for comparison. Its project catalog, Goal, Evidence, and transaction layout
must not be treated as the current v1 direction.

This document is the first review slice of the DogdouSpec v1 core contract. It freezes enough structure to evaluate scoped references, indexes, Tasks, and Task-local execution history. It intentionally does not yet freeze every project document schema or the complete transaction protocol.

## 1. Design boundary

The XML model is authoritative. The CLI understands XML structure, stable identity, XPath, schemas, references, revisions, assertions, and transactions. It does not contain compiled knowledge of Iteration, Task, Finding, or Change workflows.

Schemas define valid structure. Versioned transaction helpers define reusable atomic mutations. The Skill defines the composite workflow followed by an Agent.

The v1 XML model favors:

- Small index-first reads.
- Stable identity instead of positional identity.
- Self-contained Task context.
- Directed references with derived reverse lookup.
- Task-local durable records instead of conversation history.
- One authoritative location for each fact.

## 2. Common document contract

Every managed document root has:

```xml
<goal
  id="GOAL-ITER-2026-001"
  schema_version="1.0"
  revision="7">
  ...
</goal>
```

The common attributes are:

| Attribute | Meaning |
|---|---|
| `id` | Stable identity of the document root. |
| `schema_version` | Version of the document schema. |
| `revision` | Unsigned document revision, starting at `1`. |

Managed IDs use uppercase ASCII tokens separated by hyphens:

```text
[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*
```

Examples include `ITER-2026-001`, `REQ-001`, `T-014`, and `REC-T014-003`. Titles, slugs, paths, and XML positions are never identities.

Every `id` attribute in a managed project identifies the element that owns the
attribute and must be unique across that project. Attributes that point to an
existing identity use names such as `ref`, `target`, or `work_item`; they must
not reuse `id` as a reference-bearing attribute.

Timestamps use UTC ISO 8601 round-trip form, for example `2026-08-22T04:12:30.0000000Z`.

## 3. Visibility and references

### 3.1 Visibility levels

Addressable objects declare the widest boundary from which they may be referenced:

```text
document < work < project
```

The levels mean:

| Visibility | Permitted source |
|---|---|
| `document` | Only the document containing the target. |
| `work` | Any cataloged document belonging to the same Iteration or Research Work Item. |
| `project` | Any managed document cataloged by the same project. |

The default visibility is `document`. Schemas may require a wider visibility for specific object types.

Examples:

```xml
<requirement id="REQ-001" visibility="work">
  ...
</requirement>

<knowledge id="KNOW-001" visibility="project">
  ...
</knowledge>
```

Visibility is an architectural coupling boundary, not an access-control or cryptographic authorization mechanism.

### 3.2 Reference form

A persisted reference is a directed edge:

```xml
<ref
  scope="work"
  document="work:ITER-2026-001:spec"
  target="REQ-001"
  relation="implements"/>
```

Reference attributes are:

| Attribute | Requirement | Meaning |
|---|---|---|
| `scope` | Required | Resolution boundary: `document`, `work`, or `project`. |
| `document` | Required outside the current document | Canonical catalog document reference. |
| `target` | Required | Stable target ID. |
| `relation` | Required | Directed relation name defined by the owning schema. |

For a same-document reference, `document` is omitted:

```xml
<ref scope="document" target="T-001" relation="depends-on"/>
```

A validator must confirm that:

1. The target document is cataloged.
2. The target ID exists exactly once in that document.
3. The source and target are within the declared `scope`.
4. The source is permitted by the target's `visibility`.
5. The `relation` is valid at the reference location.
6. The declared `scope` is the narrowest boundary containing both source and target.

Persisted references never contain XPath expressions. XPath is used to discover and inspect objects; stable IDs are used to persist relationships.

### 3.3 Direction rule

Only the authoritative forward edge is stored. Reverse edges are queried and are never duplicated as authoritative state.

Examples:

- A dependent Task points to its prerequisite with `depends-on`.
- A Task points to a Requirement with `implements`.
- Evidence points to the acceptance criterion it proves.
- A replacement object points to the object it supersedes.

Reverse lookup is derived:

```xpath
//ref[@target=$target_id]
```

Project-wide reverse lookup evaluates that XPath independently across cataloged documents. A future derived index may accelerate the lookup, but it cannot become authoritative.

## 4. Index contract

Every independently discoverable object may contain one `index` element. A Task must contain it as its first child.

```xml
<index>
  <summary>Implement the first safe XML query pipeline.</summary>
  <term key="repo" value="dogdouspec"/>
  <term key="component" value="xml-core"/>
  <term key="topic" value="xpath-projection"/>
  <term key="priority" value="p0"/>
</index>
```

The index rules are:

- `summary` is plain text and is limited to 512 characters.
- An index contains at most 32 `term` elements.
- `key` and `value` are exact machine tokens, not comma-separated lists.
- Keys use lowercase ASCII letters, digits, dots, and hyphens.
- Values use lowercase ASCII letters, digits, dots, colons, slashes, underscores, and hyphens.
- The same key may have multiple values.
- An identical key/value pair must not appear twice in one index.
- Term order is stable for rendering but has no semantic priority.
- Object ID and lifecycle status remain attributes on the owning object and are not duplicated as terms.

Exact-match lookup is the default:

```xpath
//task[index/term[@key='topic' and @value='xpath-projection']]
```

Substring matching against serialized index content is not a supported Skill convention.

## 5. Task contract

### 5.1 Task ownership and context

A Task is the primary resumable execution unit. It must contain enough durable context for a new Agent to understand and continue the work without loading unrelated Tasks or relying on a previous conversation.

A Task therefore owns:

- Its identity and lifecycle status.
- A compact discovery index.
- Objective and rationale.
- Repository and path scope.
- Necessary constraints.
- Acceptance criteria.
- A small set of origin and dependency references.
- Task-local attempts, findings, decisions, blockers, handoffs, verification, and completion records.
- Evidence references required to justify its terminal state.

Task metadata must not become a graph of narrative references. Context needed repeatedly by the Task should be copied into its bounded context as a provenance-bearing snapshot or summary. External references in metadata are reserved for identity-bearing relationships such as origin requirements, genuine prerequisites, evidence, and successors.

### 5.2 Task shape

```xml
<task
  id="T-002"
  visibility="work"
  status="pending"
  created_at="2026-08-22T04:00:00.0000000Z"
  updated_at="2026-08-22T04:00:00.0000000Z">
  <index>...</index>
  <title>Define XPath projection behavior</title>
  <objective>...</objective>
  <rationale>...</rationale>
  <scope>...</scope>
  <origin>...</origin>
  <dependencies>...</dependencies>
  <constraints>...</constraints>
  <acceptance>...</acceptance>
  <context>...</context>
  <lease>...</lease>
  <records>...</records>
  <evidence>...</evidence>
</task>
```

The element order is schema-defined and deterministic. Optional empty containers are omitted unless a transaction helper requires them as a stable append target.

### 5.3 Lifecycle

The v1 Task statuses are:

```text
pending
in-progress
blocked
verification
done
transferred
superseded
cancelled
```

The ordinary path is:

```text
pending -> in-progress -> verification -> done
                 |              |
                 v              v
              blocked <---------+
                 |
                 v
             in-progress
```

`transferred`, `superseded`, and `cancelled` are terminal dispositions with schema assertion requirements. `done` means the current acceptance criteria are satisfied and referenced by adequate evidence.

Each Task is an independent state machine. A Goal does not persist `current_task`, `next_task`, `active_task`, or an equivalent global pointer. XPath returns Tasks in document order, which is the default execution order.

### 5.4 Scope

Task scope is kept locally and is not represented as a list of references to other Tasks:

```xml
<scope>
  <repository ref="REPO-DOGDOUSPEC">
    <include path="src/DogdouSpec.Core/**"/>
    <include path="tests/DogdouSpec.Core.Tests/**"/>
    <exclude path="docs/approved/**"/>
  </repository>
</scope>
```

Paths are repository-relative, use `/`, and must not contain `..` segments.
The `ref` attribute points to the repository catalog identity. It is not a new
object identity; `id` is reserved for the identity of the element that owns it.

### 5.5 Origin and dependencies

Origin references explain why the Task exists:

```xml
<origin>
  <ref
    scope="work"
    document="work:ITER-2026-001:spec"
    target="REQ-002"
    relation="implements"/>
</origin>
```

Dependencies contain only genuine execution prerequisites:

```xml
<dependencies>
  <ref scope="document" target="T-001" relation="depends-on"/>
</dependencies>
```

Task dependency edges must be acyclic. A Task must not reference another Task merely to avoid writing its own objective, constraints, or acceptance context.

### 5.6 Acceptance

Acceptance criteria are Task-local and stable within the current Goal revision:

```xml
<acceptance>
  <criterion id="AC-T002-01" visibility="work" status="pending">
    The filter function retains only named direct attributes and child elements.
  </criterion>
  <criterion id="AC-T002-02" visibility="work" status="pending">
    Missing members are ignored without changing result cardinality.
  </criterion>
</acceptance>
```

Criterion statuses are `pending`, `passed`, `failed`, and `not-applicable`. A terminal Task transaction must provide the disposition and evidence required by the Task status.

### 5.7 Local context

The context container holds bounded information that the next Agent is expected to need:

```xml
<context>
  <summary>
    The query engine uses XPath 1.0 through System.Xml.XPath and a custom
    XsltContext for variables and DogdouSpec extension functions.
  </summary>
  <constraint id="CTX-T002-01">
    Projection must never modify the managed source document.
  </constraint>
</context>
```

Context is not an unbounded work log. Repeated discoveries and execution history belong in records.

### 5.8 Lease

A lease is optional and has operational rather than authorization meaning:

```xml
<lease
  operation_id="OP-CLAIM-T002-001"
  agent="codex"
  claimed_at="2026-08-22T04:10:00.0000000Z"
  expires_at="2026-08-22T05:10:00.0000000Z"/>
```

A lease prevents accidental concurrent claims. It does not grant permission outside Task scope or bypass an approval gate.

### 5.9 Task-local records

Records are durable semantic context:

```xml
<records>
  <record
    id="REC-T002-001"
    kind="decision"
    status="active"
    created_at="2026-08-22T04:20:00.0000000Z"
    agent="codex">
    <index>
      <summary>Use an XPath NodeSet extension rather than a CLI projection flag.</summary>
      <term key="topic" value="xpath-extension"/>
    </index>
    <body>
      The function returns a projected node set. Standard XPath composition
      remains owned by System.Xml.XPath.
    </body>
  </record>
</records>
```

Record kinds are:

```text
attempt
finding
decision
blocker
handoff
verification
completion
deviation
```

Record statuses are `active`, `resolved`, `superseded`, and `informational`. Records are append-preferred. A correction normally appends a new record with a same-document `supersedes` reference rather than rewriting historical meaning.

Mechanical mutation history belongs in transaction events, not Task records.

## 6. Goal contract

A Goal owns an ordered collection of independent Tasks:

```xml
<goal
  id="GOAL-ITER-2026-001"
  schema_version="1.0"
  revision="7"
  work_item="ITER-2026-001">
  <index>...</index>
  <tasks>
    <task id="T-001" status="done">...</task>
    <task id="T-002" status="pending">...</task>
    <task id="T-003" status="pending">...</task>
  </tasks>
</goal>
```

Goal progress is derived, for example:

```xpath
count(//task[@status='done'])
```

```xpath
count(//task[not(
  @status='done'
  or @status='transferred'
  or @status='superseded'
  or @status='cancelled'
)])
```

The Goal root may have its own lifecycle, but that lifecycle never identifies the next Task.

## 7. Items intentionally deferred from this review slice

This draft does not yet freeze:

- Complete XSD files.
- Full Project, Knowledge, Policy, Backlog, Finding, Change, Evidence, and Event shapes.
- The complete Patch and Transaction schema.
- Approval record authentication.
- Evidence signatures or event hash chains.
- Schema migration commands.
- MCP, GUI, editor, or scheduler adapters.
