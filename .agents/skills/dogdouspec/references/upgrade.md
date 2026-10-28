# DogdouSpec Upgrade Contract

DogdouSpec provides upgrade facts and convenient tools for collaboration.
The calling agent owns repository analysis and judgment: whether to overwrite,
preserve, reapply or retire a customization. Advice and history are not approval
gates. Installing a binary does not itself change repository files.

## Read What You Need

After replacing a global or vendored binary, a useful assessment sequence is:

```powershell
dogdouspec --version
dogdouspec workspace discover --format xml
dogdouspec skill status --format xml
dogdouspec schema status --format xml
dogdouspec skill guide --file references/upgrade.md
dogdouspec validate --format xml
```

Use `skill guide --file <path>` for one reference, `skill guide --all` for the
complete guide, or `skill export --output-dir <new-directory>` for UTF-8 files.
Older binaries without file selection still support `skill guide --all`.
Status-first assessment avoids loading the whole Skill. Read this short upgrade
guide after changing binaries even when files match; file status cannot describe
every CLI behavior change.

In a shared-ancestor installation, use `--output-dir <shared-skill-path>` on
Skill commands and synchronize at the owning scope. Do not install duplicate Skill copies
merely to make a nested workspace's default status green. Validate and inspect
schema copies separately for each workspace.

## Baseline, Differences and Optional History

This release adds `.skill-state.xml` inside the Skill directory. Commit it with
the Skill. Its `baseline` records the writing CLI version, UTC sync time and
per-file SHA-256 digests; UTF-8 content uses CRLF-to-LF normalization only.
Its `history` stores optional free-text explanations. Metadata is excluded from
Skill content comparison. `workspace init` stamps a new installation, while an
existing or mixed installation retains its previous provenance.

`skill status` preserves `matching`, `modified`, `missing`, `extra` and exit codes
0 (matching) / 1 (differences). Additional provenance describes `pristine`,
`outdated`, `locally-modified`, `unknown-provenance`, new and retired files.
`automatic_upgrade` is advisory:

- `ready`: differing target files have no detected local changes against the baseline.
- `review-needed`: local changes would be overwritten while an embedded update is available.
- `unknown`: the available baseline cannot establish upgrade conditions.
- `not-needed`: target content already matches, or no new embedded changes exist.

Local differences can remain after a successful upgrade with retained customization.
Unknown provenance does not mean customized. Old history does not mean a file
is still customized. Missing history does not mean unmodified.

```powershell
dogdouspec skill diff --format unified
dogdouspec skill records --last 5 --format xml
dogdouspec schema diff --format unified
```

Diffs compare local content with the current binary, require no Git, and write
nothing. Limit output with `skill diff --file references/authority.md` or
`schema diff --name tasks`. Both accept `--format xml`; exit 1 means differences,
not command failure. Baseline hashes do not reconstruct old text or provide a
three-way merge. Retired files are compared but preserved by sync.
If a local file cannot be read as UTF-8 text, diff identifies the file and returns
an error without partial output. XML diff output preserves the same payload as
unified output, including schema CRLF/LF differences. These comparison errors do
not introduce an additional approval requirement for `sync --force`.

Users and agents edit Skill Markdown directly. After customization, optionally
explain the reason and outcome; no template or file association is required:

```powershell
dogdouspec skill record --summary "Added the repository's independent review convention to references/authority.md."
```

`record` appends history without resetting the baseline. `records` reads recent
entries without writing. Summaries help the next agent find the appropriate
place to preserve the intent in newer guidance; the CLI does not interpret them.

## Synchronize and Reconcile

For uncustomized files, inspect the update and synchronize when desired:

```powershell
dogdouspec skill sync --force --format xml
dogdouspec schema sync --expected-version 1.0 --format xml
```

For customized files, a useful workflow is to inspect diff/history, keep old
content for comparison, sync the new version, then reapply the intended changes
to the new Markdown and optionally record the result. The caller can also adopt
the new content unchanged. Neither history nor provenance blocks `--force`.
No discard acknowledgement or automatic merge is provided.

`skill sync` and `skill export` write the current embedded files, update the
baseline after writing, and preserve history and unmanaged files. They never
edit `AGENTS.md`, wrappers or CI. `schema sync` refreshes optional readable XSD
copies; embedded schemas remain authoritative and managed XML is not migrated.
Unsupported document versions require the applicable migration workflow.

Reconcile repository-specific guidance as appropriate. Selected or resumed tasks
should read `task context --no-history`, including iteration and task constraints.
Use `task next`, `task records`, `task record --check-*`, `query/search --xpath-file`,
`query --warn-empty`, `transaction apply --dry-run` and `iteration create --file`
where useful. Preserve raw request contracts and owner/review boundaries.

Backlog progress can now use `backlog record --id <ID> --summary "..."` and
`backlog records --id <ID> --last 5`. Lifecycle commands accept optional summaries
and `--check-*` evidence. Existing no-summary invocations/replays remain supported;
no XML migration or new closure prerequisite is introduced.

## Verification and Recovery

Review discovery adds `task review status` and `task review records --last 5`.
Use `task review record --task <ID> --summary "..."` for actual review notes,
with optional actor/result. Task context exposes compact review facts and a status
command. These helpers reuse current XML; existing formal submissions and error
codes remain compatible, with clearer next-command guidance. Recorded results
are optional collaboration metadata and do not decide lifecycle or approval.

Recheck `validate`, `skill status`, `schema status` and the repository diff.
For Git repositories, `git diff --check` and `git status --short -- .dogdouspec
.agents/skills/dogdouspec AGENTS.md` help review the result. Git is not required
for Skill inspection, recording or synchronization.

Choose build/tests based on what changed and repository rules. Independent
guidance and unused schema copies may need only the checks above, with the
reason recorded when useful. Code, build inputs, wrappers and CI changes need
appropriate verification. In DogdouSpec's own source repository, Skill Markdown
and XSDs are embedded build inputs, so run `build.cmd`.

The caller handles backups, repository rollback and executable downgrade as
needed. Review and checkpoint with existing Git-write authorization; DogdouSpec
does not stage, commit, push or decide that a customization should be discarded.
