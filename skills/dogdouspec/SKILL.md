---
name: dogdouspec
description: Comprehensive workflow instructions for managing project iterations, tasks, and authoritative XML artifacts in DogdouSpec workspaces using the repo-local CLI. Use when starting, executing, querying, updating, or completing tasks in a DogdouSpec workspace; do not use for generic ad-hoc XML editing outside DogdouSpec.
---

# DogdouSpec Workflow Guide

DogdouSpec is a repository-local, iteration-first specification and task management system. Authoritative state is stored in XML documents under `.dogdouspec/` and validated against XSD v1 schemas.

## Core Invariants

1. **Repository-Local Execution**: Use `.\dogdouspec.cmd` (Windows) or `dotnet run --project src/DogdouSpec.Cli/DogdouSpec.Cli.csproj --` (cross-platform). No global tools, background daemons, or MCP servers are required.
2. **Never Edit Managed XML Directly**: Never edit, write, or copy `.dogdouspec/*.xml` using text editors or scripts. All mutations must pass through the public CLI (`task update`, `task add`, `task revise`, `task split`, `requirement propose`, `change propose`, `change apply`, `append`, `transaction apply`, `iteration confirm`).
3. **Two-Phase Query Pattern**: Query compact indexes first (`ds:filter`), then load full task details only for the active task. Never load whole task graphs into context.
4. **Exact Revisions & Post-Mutation Re-Query**: Always pass exact expected revisions to mutating commands. After each mutation, validate the workspace with `dogdouspec validate` and re-query target documents.
5. **Respect Authority Boundaries**: Technical task automation never auto-completes product requirements, design decisions, acceptance criteria, or iterations. Stop and prompt the owner when product decisions are needed.
6. **Terminal Task Immutability**: Tasks in `done`, `transferred`, `superseded`, or `cancelled` statuses are immutable; low-level edits and execution transitions fail with `TASK_IMMUTABLE`. Only append-only informational records may be added.
7. **Replanning Execution Freeze**: When an iteration is in `status="replanning"`, task execution transitions (`start`, `resume`, `verify`, `complete`) fail closed with `ITERATION_REPLANNING_EXECUTION_FROZEN`. Technical planning helpers (`task add`, `task split`, `change apply`) and terminal dispositions remain enabled.

---

## Standard Agent Workflow Loop

```mermaid
flowchart TD
    A["1. Workspace Discovery & Validation"] --> B["2. Index-First Task Selection"]
    B --> C["3. Load Full Selected Task"]
    C --> D["4. Execute & Verify Code (build.cmd)"]
    D --> E["5. Task Update (verify -> complete)"]
    E --> F["6. Validate Workspace & Check Next Task"]
```

### 1. Workspace Discovery & Validation

Verify workspace health and list active iterations:

```powershell
.\dogdouspec.cmd workspace discover --format xml
.\dogdouspec.cmd validate --format xml
.\dogdouspec.cmd iteration list --format xml
```

### 2. Index-First Task Selection (Two-Phase Query)

Use two explicit compact queries to derive the next actionable task:

1. **Resume In-Progress or Verification Task** (Highest Priority):
   ```powershell
   .\dogdouspec.cmd query --document "<ITERATION_ID>/tasks.xml" --xpath "ds:filter(/tasks/task[@status='in-progress' or @status='verification'][1], '@id', '@status', '@agent', 'index')" --format xml
   ```

2. **Next Ready Pending Task** (If no task is in-progress or in verification):
   ```powershell
   .\dogdouspec.cmd query --document "<ITERATION_ID>/tasks.xml" --xpath "ds:filter(/tasks/task[@status='pending' and not(dependencies/ref[@relation='depends-on']/@target = /tasks/task[@status!='done' and @status!='transferred' and @status!='superseded' and @status!='cancelled']/@id)][1], '@id', '@status', '@agent', 'index')" --format xml
   ```

*Note: Blocked tasks (`@status='blocked'`) are reported separately to resolve blockers or escalate.*

### 3. Load Full Selected Task

Load the complete task document by ID:

```powershell
.\dogdouspec.cmd query --document "<ITERATION_ID>/tasks.xml" --xpath "/tasks/task[@id='<TASK_ID>']" --format xml
```

Identify objectives, scope includes/excludes, origin requirement, acceptance criteria, constraints, and previous records before modifying code.

### 4. Technical Execution & State Transitions

1. **Start Task** (transitions `pending` -> `in-progress`):
   ```powershell
   Get-Content update_start.xml -Raw | .\dogdouspec.cmd task update --iteration "<ITERATION_ID>" --task "<TASK_ID>" --expected-revision <REV> --stdin --format xml
   ```
2. **Implement & Build**:
   - Make necessary code and test changes.
   - Run `.\build.cmd` to compile and execute all tests.
3. **Verify Task** (transitions `in-progress` -> `verification`):
   ```powershell
   Get-Content update_verify.xml -Raw | .\dogdouspec.cmd task update --iteration "<ITERATION_ID>" --task "<TASK_ID>" --expected-revision <REV> --stdin --format xml
   ```
4. **Complete Task** (transitions `verification` -> `done`):
   ```powershell
   Get-Content update_complete.xml -Raw | .\dogdouspec.cmd task update --iteration "<ITERATION_ID>" --task "<TASK_ID>" --expected-revision <REV> --stdin --format xml
   ```

### 5. Task & Requirement Change Decision Tree

When requirements, scope, or architecture need adjustment during an iteration:

- **Elaborating an Active or Pending Task**: Use `dogdouspec task revise` to add constraints, acceptance criteria, or dependencies, and append discussion records without modifying decided product scope. Once a task has started, retain its rationale and only submit an additive scope expansion; record changed reasoning as discussion.
- **Adding a New Technical Task**: Use `dogdouspec task add` to add a pending task referencing an existing approved requirement.
- **Splitting a Complex Task**: Use `dogdouspec task split` to mark the parent task `superseded`, `transferred`, or `cancelled` and add 2+ focused pending subtasks atomically.
- **Proposing a New Requirement**: Use `dogdouspec requirement propose` to add a requirement with `status="proposed"`. (Technical agents cannot self-approve; owner confirmation via `iteration confirm` is required).
- **Handling Mid-Flight Surprises / Material Scope Gaps**:
  1. Use `dogdouspec change propose` to attach an active finding record to the task, freeze target tasks to `blocked`, and add proposed requirements to `spec.xml`.
  2. Ask the human product owner to confirm replanning (`dogdouspec iteration confirm --stdin` with `action="replan"`).
  3. During `status="replanning"`, use `dogdouspec change apply` to resolve active findings, apply task dispositions (`superseded`/`transferred`/`cancelled`), and add successor tasks.
  4. Ask the product owner to confirm continuation (`dogdouspec iteration confirm --stdin` with `action="continue"`).

### 6. Post-Mutation Re-query & Validation

Always re-validate the workspace and re-query after writing:

```powershell
.\dogdouspec.cmd validate --format xml
.\dogdouspec.cmd query --document "<ITERATION_ID>/tasks.xml" --xpath "/tasks/task[@id='<TASK_ID>']/@status" --format xml
```

---

## Supporting References

Read these dedicated reference guides for specialized operations:

- **[XPath Query & Projection Reference](references/xpath.md)**: Read when writing XPath queries, using variables, or applying `ds:filter` / `ds:filter-out` member projections.
- **[Mutation Operations Reference](references/mutations.md)**: Read when choosing between `task update`, `task add`, `task revise`, `task split`, `requirement propose`, `change propose`, `change apply`, `append`, and `transaction apply`.
- **[Authority & Lifecycle Reference](references/authority.md)**: Read when checking iteration readiness, processing owner confirmations, or handling surprises and replanning.
