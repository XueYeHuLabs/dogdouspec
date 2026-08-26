# DogdouSpec

DogdouSpec is an iteration-first structured XML/XPath specification and technical execution engine designed for human and AI pairing.

## 1. Baseline and Requirements

- .NET SDK `10.0.303`, target framework `net10.0`, C# 14.
- `System.CommandLine` 2.0.11 for CLI parsing.
- Built-in `System.Xml`, `System.Xml.XPath`, and `XmlSchemaSet`.
- MSTest 4.0.2 for unit and integration testing.

## 2. Build and Test

Run the repository build and test script:

```cmd
build.cmd
```

Or using the .NET CLI:

```cmd
dotnet build DogdouSpec.slnx -c Debug
dotnet test DogdouSpec.slnx -c Debug --no-build
```

## 3. CLI Wrapper (`dogdouspec.cmd`)

The repository-local wrapper `dogdouspec.cmd` runs the CLI adapter directly:

```cmd
dogdouspec.cmd <command> [options]
```

### Implemented Commands

1. **Workspace Discovery**
   ```cmd
   dogdouspec.cmd workspace discover [--workspace-root PATH] [--format xml|human]
   ```
   Walks ancestor directories to find the nearest `.dogdouspec` directory, or checks explicit `--workspace-root`.

2. **Workspace Initialization**
   ```cmd
   dogdouspec.cmd workspace init [--workspace-root PATH] [--format xml|human]
   ```
   Atomically initializes `.dogdouspec`, `_schema/` (with readable XSD copies), `_skill/`, and initial valid `knowledge.xml` and `backlog.xml`. Fails closed without overwriting existing state.

3. **Schema Display**
   ```cmd
   dogdouspec.cmd schema show --name <NAME> [--version 1.0]
   ```
   Outputs exact schema XML to standard output for inspection or redirection.
   Available schemas: `spec`, `tasks`, `knowledge`, `backlog`, `requests`, `common`.

4. **Iteration Listing**
   ```cmd
   dogdouspec.cmd iteration list [--workspace-root PATH] [--format xml|human]
   ```
   Discovers and inspects direct date-prefixed candidate iteration directories in normalized name order, returning compact metadata (ID, path, kind, status, spec revision, tasks revision, and index) without loading full task bodies. Malformed candidates are reported as structured diagnostics.

5. **Iteration Creation**
   ```cmd
   dogdouspec.cmd iteration create --id YYYYMMDD-name --kind feature|research [--workspace-root PATH] [--format xml|human]
   ```
   (Alias: `dogdouspec.cmd iteration new`)
   Atomically creates a new feature or research iteration directory with valid `spec.xml` and `tasks.xml` draft structures, project-unique deterministic time-first IDs, schema version 1.0, and revision 1. Employs workspace writer locking, startup recovery, prospective whole-workspace validation, and same-volume atomic publication.

6. **Iteration Readiness**
   ```cmd
   dogdouspec.cmd iteration readiness --iteration ID --phase activation|completion [--workspace-root PATH] [--format xml|human]
   ```
   Deterministically evaluates and reports technical gating conditions and pending product decisions for activation or completion review without mutating document state.

7. **Iteration Confirmation**
   ```cmd
   dogdouspec.cmd iteration confirm (--stdin|--file PATH) [--workspace-root PATH] [--format xml|human]
   ```
   Authoritatively applies owner-instructed iteration lifecycle transitions and product requirement/design/acceptance decisions (`activate`, `accept-design-change`, `replan`, `continue`, `complete`, `cancel`, `supersede`) to `spec.xml`.

8. **Template Display**
   ```cmd
   dogdouspec.cmd template show --name <NAME> [--version 1.0]
   ```
   Outputs exact template XML to standard output for inspection or redirection.
   Available templates: `record.discussion`, `record.finding`, `record.verification`, `task.update`, `task.review`, `task.add`, `task.revise`, `task.split`, `requirement.propose`, `change.propose`, `change.apply`, `transaction.apply`, `iteration.confirmation`, `knowledge.entry`, `backlog.item`.

9. **Validation**
   ```cmd
   dogdouspec.cmd validate [--workspace-root PATH] [--iteration ID] [--document RELATIVE_PATH] [--format xml|human]
   ```
   Validates managed XML documents securely against embedded authoritative XSD schemas and semantic rules (project-wide ID uniqueness, time-first grammar, document ownership, forward reference scoping and narrowest scope, task dependency acyclicity, task done predicates, and confirmation provenance). Supports whole workspace, iteration, and single document scopes.

10. **XPath Query**
    ```cmd
    dogdouspec.cmd query --document REF --xpath EXPR [--var name=value ...] [--workspace-root PATH] [--format xml|human]
    ```
    Evaluates an XPath 1.0 expression against a single managed document with support for string variables (`--var name=value`) and projection extension functions (`ds:filter`, `ds:filter-out`).

11. **Scoped Search**
    ```cmd
    dogdouspec.cmd search --scope project|iteration [--iteration ID] --xpath EXPR [--var name=value ...] [--workspace-root PATH] [--format xml|human]
    ```
    Evaluates an XPath 1.0 expression independently across all managed documents in a scope in deterministic normalized relative-path order.

12. **Generic Append**
    ```cmd
    dogdouspec.cmd append --document REF --parent-xpath EXPR [--var name=value ...] --expected-revision N (--stdin|--file PATH) [--workspace-root PATH] [--format xml|human]
    ```
    Atomically appends a complete schema-valid element with a project-unique time-first ID to a managed document under a single selected parent element. Enforces protected-state authority rules, prospective whole-workspace validation, deterministic root revision increment, and identity-based idempotency.

13. **Task Operations (`task update`, `task review`, `task add`, `task quick`, `task revise`, `task split`)**
    ```cmd
    dogdouspec.cmd task update --iteration ID --task TASK_ID --expected-revision N (--stdin|--file PATH) [--workspace-root PATH] [--format xml|human]
    dogdouspec.cmd task review --iteration ID --task TASK_ID --expected-revision N (--stdin|--file PATH) [--workspace-root PATH] [--format xml|human]
    dogdouspec.cmd task add --iteration ID --expected-revision N (--stdin|--file PATH) [--workspace-root PATH] [--format xml|human]
    dogdouspec.cmd task quick --title TEXT --scope PATH --done-when TEXT --why TEXT [--origin REQUIREMENT ...] [--depends-on TASK ...] [--term key=value ...] [--agent NAME] [--review-required] [--iteration ID] [--expected-revision N] [--start] [--dry-run] [--id TASK_ID] [--operation-id ID] [--workspace-root PATH] [--format xml|human]
    dogdouspec.cmd task revise --iteration ID --task TASK_ID --expected-revision N (--stdin|--file PATH) [--workspace-root PATH] [--format xml|human]
    dogdouspec.cmd task split --iteration ID --task TASK_ID --expected-revision N (--stdin|--file PATH) [--workspace-root PATH] [--format xml|human]
    ```
    High-level schema-aware task mutations. Enforces legal task state transitions, terminal task immutability, replanning execution freezes, origin requirement verification, durable record stamping, and revision concurrency.
    A task with `review required="true"` cannot complete until its latest structured review is approved by an actor different from immutable task `@agent` attribution. This separation records provenance only; it is not cryptographic authentication or authorization proof. `changes-requested` creates an active blocking finding and returns the task to `in-progress`.
    `task quick` is only a compact input helper: it persists a normal Task. Without `--origin` it creates one operational `supports` origin to the current active iteration; with origins it creates `implements` edges to Requirements. `--start` writes the final in-progress task, start record, and receipt in one tasks.xml revision. `--dry-run` prints the generated request without writing.

14. **Requirement Proposal (`requirement propose`)**
    ```cmd
    dogdouspec.cmd requirement propose --iteration ID --expected-revision N (--stdin|--file PATH) [--workspace-root PATH] [--format xml|human]
    ```
    Appends a new proposed requirement (`status="proposed"`) to `spec.xml`. Non-proposed statuses fail with `OWNER_DECISION_REQUIRED`.

15. **Mid-flight Change Operations (`change propose`, `change apply`)**
    ```cmd
    dogdouspec.cmd change propose --iteration ID --expected-spec-revision N --expected-tasks-revision M (--stdin|--file PATH) [--workspace-root PATH] [--format xml|human]
    dogdouspec.cmd change apply --iteration ID --expected-spec-revision N --expected-tasks-revision M (--stdin|--file PATH) [--workspace-root PATH] [--format xml|human]
    ```
    Multi-document atomic mutations across `spec.xml` and `tasks.xml`. `change propose` attaches active finding records, freezes target tasks to `blocked`, and proposes new requirements. `change apply` runs during `status="replanning"` to resolve active findings, apply terminal task dispositions, and add successor tasks.

16. **Low-level Transaction Apply**
    ```cmd
    dogdouspec.cmd transaction apply (--stdin|--file PATH) [--workspace-root PATH] [--format xml|human]
    ```
    Applies bounded, revision-checked XPath 1.0 assertions and sequential append/replace/set/remove operations across one or more managed XML documents. Changed documents are prospectively validated and committed through the recovery-backed transaction core. Protected product decisions, root revisions, projection clones, and durable Task-update receipts remain non-mutable.

### PowerShell Invocation Guidelines

When invoking `dogdouspec.cmd` from PowerShell:

1. **Simple variable XPath in single quotes**
   Use single quotes to prevent PowerShell from expanding `$variables`:
   ```powershell
   .\dogdouspec.cmd query --document 20260823-xpath-core/tasks.xml --var task_id=20260823-task-xpath-projection --xpath '//task[@id=$task_id]'
   ```

2. **Complex XPath with embedded string literals**
   Represent embedded XPath single-quoted literals by doubling single quotes (`''`) within the outer PowerShell single-quoted string:
   ```powershell
   .\dogdouspec.cmd query --document 20260823-xpath-core/tasks.xml --var task_id=20260823-task-xpath-projection --xpath 'ds:filter(//task[@id=$task_id], ''@id'', ''@status'', ''index'')'
   ```

3. **Outer double quotes with backtick-escaped variables**
   If outer double quotes are used in PowerShell, escape the `$` with a backtick (`` `$ ``) to prevent shell expansion:
   ```powershell
   .\dogdouspec.cmd query --document 20260823-xpath-core/tasks.xml --var task_id=20260823-task-xpath-projection --xpath "ds:filter(//task[@id=`$task_id], '@id', '@status', 'index')"
   ```

## 4. Exit Codes

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

## 5. Installation in Other Repositories

To deploy and use DogdouSpec in an existing Windows Git repository, follow the verified step-by-step procedure in [docs/INSTALL_IN_OTHER_REPOSITORY.md](docs/INSTALL_IN_OTHER_REPOSITORY.md).

The disposition and executable evidence for all 15 findings from the DogdouClix
evaluation are recorded in [docs/DOGDOUCLIX_DOGFOOD_REMEDIATION.md](docs/DOGDOUCLIX_DOGFOOD_REMEDIATION.md).

## 6. Architectural Boundaries & Workflow

- **Repository-Local State**: Authoritative specification and task state is stored entirely within `.dogdouspec/` XML documents and validated against embedded XSD v1 schemas.
- **Authority Boundaries**: Technical agents manage task lifecycles, execution records, and code changes autonomously. Product requirements, design decisions, and iteration completions require explicit human owner confirmation via `iteration confirm`.
- **Coding Agent Workflow**: See [`AGENTS.md`](AGENTS.md) and [`.agents/skills/dogdouspec/SKILL.md`](.agents/skills/dogdouspec/SKILL.md) for workflow integration rules and compact two-phase query patterns.
