# XPath Query & Projection Reference

DogdouSpec provides a safe, deterministic XPath 1.0 engine with custom projection functions, variable bindings, and strict execution bounds to prevent memory exhaustion and context window overflow.

## Two-Phase Query Pattern

Agents must always follow the two-phase query pattern:

1. **Phase 1: Compact Index Selection**:
   Query minimal attributes and index metadata across candidate tasks:
   ```powershell
   dogdouspec query --document "20260823-dogdouspec-v1/tasks.xml" --xpath "ds:filter(/tasks/task[@status='in-progress' or @status='verification'][1], '@id', '@status', '@agent', 'index')" --format xml
   ```
   If empty, use the project-aware readiness selector (required):
   ```powershell
   dogdouspec task next --iteration "20260823-dogdouspec-v1" --format xml
   ```

   A pending-task XPath is document-local and cannot establish readiness across iterations.

2. **Phase 2: Selected Task Context & Effective Constraints**:
   Read context before starting or resuming execution:
   ```powershell
   dogdouspec task context --iteration "20260823-dogdouspec-v1" --task "20260823-task-dogfood-review" --no-history --format xml
   ```

   Read constraints from `spec.xml` (`scope="iteration"`) and the Task (`scope="task"`); both apply with no implicit override. Raw Task queries and `task show` omit iteration constraints. If essential context exceeds the limit, increase `--max-bytes` instead of bypassing this read. Load additional detail or history only as needed.

## Projection Functions: `ds:filter` and `ds:filter-out`

DogdouSpec introduces two custom XPath extension functions in the `ds:` namespace:

- `ds:filter(nodeset, member1, member2, ...)`: Returns a projected clone containing only the specified member attributes and child elements.
- `ds:filter-out(nodeset, member1, member2, ...)`: Returns a projected clone excluding the specified member attributes and child elements.

### Key Semantics:
1. **Direct Members and Inspection Idioms**: Member names refer to immediate attributes (prefixed with `@`, e.g. `'@id'`, `'@status'`), direct child elements (e.g. `'index'`, `'acceptance'`), or inspection idioms (`'name()'`, `'namespace-uri()'`).
2. **Attribute Predicates on Members**: Member selectors may include attribute predicates to filter child elements or attributes (e.g. `'record[@kind="completion"]'`, `'item[@status="open"]'`). Positional predicates (e.g. `'index[1]'`) are rejected.
3. **Missing Members Ignored**: If a requested member does not exist on a selected element, it is ignored without throwing an error.
4. **Preserves XPath Document Order**: Filtered elements preserve the original document sequence.
5. **No Silent Truncation & Authoritative Limits**: Query results are never silently truncated. If output exceeds system limits, the engine returns an explicit `LIMIT_EXCEEDED` error diagnostic.
   - `MaxDocumentBytes`: 16,777,216 bytes (16 MiB)
   - `MaxOutputBytes`: 4,194,304 bytes (4 MiB)
   - `MaxResultNodes`: 10,000 nodes
   - `MaxProjectedNodes`: 50,000 nodes

### Copyable XPath Examples:

```xpath
ds:filter(/tasks/task[@status='in-progress'], '@id', '@status', 'index')
```

```xpath
ds:filter(/tasks/task/records/record[@kind='completion'], '@id', '@created_at', 'summary')
```

```xpath
ds:filter-out(/tasks/task, 'records')
```

```xpath
ds:filter(/iteration/product/deliverables/deliverable, '@id', 'index')
```

```xpath
ds:filter(/backlog/items/item[@status='open'], '@id', 'index')
```

## XPath Variables & Parameterized CLI Queries

In `query` and `search` CLI commands, use `--var name=value` (repeatable) to bind variables securely. In shell invocations, always quote the `--xpath` argument with single quotes so the shell does not expand `$variable`:

```powershell
dogdouspec query --document "20260823-dogdouspec-v1/tasks.xml" --xpath '/tasks/task[@id=$task_id]/@status' --var task_id=20260823-task-dogfood-review --format xml
```

In `transaction apply` request XML, variables are declared in `<variables><variable name="task_id">value</variable></variables>` and referenced as `$task_id`.

For complex expressions or shells that rewrite arguments, put the exact XPath in a UTF-8 file outside managed XML and use `--xpath-file PATH` instead of `--xpath` (exactly one is required). Both `query` and `search` support it and `--var`. This avoids Git Bash/MSYS path rewriting of XPath beginning with `/`; alternatively configure the shell's argument conversion explicitly.

```powershell
dogdouspec query --document "<ITERATION_ID>/tasks.xml" --xpath-file task-history.xpath --var task_id=<TASK_ID> --format xml
```

An empty node-set normally succeeds with no warning. For diagnostics, `query --warn-empty` emits `EMPTY_QUERY_RESULT` on stderr with the root and direct children; stdout and exit status remain unchanged. Scalar values such as `count(...)=0` are not treated as empty node-sets. Backlog items live at `/backlog/items/item`, not `/backlog/item`.

## Bounded Task History

Use `task records` to inspect persisted records in document order without loading the whole Task:

```powershell
dogdouspec task records --task "<TASK_ID>" --iteration "<ITERATION_ID>" --last 5 --kind verification --fields id,kind,created_at,summary,checks,covers --max-bytes 32768 --format xml
```

`--kind` is repeatable; filtering occurs before selecting the last N records (default 20). `--fields` projects attributes or children. Summary truncation is opt-in with `--max-summary N` and carries explicit truncation metadata. Output reports total, matching and omitted counts and the tasks revision; exceeding `--max-bytes` fails with `LIMIT_EXCEEDED` rather than dropping more records. This view supplements the required `task context` read.

Equivalent flexible XPath remains available:

```xpath
ds:filter(/tasks/task[@id=$task_id]/records/record[@kind='verification'][position() > last()-5], '@id', '@created_at', 'summary', 'checks')
```

## Scope Search

Use `dogdouspec search` to evaluate XPath across multiple documents in a scope:

- **Iteration Scope**:
  ```powershell
  dogdouspec search --scope iteration --iteration "20260823-dogdouspec-v1" --xpath "//term[@key='component']" --format xml
  ```
- **Project Scope**:
  ```powershell
  dogdouspec search --scope project --xpath "//record[@actor='primary-agent']" --format xml
  ```
