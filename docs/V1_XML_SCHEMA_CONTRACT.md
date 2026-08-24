# DogdouSpec v1 XML Schema Contract

Status: Normative implementation contract

This document freezes the iteration-first persisted XML model for the first
usable DogdouSpec release. The executable schemas are under `schemas/v1`.
Where an older design or draft conflicts with this document, this document and
the current iteration-first demo take precedence.

## 1. v1 product boundary

The v1 persisted workspace contains:

```text
.dogdouspec/
  _schema/
  _skill/
  knowledge.xml
  backlog.xml
  YYYYMMDD-name/
    spec.xml
    tasks.xml
```

There is no mandatory `project.xml`. The filesystem is the coarse project
index. Date-prefixed Work directories are the first discovery surface.

The v1 core supports:

- Feature Iterations and Research Work using the same directory boundary.
- Product and research intent in `spec.xml`.
- Independent technical Tasks and durable execution records in `tasks.xml`.
- Project knowledge and deferred obligations.
- XPath 1.0 reads, structural projection, validation, template-driven append,
  Task update, and explicit product confirmation.

The v1 core does not persist claim, lease, Evidence, transaction, event, or
completion-request files. MCP, GUI, signatures, hash chains, generated policy
digests, and high-contention Task sharding are outside v1.

## 2. Schema ownership and XML profile

The schemas shipped with the executable are authoritative for the executable's
schema version. The repository copies under `schemas/v1` are the development
source. A workspace `_schema` directory exposes readable copies or links for an
Agent; editing a workspace copy does not silently change CLI validation.

Managed documents:

- Use XML 1.0 and UTF-8 without a byte-order mark.
- Use no XML namespace. This keeps common XPath expressions concise.
- Use `schema_version="1.0"` at every persisted document root.
- Prohibit DTDs, external entities, XInclude, and external schema resolution.
- Do not use mixed content in structural containers.
- Preserve narrative whitespace inside leaf text elements.
- Are serialized deterministically by the CLI.

The `ds` prefix is reserved for XPath extension functions and is not declared
in managed XML.

## 3. Documents and ownership

| Document | Owns | Does not own |
|---|---|---|
| `spec.xml` | Work identity, product/research intent, requirements or questions, product acceptance, design decisions, product confirmations | Technical Task execution state |
| `tasks.xml` | Task graph, technical state, Task acceptance, attempts, findings, discussion summaries, verification, completion and handoff records | Product acceptance decisions |
| `knowledge.xml` | Reusable verified project knowledge | Mandatory policy or active Task state |
| `backlog.xml` | Obligations deliberately removed from active Work | Work still required by current acceptance |

One fact has one persisted owner. References connect owners; they do not copy
another object's authoritative state.

## 4. Identity, time, and revision

### 4.1 IDs

Every persisted object ID is project-unique and time-first:

```text
YYYYMMDD-name
YYYYMMDDThhmmssZ-name
```

The first form is used for durable planned objects. The timestamp form is used
for chronological records and operations. The suffix uses lowercase ASCII
letters, digits, and hyphens.

An ID never depends on a mutable title. The Work root ID must equal its
directory name. A CLI-generated timestamp uses UTC. When two IDs would collide,
the CLI adds a deterministic numeric suffix to the semantic suffix rather than
changing the timestamp grammar. Every appended Task record is stamped with an
`operation_id` matching its mutating `task-update` request ID. Operation receipts
are derived directly from Task records carrying `operation_id`; there are no
separate `_ops` files or persisted request payload documents.

Low-level `<transaction>` requests also carry an `operation_id`, but it is only
a commit/recovery correlation ID. It is not copied into managed product state and
does not provide durable post-commit retry recognition. Transaction payloads
cannot create `operation_id` attributes, and resolved before/after validation
prevents them from changing or deleting Task-update receipt records.

### 4.2 Revisions

Each persisted document has a positive integer `revision`. One successful
commit that changes a document increments it exactly once. Assertion-only reads
do not increment it.

All mutations provide `expected_revision`. A mismatch fails before mutation.
Object-level revisions and persisted leases are not part of v1.

### 4.3 Timestamps and actors

Timestamps use UTC `xs:dateTime`. A Task may carry current-state timestamps such
as `started_at`, `completed_at`, and `updated_at`. Records carry their own
`created_at` and `actor`, as well as an optional `operation_id`.

Actor is attribution, not cryptographic authentication. The Skill and CLI
authority boundary must not describe it as proof of human identity.

## 5. Index contract

Every Work root and Task has an `index`. Requirements, questions, deliverables,
design decisions, knowledge entries, backlog items, and important records may
also have one.

```xml
<index>
  <summary>Implement composable XPath projection.</summary>
  <term key="component" value="xpath-query"/>
  <term key="topic" value="xpath-projection"/>
  <term key="priority" value="p0"/>
  <term key="tag" value="document-order"/>
</index>
```

Rules:

- `summary` is a compact discovery description, not a copy of the full body.
- Keys and values use normalized lowercase tokens.
- The reserved v1 keys are `component`, `topic`, `priority`, `kind`, `scope`,
  `risk`, `iteration`, and `tag`.
- A key may occur more than once.
- `tag` is the normal escape hatch for Agent-defined keywords.
- Important indexed nodes should normally carry two to six terms.
- An Agent updates the index when a semantic change makes the existing summary
  or terms misleading.
- The CLI validates syntax but does not invent business keywords.

Exact index lookup is the primary navigation mechanism. Full-text
`contains(string(.), $text)` is a discovery fallback. A repeatedly useful
full-text concept should be promoted to a term.

## 6. Reference and visibility contract

References are stable-ID, single-direction edges:

```xml
<ref
  scope="iteration"
  target="20260823-req-structural-projection"
  relation="implements"/>
```

`scope` defines the maximum resolution boundary:

- `document`: the containing XML document only.
- `iteration`: `spec.xml` and `tasks.xml` in the containing Work directory.
- `project`: all managed documents below the nearest `.dogdouspec` root.

The narrowest sufficient scope is required. A cross-Work reference therefore
uses `project`. Resolution visits documents in deterministic normalized path
order and requires exactly one target ID.

References do not persist target paths or XPath expressions. Reverse edges are
not stored; they are derived with `//ref[@target=$target_id]` over the declared
search boundary. A terminal or superseded object remains addressable. Physical
deletion is rejected while references exist.

## 7. Structured rationale and process records

A durable object must preserve enough semantic history for a later Agent to
understand not only the conclusion, but why it was reached.

Requirements and Tasks use:

- `statement` or `objective`: what is required.
- `rationale`: why it matters.
- `key_points`: facts that must be noticed during a focused read.
- `records`: chronological structured summaries of execution or discussion.

A record may include:

```xml
<record
  id="20260823T050000Z-record-projection-discussion"
  kind="discussion"
  status="resolved"
  created_at="2026-08-23T05:00:00Z"
  actor="owner">
  <index>
    <summary>Compared projection document-order strategies.</summary>
    <term key="topic" value="projection-order"/>
  </index>
  <summary>Compared independent fragments with a shared projected tree.</summary>
  <context>The first implementation could not define cross-document order.</context>
  <options>
    <option id="20260823-option-separate-documents" disposition="rejected">
      <summary>Materialize every selected root in a separate document.</summary>
      <reason>The results have no common XPath document order.</reason>
    </option>
    <option id="20260823-option-shared-tree" disposition="selected">
      <summary>Materialize one shared projected tree.</summary>
      <reason>It preserves deterministic composition and ordering.</reason>
    </option>
  </options>
  <outcome>Use a shared tree or a conformant virtual Navigator.</outcome>
</record>
```

Supported v1 record kinds are `start`, `discussion`, `question`, `attempt`,
`finding`, `decision`, `resolution`, `verification`, `completion`, and
`handoff`. Status is `informational`, `active`, `resolved`, or `superseded`.

Records are append-oriented. A semantic correction appends a resolution or
superseding record and may update the old record's status in the same atomic
Task update. Raw chat transcripts are not persisted by default. The summary,
relevant alternatives, reasoning, unresolved questions, and outcome are.

An Agent may record observations and proposed conclusions. A record does not
approve a Requirement, accept a material design decision, waive acceptance, or
complete an Iteration. Those states require product confirmation.

## 8. Feature specification

A feature `spec.xml` contains, in order:

1. Work index.
2. `product`: objective, deliverables, scope, requirements, and acceptance.
3. `design`: overview, boundaries, and decisions.
4. Product confirmations.

Requirement lifecycle:

```text
proposed -> approved -> superseded
                    -> withdrawn
```

A Requirement is never `completed`. Product acceptance decisions express
whether the delivered result satisfies the approved Requirement.

Product acceptance decisions are `pending`, `accepted`, `rejected`, or
`waived`. A waiver requires a rationale in the confirmation request.

Design decision states are `proposed`, `accepted`, `rejected`, and
`superseded`.

## 9. Research specification

Research uses the same Work directory and Task execution model. Its
`spec.xml` root remains `iteration` with `kind="research"`, but contains a
`research` body instead of `product`:

- Objective.
- Questions with `open`, `answered`, `deferred`, or `withdrawn` state.
- Method and boundaries.
- Expected outputs.
- Product-confirmed completion criteria.

XSD validates the structural choice. Semantic validation requires `product`
for `kind="feature"` and `research` for `kind="research"`.

Research completion does not automatically approve a proposed product design.
Its answers become records, knowledge proposals, Tasks, or a product decision
through explicit disposition.

## 10. Task contract

Tasks are independent state machines stored in document order:

```text
pending -> in-progress -> verification -> done
                     |                |
                     +-> blocked -----+

pending|in-progress|blocked|verification
    -> transferred|superseded|cancelled
```

There is no persisted global current or next Task pointer. The Skill queries
current state and selects in document order.

Each Task contains its own objective, rationale, scope, necessary origin and
dependency references, constraints, technical acceptance, focused context, key
points, and records. It must not require repeated loading of other Tasks merely
to understand its own work.

Task acceptance states are `pending`, `passed`, `failed`, and
`not-applicable`. These are technical states and may be updated by Agent
automation when supported by verification records.

`done` requires:

- Every Task criterion is `passed` or `not-applicable`.
- At least one completion record exists.
- Applicable verification or completion records cover the criteria.
- No active Task-local finding blocks the Task objective.

Task completion may make an Iteration ready for product review. It never
updates Requirement or product acceptance state.

## 11. Product decision protection

The protected `spec.xml` surface includes:

- Iteration lifecycle state.
- Requirement approval, supersession, and withdrawal.
- Research question final disposition.
- Product acceptance decisions and waivers.
- Accepted, rejected, or superseded material design decisions.
- Authoritative confirmation records.

Draft planning may write proposed content. A generic append or transaction
request that resolves to a protected write fails with
`OWNER_DECISION_REQUIRED`. XPath spelling does not bypass the resolved-node
check. For already decided objects, the protected comparison covers both their
decision state and authoritative narrative; appending structured history under
an allowed `records` container does not rewrite the prior decision.

`iteration readiness` is read-only. `iteration confirm` is the dedicated
revision-checked write path. The Skill must stop and wait for explicit owner
instruction before invoking it.

## 12. Knowledge and backlog

Knowledge entries use `proposed`, `verified`, `retired`, or `rejected` status.
An Agent may append a proposal. Verification is a product/project decision and
uses explicit confirmation or configured owner action.

Backlog items use `open`, `scheduled`, `completed`, or `cancelled`. A backlog
item records its source, rationale, impact, priority through index terms, and a
target or review condition. Work required by current product acceptance cannot
be moved to backlog without a confirmed product change.

## 13. Semantic validation beyond XSD

XSD is necessary but not sufficient. Whole-project validation also checks:

- Project-wide ID uniqueness and time-first grammar.
- Operation ID ownership (strictly Task-owned records in tasks.xml), absence of cross-Task/cross-document spreading, and element ID collision avoidance.
- Work directory and root ID equality.
- Feature/research body agreement with `kind`.
- Reference scope, uniqueness, and resolvability.
- Task dependency acyclicity and terminal predicates.
- Task acceptance coverage.
- Protected state and confirmation provenance.
- Product completion readiness and explicit confirmation.
- Backlog deferral safety.
- Schema version support and deterministic document discovery.

Validation is read-only and returns all safely discoverable diagnostics.

## 14. Deterministic serialization

The CLI owns writes to managed documents. It preserves untouched text and
subtrees where possible and serializes changed structures using:

- UTF-8 without BOM.
- Two-space indentation.
- One final newline.
- Schema-defined element order.
- Stable attribute order defined by the serializer contract.
- No silent whitespace normalization inside narrative leaf elements.

This keeps Git review useful while allowing schema-aware mutation.
