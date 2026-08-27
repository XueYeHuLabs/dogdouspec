# XPath Query & Projection Reference

DogdouSpec provides a safe, deterministic XPath 1.0 engine with custom projection functions, variable bindings, and strict execution bounds to prevent memory exhaustion and context window overflow.

## Two-Phase Query Pattern

Agents must always follow the two-phase query pattern:

1. **Phase 1: Compact Index Selection**:
   Query minimal attributes and index metadata across candidate tasks:
   ```powershell
   dogdouspec query --document "20260823-dogdouspec-v1/tasks.xml" --xpath "ds:filter(/tasks/task[@status='in-progress' or @status='verification'][1], '@id', '@status', '@agent', 'index')" --format xml
   ```
   If empty, query the first ready pending task whose dependencies are satisfied:
   ```powershell
   dogdouspec query --document "20260823-dogdouspec-v1/tasks.xml" --xpath "ds:filter(/tasks/task[@status='pending' and not(dependencies/ref[@relation='depends-on']/@target = /tasks/task[@status!='done' and @status!='transferred' and @status!='superseded' and @status!='cancelled']/@id)][1], '@id', '@status', '@agent', 'index')" --format xml
   ```

2. **Phase 2: Full Document Loading**:
   Load complete details only for the single selected task:
   ```powershell
   dogdouspec query --document "20260823-dogdouspec-v1/tasks.xml" --xpath "/tasks/task[@id='20260823-task-dogfood-review']" --format xml
   ```

## Projection Functions: `ds:filter` and `ds:filter-out`

DogdouSpec introduces two custom XPath extension functions in the `ds:` namespace:

- `ds:filter(nodeset, member1, member2, ...)`: Returns a projected clone containing only the specified member attributes and child elements.
- `ds:filter-out(nodeset, member1, member2, ...)`: Returns a projected clone excluding the specified member attributes and child elements.

### Key Semantics:
1. **Direct Members Only**: Member names refer to immediate attributes (prefixed with `@`, e.g. `'@id'`, `'@status'`) or direct child elements (e.g. `'index'`, `'acceptance'`). Nested deep paths (like `'index/summary'`) are not member selectors.
2. **Missing Members Ignored**: If a requested member does not exist on a selected element, it is ignored without throwing an error.
3. **Preserves XPath Document Order**: Filtered elements preserve the original document sequence.
4. **No Silent Truncation & Authoritative Limits**: Query results are never silently truncated. If output exceeds system limits, the engine returns an explicit `LIMIT_EXCEEDED` error diagnostic.
   - `MaxDocumentBytes`: 16,777,216 bytes (16 MiB)
   - `MaxOutputBytes`: 4,194,304 bytes (4 MiB)
   - `MaxResultNodes`: 10,000 nodes
   - `MaxProjectedNodes`: 50,000 nodes

### Copyable XPath Examples:

```xpath
ds:filter(/tasks/task[@status='in-progress'], '@id', '@status', 'index')
```

```xpath
ds:filter-out(/tasks/task, 'records')
```

```xpath
ds:filter(/iteration/product/deliverables/deliverable, '@id', 'index')
```

```xpath
ds:filter(/tasks/task[@status='pending' and not(dependencies/ref[@relation='depends-on']/@target = /tasks/task[@status!='done' and @status!='transferred' and @status!='superseded' and @status!='cancelled']/@id)][1], '@id', '@status', 'index')
```

## XPath Variables & Parameterized CLI Queries

In `query` and `search` CLI commands, use `--var name=value` (repeatable) to bind variables securely. In shell invocations, always quote the `--xpath` argument with single quotes so the shell does not expand `$variable`:

```powershell
dogdouspec query --document "20260823-dogdouspec-v1/tasks.xml" --xpath '/tasks/task[@id=$task_id]/@status' --var task_id=20260823-task-dogfood-review --format xml
```

In `transaction apply` request XML, variables are declared in `<variables><variable name="task_id">value</variable></variables>` and referenced as `$task_id`.

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