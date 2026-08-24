# DogdouSpec v1 Design

Status: Legacy bootstrap baseline; partially superseded

Normative precedence: the iteration-first contracts in
`V1_XML_SCHEMA_CONTRACT.md`, `V1_CLI_CONTRACT.md`, and
`V1_SKILL_WORKFLOW.md` supersede this document wherever layout, persisted
documents, query output, mutation helpers, or lifecycle authority conflicts.
The implementation plan must not reintroduce the earlier mandatory project
catalog, independent Evidence lifecycle, Task leases, or automatic product
completion.

Version: 0.1

Scope: First usable local project-governance release

## 1. Purpose

DogdouSpec is a local, XML-backed project governance tool for the Dogdou project. It provides a structured source of truth for project structure, durable engineering knowledge, policies, specifications, iteration and research state, discoveries, changes, evidence, and deferred work.

The first release is CLI-first. It does not require an MCP server, a global installation, or per-editor configuration. Agents and humans use the same repository-local command-line entry point. Skills guide the workflow but do not own state or write XML directly.

DogdouSpec is implementation-aware but is not an implementation engine. It constrains, explains, records, validates, and gates implementation work. It does not edit product source code, choose an architecture on behalf of an owner, approve material specification changes, or silently relax acceptance criteria.

## 2. Goals

The first usable release must:

1. Represent one Dogdou project containing multiple repositories.
2. Maintain a project catalog, durable knowledge, policies, a project backlog, iterations, and research work items.
3. Store authoritative state in structured XML that can be queried with XPath 1.0.
4. Keep specification, execution state, evidence, history, and deferred obligations separate.
5. Support predictable handling of discoveries, task expansion, replanning, superseded designs, and residual work.
6. Provide a repository-local CLI with stable human-readable and JSON output.
7. Validate document structure, references, lifecycle transitions, completion conditions, and policy gates.
8. Generate a scoped context package for an agent before implementation begins.
9. Preserve all material history without treating logs as the current source of truth.
10. Support migration of the DogdouSpec project itself after the first usable release is complete.

## 3. Non-goals

The first release will not:

- Provide an MCP server.
- Schedule or host autonomous agents.
- Edit product source files.
- Run arbitrary build, test, VM, release, or deployment commands.
- Replace Git, repository-native build configuration, `.editorconfig`, CI, or code ownership files.
- Automatically approve specification changes, policy exceptions, accepted risks, releases, pushes, or expensive verification.
- Become a general document database or a general-purpose workflow engine.
- Provide a graphical user interface.
- Optimize for concurrent remote writers or a centralized server.

An optional MCP adapter or constrained verification runner may be considered only after self-hosting proves that the CLI and document contracts are stable.

## 4. Core principles

### 4.1 One fact, one authoritative owner

Each kind of fact has one authoritative document. Other documents link to it rather than copying it. Derived summaries are caches and must identify their source revision.

### 4.2 Plans are hypotheses

Planning is expected to be incomplete. Discoveries and scope expansion are normal domain events. The system must make replanning explicit without erasing the original plan or allowing a task tree to expand without review.

### 4.3 No silent semantic mutation

An agent may elaborate execution tasks when the approved specification remains unchanged. It may not silently change an objective, requirement, non-goal, architecture boundary, acceptance criterion, authority boundary, or accepted risk.

### 4.4 Current state is compact; history is separate

`goal.xml` contains current execution state. Change records, events, evidence, and Git history explain how the project arrived there. Reconstructing current state from an event stream is not required.

### 4.5 Native enforcement remains native

Formatting belongs in `.editorconfig`; compiler rules belong in project files; executable validation belongs in tests and CI. DogdouSpec records their meaning, scope, and relationship to work but does not duplicate their implementation.

### 4.6 Automation stops at authority boundaries

Skills may automate routine elaboration and state transitions. They must stop for material specification changes, policy exceptions, accepted risks, protected ownership changes, external side effects, and verification requiring explicit authorization.

## 5. System boundaries

### 5.1 XML workspace

The workspace is the durable project source of truth. It owns document identity, revisions, links, and structured content. It does not select work or execute commands.

### 5.2 Domain and validation core

The core discovers the workspace, parses XML securely, evaluates XPath, resolves links, validates schemas and semantic invariants, applies lifecycle rules, and commits transactions. It contains no editor-specific or agent-specific behavior.

### 5.3 CLI adapter

The CLI exposes domain operations and stable JSON results. It parses arguments and renders output but does not contain a second copy of domain rules.

### 5.4 Skill

The Skill explains when and why to invoke CLI operations. It selects the workflow, requests scoped context, and stops at gates. It keeps no private project state and never writes XML directly.

### 5.5 Agent

The Agent performs investigation, design, implementation, and verification within the scope and authority returned by DogdouSpec. It records findings and evidence, but cannot approve its own material scope expansion unless policy explicitly permits it.

### 5.6 Human or project owner

The owner approves material specification revisions, policy exceptions, accepted risks, protected ownership changes, release operations, and other configured authorization gates.

## 6. Repository-local distribution and discovery

The canonical entry point is a workspace-local wrapper. An illustrative Dogdou deployment is:

```text
L:\dogdou\
|-- dogdouspec.cmd
|-- tools\
|   `-- dogdouspec.exe
`-- .dogdouspec\
    `-- project.xml
```

The wrapper invokes the pinned tool binary. The CLI searches the current directory and its ancestors for `.dogdouspec/project.xml`, unless an explicit `--project-root` is supplied. The authoritative Dogdou workspace location and its Git ownership must be selected before self-hosting; the example does not decide that ownership.

The first Windows release should be distributed as a single executable. No global `PATH` change, MCP registration, background process, editor extension, or per-agent installation is required.

## 7. Workspace layout

```text
.dogdouspec\
|-- project.xml
|-- knowledge.xml
|-- policies.xml
|-- backlog.xml
|-- schema-lock.xml
|-- schemas\
|   |-- project.xsd
|   |-- knowledge.xsd
|   |-- policies.xsd
|   |-- backlog.xsd
|   |-- spec.xsd
|   |-- goal.xsd
|   |-- change.xsd
|   |-- findings.xsd
|   |-- evidence.xsd
|   `-- event.xsd
|-- iterations\
|   `-- ITER-YYYY-NNN-slug\
|       |-- spec.xml
|       |-- goal.xml
|       |-- findings.xml
|       |-- changes\
|       |   `-- CHG-NNN.xml
|       |-- evidence\
|       |   `-- manifest.xml
|       `-- events\
|           `-- YYYYMMDD-session-id.xml
`-- research\
    `-- RES-YYYY-NNN-slug\
        |-- spec.xml
        |-- goal.xml
        |-- findings.xml
        |-- changes\
        |-- evidence\
        |   `-- manifest.xml
        `-- events\
```

The exact XML schemas are deliberately deferred until the domain and lifecycle rules in this document are reviewed.

## 8. Authoritative document responsibilities

| Artifact | Owns | Must not own |
|---|---|---|
| `project.xml` | Project identity, repository catalog, work-item catalog, document links | Detailed task state or duplicated work status |
| `knowledge.xml` | Verified reusable facts, invariants, failure shields, glossary, approved knowledge | Current progress or unverified investigation notes |
| `policies.xml` | Normative project constraints, scopes, authority gates, exception rules | Implementation details or transient task decisions |
| `backlog.xml` | Deferred obligations that have formally left their originating work item | Open work still required by current acceptance |
| `spec.xml` | Objective, scope, non-goals, requirements, design constraints, acceptance contract | Mutable execution progress |
| `goal.xml` | Tasks, dependencies, leases, blockers, execution and verification state | Duplicated design prose or unbounded logs |
| `findings.xml` | Evidence-backed Findings and their resulting Issues for an Iteration or Research Work Item | Normative policy or an implicit implementation decision |
| `changes/CHG-*.xml` | Proposed semantic change, impact analysis, decision, and disposition | Product implementation work |
| `evidence/manifest.xml` | Evidence identity, provenance, hashes or artifact links, and coverage links | Acceptance decisions without their governing requirement |
| `events/*.xml` | Append-oriented mutation and session history | Authoritative current state |
| `AGENTS.md` | Bootstrap instructions, immediate safety rules, repository-native entry points | The complete project knowledge base or manually duplicated policy catalog |

## 9. Domain model

### 9.1 Work item

A Work Item is an independently governed Iteration or Research effort with its own specification, execution state, evidence, changes, and lifecycle.

### 9.2 Requirement

A Requirement defines a result that must be implemented or proved. It belongs to a specification revision and has stable identity and acceptance coverage.

### 9.3 Design element

A Design Element records an approved approach or boundary used to satisfy requirements. It is normative only within the specification and revision that owns it.

### 9.4 Task

A Task is a bounded execution unit that contributes to an approved specification. It has dependencies, an allowed scope, acceptance references, and a terminal disposition.

### 9.5 Finding

A Finding is an observed fact with provenance. Recording a Finding does not modify the specification or create implementation authority.

### 9.6 Issue

An Issue is an unresolved consequence of a Finding. Every Issue must receive an explicit disposition before its owning work item can complete.

### 9.7 Change

A Change proposes a semantic modification to an approved specification, policy, boundary, or acceptance contract. It carries impact analysis and an approval decision.

### 9.8 Evidence

Evidence is reproducible proof associated with a task, requirement, acceptance case, specification revision, environment, and producer. An artifact is not evidence until its provenance and coverage are recorded.

### 9.9 Backlog item

A Backlog Item is an obligation deliberately transferred out of the current work item. It must retain its source, risk, reason, acceptance impact, priority, and target or review condition.

## 10. Lifecycles

### 10.1 Work-item lifecycle

```text
Draft -> Ready -> Active -> Verifying -> Complete
                    |          |
                    v          v
                Replanning   Blocked
                    |
                    +-----> Active

Draft, Ready, Active, Replanning, or Blocked
    -> Cancelled or Superseded
```

`Replanning` means the approved execution model may no longer be valid. Affected tasks are frozen until the related Change is resolved.

### 10.2 Task lifecycle

```text
Pending -> Ready -> InProgress -> Verification -> Done
                        |
                        v
                     Blocked
```

Terminal non-success dispositions are:

- `Transferred`: responsibility moved to a successor task or work item.
- `Superseded`: the approved design changed and the task is no longer applicable.
- `Cancelled`: intentionally stopped without a successor obligation.

`Done` means the task's current acceptance references are satisfied. It must never mean deferred, abandoned, or merely implemented without verification.

### 10.3 Issue dispositions

Every Issue must resolve to exactly one of:

- `ResolvedInCurrent`
- `ExpandedToTask`
- `ResearchRequired`
- `ChangeRequired`
- `Transferred`
- `DeferredToBacklog`
- `AcceptedRisk`
- `Rejected`
- `Superseded`

## 11. Discovery and surprise protocol

Implementation discovery follows a fixed protocol:

```text
Observation
  -> Finding
  -> Issue and impact assessment
  -> one explicit disposition
```

### 11.1 Local elaboration

The current work item may add or split Tasks without changing the specification only when all of the following hold:

1. Objective, requirements, non-goals, design boundaries, and acceptance remain unchanged.
2. No new repository, component ownership, security authority, external side effect, or approval class is introduced.
3. Existing evidence remains applicable.
4. The additional work remains a bounded part of the current delivery.

This is planning elaboration, not a specification change.

### 11.2 Research extraction

Create a child Research Work Item when the correct change is not yet known, a hypothesis must be tested, or the impact cannot be bounded. The originating task becomes blocked by the Research Work Item. Research exits only when it produces a supported disposition, not merely a narrative report.

### 11.3 Material change

A Change is required when a discovery alters an approved objective, requirement, non-goal, design or ownership boundary, acceptance criterion, verification authority, or accepted risk. The affected work enters `Replanning` and affected tasks are frozen.

The Change must identify:

- The triggering Finding and Issue.
- Affected requirements, tasks, repositories, policies, and evidence.
- Alternatives considered.
- Proposed disposition.
- Approval authority.
- Evidence invalidated or requiring revalidation.

### 11.4 Successor iteration

Create a successor Iteration rather than expanding the current one when any of the following are true:

- The objective or a critical requirement changes.
- A core architecture, ABI, security, or ownership boundary is replaced.
- Material completed evidence becomes invalid.
- A new independently governed subsystem or cross-repository delivery appears.
- Verification, release, or authorization boundaries materially change.
- The original acceptance contract can no longer explain the resulting delivery.

The previous Iteration becomes `Superseded` and maps each task to `Done`, `Transferred`, `Superseded`, or `Cancelled`. History is preserved.

### 11.5 Deferral

An Issue may move to the project backlog only when it does not prevent the current specification's required acceptance. A deferred item must record its source, reason, risk, acceptance impact, owner, priority, and target Work Item or review condition.

Work that is still required for current acceptance cannot be made deferrable by changing only task state. It requires an approved specification Change.

## 12. Completion predicates

An Iteration is eligible for `Complete` only when:

1. Every required acceptance case for the current specification revision is satisfied.
2. Every Task has a valid terminal state.
3. Every Finding and Issue has a complete disposition.
4. No unresolved Change or acceptance-affecting blocker remains.
5. Every transferred task names a resolvable successor.
6. Every deferred Issue has a valid backlog record.
7. Every accepted risk has the required approval.
8. Evidence is valid for the current specification revision and declared environment.
9. All project links and semantic validation checks pass.

Eligibility is a technical readiness result, not a completion decision. Task
state, validation, and evidence may be advanced or evaluated automatically, but
they must not automatically accept product requirements or transition an
Iteration to `Complete`. Product acceptance dispositions and Iteration
completion require an explicit owner confirmation recorded in the
specification. An Agent may prepare the readiness report and proposed follow-up
options, then must stop at this decision gate.

A Requirement itself is not `Complete`: it is proposed, approved, superseded,
or withdrawn. Technical records establish coverage and readiness. The separate
product acceptance decision establishes whether the delivered outcome satisfies
the approved Requirement.

Research is eligible for `Complete` only when each research question has a supported answer or an explicit unresolved disposition, and its findings lead to a decision, Change, Task, Backlog Item, or justified rejection.

## 13. Policy, knowledge, and agent-guide boundary

`policies.xml` is the canonical source for normative project constraints. `knowledge.xml` is the canonical source for verified descriptive knowledge. Knowledge does not become mandatory merely because it is recorded; promotion to Policy is an explicit governed action.

`AGENTS.md` remains a small bootstrap and safety surface. It tells an Agent how to invoke DogdouSpec, what must happen before product edits, and which emergency constraints apply before any context query is possible.

High-risk mandatory policies may appear in an automatically generated digest inside `AGENTS.md`. The digest carries the source policy revision and hash. It is a cache, not an independent authority. A mismatch is a validation error.

## 14. Scoped implementation context

Before product work, an Agent requests a context package:

```powershell
dogdouspec context --repo dev.core --work ITER-2026-001 --task T-014 --intent implementation
```

The package contains only:

1. Applicable specification requirements and acceptance cases.
2. Task scope, dependencies, lease, and allowed repositories or paths.
3. Applicable mandatory policies and gates.
4. Relevant verified knowledge.
5. Repository-native build and verification entry points.
6. Explicit stop conditions.

The package is derived and reports all source document revisions. It is not persisted as a new source of truth.

## 15. CLI capability boundary

The first release should expose domain operations instead of unrestricted file mutation. The initial command families are:

```text
dogdouspec init
dogdouspec validate
dogdouspec query
dogdouspec search
dogdouspec context
dogdouspec iteration readiness
dogdouspec iteration confirm

dogdouspec repo add|list|remove
dogdouspec work create|list|show|transition
dogdouspec spec show|import
dogdouspec task add|split|claim|release|transition
dogdouspec finding record
dogdouspec issue open|disposition
dogdouspec change propose|assess|approve|reject
dogdouspec evidence record|list
dogdouspec backlog add|schedule|list
dogdouspec knowledge add|promote|retire
dogdouspec policy propose|approve|retire|list|check
```

Raw XPath query is supported. Raw XPath mutation is not part of the default v1 Agent interface. Domain commands must enforce cardinality, revision, lifecycle, policy, and transaction rules.

`iteration readiness` is read-only. `iteration confirm` is the dedicated write
path for protected product decisions; generic Task automation and technical
completion commands cannot invoke it as an automatic side effect.

`spec import` accepts a complete candidate document for a Draft Work Item and replaces that Draft specification through validation and a revisioned transaction. Once a Work Item leaves `Draft`, material specification updates occur only through an approved Change.

All commands support stable JSON output. Mutating commands return changed document references, previous and new revisions, and event identifiers.

## 16. Confirmed v1 implementation baseline

The following choices are fixed for v1. Changing one requires an explicit design amendment rather than an implementation-local substitution.

### 16.1 Platform, language, and solution layout

- Runtime target: .NET 10 (`net10.0`).
- Language: C# 14 with nullable reference types enabled.
- Bootstrap SDK: `10.0.303`, pinned by `global.json` with latest-patch roll-forward.
- Supported v1 operating system: Windows 11 x64.
- First runtime identifier: `win-x64`.
- Release packaging: self-contained Native AOT executable named `dogdouspec.exe`.
- Repository wrapper: `dogdouspec.cmd`.
- Debug development uses the normal JIT build for fast iteration; Release acceptance includes Native AOT publish and smoke execution.
- Trimming, single-file, and AOT analyzer warnings are treated as errors in the CLI and Core projects.

The initial solution layout is:

```text
DogdouSpec.slnx
Directory.Build.props
Directory.Packages.props
global.json
src\
|-- DogdouSpec.Core\
`-- DogdouSpec.Cli\
tests\
|-- DogdouSpec.Core.Tests\
|-- DogdouSpec.Cli.Tests\
`-- DogdouSpec.EndToEnd.Tests\
```

`DogdouSpec.Core` owns all domain, XML, validation, and transaction behavior. `DogdouSpec.Cli` owns only command definition, input binding, invocation, and rendering.

### 16.2 Dependencies

- CLI parsing: `System.CommandLine` 2.0.11, centrally pinned.
- XML, XPath, XSD, and JSON: .NET 10 BCL `System.Xml`, `System.Xml.XPath`, `System.Xml.Schema`, and `System.Text.Json`.
- JSON serialization: `System.Text.Json` source generation; reflection-based serialization is not used in the Native AOT path.
- Tests: MSTest 4.3.3 on Microsoft Testing Platform, centrally pinned.
- Package restore: NuGet lock files are committed and CI/build restore uses locked mode.

No dependency-injection, logging, database, ORM, scripting, or alternate XML package is part of v1. Core services use explicit construction. Diagnostics use internal typed records rendered by the CLI.

### 16.3 XML and XSD contract

- Managed documents use XML 1.0, UTF-8 without BOM, CRLF line endings, two-space indentation, and a final newline.
- V1 managed documents use no XML namespace. Each root carries an explicit `schema_version` attribute. Namespace introduction requires a future schema migration.
- Validation uses W3C XML Schema 1.0 through `XmlSchemaSet`.
- Schemas are loaded only from `.dogdouspec/schemas` according to `schema-lock.xml`; remote schema resolution and unlisted imports/includes are prohibited.
- Documents are loaded through a hardened `XmlReader` into `XmlDocument` with formatting-only whitespace discarded, then queried through `XPathNavigator`.
- Managed schemas prohibit mixed content. The CLI owns deterministic serialization of managed documents. Direct hand edits are not a supported mutation path except for a complete Draft document subsequently accepted by `spec import`.
- Narrative fields may use CDATA, but CDATA has no distinct domain meaning and must round-trip as the same text value.

Default resource limits are:

- 16 MiB per managed XML document.
- 128 element nesting levels.
- 4,096 characters per XPath expression.
- 200 returned nodes by default and 10,000 as the hard per-query maximum.

Limits are constants in v1, reported in `dogdouspec --version --format json`, and may be changed only by a new tool release.

### 16.4 XPath contract

V1 supports XPath 1.0 as implemented by .NET `XPathNavigator` and `XPathExpression`.

Supported result types are:

- Node set, returned in document order.
- Boolean.
- Number. Finite values are JSON numbers; `NaN`, positive infinity, and negative infinity use `value: null` plus `specialValue: "NaN"`, `"PositiveInfinity"`, or `"NegativeInfinity"`.
- String.

The public read operations are:

```text
dogdouspec query --document <document-ref> --xpath <expression>
dogdouspec search --scope <project|work-item|document-kind> --xpath <expression>
```

`query` evaluates against exactly one cataloged document. `search` evaluates the same expression independently against each selected document and returns groups ordered by canonical document reference. XPath never crosses document boundaries.

Each node match reports its document reference, node type, name, generated absolute positional XPath, and value. Element matches also include a deterministic XML fragment. The JSON contract does not claim a bidirectional or lossless XML-to-JSON object mapping; the XML fragment remains authoritative.

V1 does not support XPath 2.0 or 3.x, XQuery, XSLT, variables, extension functions, custom functions, or external resource functions. No custom `XsltContext` is installed. Persisted documents avoid namespaces, so public XPath does not require a namespace map.

Raw XPath is read-only. Internal domain mutations use typed selectors and require an exact expected match count, normally exactly one.

### 16.5 CLI contract

The canonical command name is `dogdouspec`. Commands and long option names are lowercase and case-sensitive. V1 provides no short aliases except standard help and version forms. Response files, parser directives, automatic abbreviations, and implicit command aliases are disabled.

Global options are:

```text
--project-root <path>
--format <human|json>
--no-color
--verbose
--help
--version
```

Commands are non-interactive. Missing information is a usage error rather than a prompt. Approval-gated operations require an already recorded approval reference; `--yes` or an Agent assertion cannot bypass authority.

Stdout contains the requested human or JSON result. Stderr contains diagnostics and verbose operational messages. JSON mode never emits prose, ANSI control sequences, or logs to stdout.

Stable exit codes are:

| Code | Meaning |
|---:|---|
| 0 | Success |
| 2 | Command syntax or input-binding error |
| 3 | Workspace or document not found |
| 4 | XML, schema, link, lifecycle, or domain validation failure |
| 5 | Revision conflict or lock conflict |
| 6 | Filesystem, transaction, or recovery failure |
| 7 | Authorization or approval required |
| 8 | Unexpected internal failure |

### 16.6 JSON contract

Every JSON response uses a versioned envelope:

```json
{
  "contractVersion": "1.0",
  "command": "query",
  "success": true,
  "data": {},
  "diagnostics": []
}
```

Property names use camel case. Domain enums are serialized as their documented string names. Timestamps use UTC ISO 8601 round-trip format. Paths returned for managed documents are canonical workspace-relative paths with `/` separators; native absolute paths appear only in explicitly diagnostic fields.

Diagnostics contain a stable code, severity, message, document reference, object ID when available, and safe remediation text. Stack traces are omitted unless `--verbose` is present and the failure is internal.

### 16.7 Revision, locking, and transaction contract

- Every managed document root carries an unsigned 64-bit `revision`, starting at 1 and incremented once per committed transaction that changes that document.
- A mutation of an existing document requires its expected revision. A multi-document command supplies or internally derives an expected revision for every changed document before acquiring the write lock, then rechecks them under the lock.
- `.dogdouspec/.state/workspace.lock` provides a project-level interprocess readers-writer boundary. Readers may coexist; a writer is exclusive.
- Transaction data is staged under `.dogdouspec/.state/transactions/<transaction-id>/` on the same volume as the target workspace.
- The journal records transaction ID, state, targets, old and new revisions, and SHA-256 hashes of staged content.
- Before the journal reaches `Prepared`, recovery deletes the incomplete staging directory. After `Prepared`, recovery validates staged hashes and rolls the transaction forward idempotently. If staged data is invalid or missing, recovery restores retained originals and reports exit code 6.
- Existing files use same-volume replacement; new files use same-volume move. Originals and staged files remain available until the journal reaches `Committed`.
- Readers never observe an in-progress multi-document commit because they participate in the workspace lock.

### 16.8 Build and test entry points

The repository exposes one Windows build entry point:

```text
agent.build.cmd Debug
agent.build.cmd Release
```

Debug restores in locked mode, builds with warnings as errors, and runs all non-packaging tests. Release performs the Debug checks, publishes `win-x64` Native AOT, and runs the packaged CLI smoke suite from a temporary clean workspace.

## 17. Storage, security, and transaction requirements

The core must:

- Resolve document references only through the project catalog or a workspace-contained path.
- Reject traversal outside the workspace unless a read-only repository path is explicitly cataloged.
- Disable DTD processing and external entity resolution.
- Apply XML size and complexity limits.
- Validate XSD and semantic invariants before commit.
- Require an expected revision for updates to existing documents.
- Enforce explicit match cardinality for every mutation.
- Acquire a project-level interprocess write lock.
- Stage all files involved in a domain transaction before replacement.
- Maintain a recoverable transaction journal for multi-file mutations.
- Never return success if only part of a multi-file operation was committed.
- Preserve user-authored files outside `.dogdouspec`.

Git supplies durable historical review, but correctness must not depend on every state mutation being committed immediately.

## 18. Traditional bootstrap and self-hosting boundary

The first usable release is designed and implemented through the repository's traditional development process. DogdouSpec must not claim to manage its own implementation before the following bootstrap gate passes:

1. Workspace initialization succeeds from an empty directory.
2. All v1 document types validate.
3. Domain links and lifecycle transitions are enforced.
4. A complete Iteration can be created, executed, changed, evidenced, and completed through the CLI.
5. A discovery can be expanded, researched, changed, superseded, or deferred without direct XML editing.
6. Crash recovery and revision conflicts have automated tests.
7. The repository-local executable and wrapper work without global installation.
8. The generated context package and `AGENTS.md` policy digest are validated.

After this gate, the DogdouSpec repository creates its own `.dogdouspec` workspace, imports this design and the implementation plan as the first approved specification, reconciles actual code and evidence, and switches subsequent development to the new workflow.

## 19. Deferred design decisions

The following are intentionally left for implementation tasks rather than fixed prematurely:

- Exact XML element and attribute shapes.
- Authoritative Dogdou workspace location and Git ownership.
- Exact transaction journal XML element and attribute shapes.
- Exact policy exception approval representation.
- Optional signatures or hash chains for evidence and events.
- Optional MCP, verifier, or user-interface adapters after self-hosting.

## 20. External technology references

- [System.CommandLine overview](https://learn.microsoft.com/en-us/dotnet/standard/commandline/)
- [System.CommandLine package](https://www.nuget.org/packages/System.CommandLine/2.0.11)
- [Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [XPathExpression compilation and result types](https://learn.microsoft.com/en-us/dotnet/api/system.xml.xpath.xpathexpression.compile?view=net-10.0)
- [XmlSchemaSet validation](https://learn.microsoft.com/en-us/dotnet/standard/data/xml/xmlschemaset-for-schema-compilation)
- [MSTest overview](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-intro)
