# DogdouSpec v1 XPath Query Contract - Review Draft

Status: Superseded Review Draft

Superseded by the XPath and output sections of `V1_CLI_CONTRACT.md`.

The XPath variable and projection behavior remains under review. The document
paths, Goal examples, and transaction-helper examples predate the
iteration-first demo and must not be treated as the current v1 layout.

This document defines the first v1 query contract review slice. It extends .NET XPath 1.0 only where structural projection or safe value binding is otherwise unavailable.

## 1. Evaluation model

The CLI compiles and evaluates the complete expression through `System.Xml.XPath`. A custom `XsltContext` provides DogdouSpec variables and extension functions.

The CLI does not parse or execute standard XPath axes, predicates, unions, or composition itself. Once an extension function returns a node set, the .NET XPath engine owns all further XPath evaluation.

The fixed extension namespace is:

```text
prefix: ds
URI: urn:dogdouspec:xpath:functions:1
```

The prefix is pre-bound by the CLI and does not need to appear in managed XML documents.

## 2. Variables

V1 supports named string variables:

```powershell
dogdouspec query `
  --document work:ITER-2026-001:goal `
  --var task_id=T-002 `
  --xpath '//task[@id=$task_id]'
```

Variable names use lowercase ASCII letters, digits, and underscores and must begin with a letter. The `$` is used in XPath but is omitted from `--var`.

All CLI-bound variables are XPath strings in v1. XPath may explicitly convert them:

```xpath
//task[number(@cost) <= number($maximum_cost)]
```

Duplicate variable names, unbound variables, and invalid names are query errors. Variable binding is preferred over concatenating values into an XPath expression.

Variable names are case-sensitive. `--var` splits at the first `=`; the
remaining text is the complete value, and an empty string value is permitted.

## 3. Structural projection functions

### 3.1 Signatures

```xpath
ds:filter(node-set, member, ...)
ds:filter-out(node-set, member, ...)
```

Both functions return an XPath node set. They are deterministic, read-only, and have no filesystem, network, environment, command, or managed-document mutation capability.

The first argument must be a node set containing only element nodes. An empty node set returns an empty node set. Any non-element input is a query error.

At least one member argument is required. Member arguments are XPath strings.

### 3.2 Member grammar

V1 members select only the current element's own direct attributes or direct child elements:

```text
@attribute-name
child-element-name
```

Examples:

```text
@id
@status
index
title
records
```

Members do not support `/`, `//`, `.`, `..`, predicates, axes, functions, wildcards, or namespace prefixes. Managed v1 documents have no XML namespace, so members are matched by exact name.

The caller uses the first XPath argument to select the desired scope. The projection function then operates only on each selected element's own members.

### 3.3 `ds:filter`

`ds:filter` keeps:

- The selected root element.
- Only named direct attributes.
- Only named direct child elements, including their complete subtrees.

Direct text owned by the selected root is preserved. Managed schemas prohibit
mixed content, so this primarily preserves the value of a selected leaf
element; large narrative content should remain inside a named child element.

Example:

```xpath
ds:filter(
  //task[not(@status='done')],
  '@id',
  '@status',
  'index'
)
```

Given:

```xml
<task id="T-002" status="pending">
  <index>...</index>
  <title>Define XPath projection behavior</title>
  <context>Large context...</context>
  <records>Large history...</records>
</task>
```

The projected element is:

```xml
<task id="T-002" status="pending">
  <index>...</index>
</task>
```

### 3.4 `ds:filter-out`

`ds:filter-out` keeps the selected root element and all of its members except the named direct attributes and direct child elements.

Example:

```xpath
ds:filter-out(
  //task[@id=$task_id],
  '@updated_at',
  'context',
  'records'
)
```

### 3.5 Missing and duplicate members

A syntactically valid member that does not exist on an input element is ignored. It does not change result cardinality and is not a diagnostic.

Duplicate member arguments are treated as one member.

These rules allow one query recipe to operate safely over schema-compatible elements with optional members.

### 3.6 Ordering and identity

Input roots retain their XPath result order. Attributes and child elements retain their source ordering as represented by the managed XML serializer. A selected child subtree is preserved in full.

Projection never mutates the source document. The implementation may use a materialized projected tree or a virtual `XPathNavigator`, provided observable XPath behavior is conformant.

A projected node is a read-only query value. It is not a mutation address. Mutations must target a managed document with a fresh XPath, stable object ID, expected document revision, and exact match assertion.

### 3.7 Composition

Extension results remain normal XPath node sets. The CLI imposes no top-level-only or no-nesting restriction.

Examples:

```xpath
ds:filter(//task, '@id', '@status', 'index')
  [index/term[@key='priority' and @value='p0']]
```

```xpath
ds:filter(//task, '@id', 'index')/index/term[@key='topic']
```

```xpath
ds:filter-out(
  ds:filter(//task, '@id', '@status', 'index', 'title'),
  'title'
)
```

Standard XPath processing after each function call belongs to the .NET XPath engine.

## 4. Query output

V1 query formats are `xml`, `json`, and `human`. Skills use `xml` for XML node-set reads and `json` when a stable machine envelope is more useful.

### 4.1 Compact XML node-set result

An all-element node set uses one compact wrapper and embeds the returned elements directly:

```xml
<results
  document="work:ITER-2026-001:goal"
  revision="7"
  type="node-set"
  derived="true">
  <task id="T-002" status="pending">
    <index>...</index>
  </task>
  <task id="T-003" status="pending">
    <index>...</index>
  </task>
</results>
```

There is no per-element metadata wrapper by default. This keeps the ratio of requested XML to transport metadata high.

`derived="true"` means at least one extension function produced a projected query value. The wrapper path is not a writable source path.

### 4.2 Other XPath result types

Scalar results use one compact value element:

```xml
<result
  document="work:ITER-2026-001:goal"
  revision="7"
  type="boolean">true</result>
```

Attribute, text, comment, processing-instruction, or mixed node sets use typed item wrappers because XML cannot embed those nodes as independent document children without losing identity.

### 4.3 Result limits

The CLI never silently truncates query results or text values.

If the final node count, intermediate projection budget, or serialized byte limit is exceeded, the query fails with a stable diagnostic and returns no partial success payload. The diagnostic recommends a narrower XPath or `ds:filter` projection.

Composition and nesting remain available, but all intermediate projections share one query budget. This prevents repeated projection functions from causing unbounded allocation.

## 5. Index-first Skill recipe

The standard unfinished-Task index query is:

```xpath
ds:filter(
  //task[not(
    @status='done'
    or @status='transferred'
    or @status='superseded'
    or @status='cancelled'
  )],
  '@id',
  '@status',
  'index'
)
```

The first unfinished Task is:

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
  'index'
)
```

After choosing `T-002`, the Agent reads the complete Task:

```xpath
//task[@id=$task_id]
```

No persisted next-Task pointer participates in this workflow.

## 6. Transaction helper preview

XPath is read-only. Mutations are performed by the transaction command:

```text
dogdouspec transaction apply --file transaction.xml
```

Versioned helpers may provide concise schema-aware atomic actions:

```powershell
dogdouspec transaction invoke task.claim `
  --document work:ITER-2026-001:goal `
  --expected-revision 7 `
  --operation-id OP-CLAIM-T002-001 `
  --var task_id=T-002 `
  --var agent=codex
```

`task.claim` is helper identity and audit metadata. The transaction engine still performs generic assertions and XML operations. The complete helper and transaction schema is deferred to the second contract slice.
