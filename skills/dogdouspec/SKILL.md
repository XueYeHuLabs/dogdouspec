---
name: dogdouspec
description: Comprehensive workflow instructions for managing project iterations, tasks, and authoritative XML artifacts in DogdouSpec workspaces using the repo-local CLI. Use when starting, executing, querying, updating, or completing tasks in a DogdouSpec workspace; do not use for generic ad-hoc XML editing outside DogdouSpec.
---

# DogdouSpec Workflow Guide

DogdouSpec is a repository-local, iteration-first specification and task management system. Authoritative state is stored in XML documents under `.dogdouspec/` and validated against XSD v1 schemas.

## Core Invariants

1. **Repository-Local Execution**: Use `.\dogdouspec.cmd` (Windows) or `dotnet run --project src/DogdouSpec.Cli/DogdouSpec.Cli.csproj --` (cross-platform). No global tools, background daemons, or MCP servers are required.
2. **Never Edit Managed XML Directly**: Never edit, write, or copy `.dogdouspec/*.xml` using text editors or scripts. All mutations must pass through the public CLI (`task update`, `append`, `transaction apply`, `iteration confirm`).
3. **Two-Phase Query Pattern**: Query compact indexes first (`ds:filter`), then load full task details only for the active task. Never load whole task graphs into context.
4. **Exact Revisions & Post-Mutation Re-Query**: Always pass exact expected revisions to mutating commands. After each mutation, validate the workspace with `dogdouspec validate` and re-query target documents.
5. **Respect Authority Boundaries**: Technical task automation never auto-completes product requirements, design decisions, acceptance criteria, or iterations. Stop and prompt the owner when product decisions are needed.

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

### 5. Post-Mutation Re-query & Validation

Always re-validate the workspace and re-query after writing:

```powershell
.\dogdouspec.cmd validate --format xml
.\dogdouspec.cmd query --document "<ITERATION_ID>/tasks.xml" --xpath "/tasks/task[@id='<TASK_ID>']/@status" --format xml
```

---

## Supporting References

Read these dedicated reference guides for specialized operations:

- **[XPath Query & Projection Reference](references/xpath.md)**: Read when writing XPath queries, using variables, or applying `ds:filter` / `ds:filter-out` member projections.
- **[Mutation Operations Reference](references/mutations.md)**: Read when choosing between `append`, `task update`, and `transaction apply`, handling revisions, or resolving idempotency conflicts.
- **[Authority & Lifecycle Reference](references/authority.md)**: Read when checking iteration readiness, processing owner confirmations, or handling surprises and replanning.