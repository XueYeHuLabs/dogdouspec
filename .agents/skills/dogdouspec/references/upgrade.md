# DogdouSpec Upgrade Contract

This document is the authoritative upgrade workflow shipped inside the
DogdouSpec binary. `dogdouspec skill guide --all` prints the current binary's
copy, so callers can recover the correct workflow before synchronizing any
repository files.

## Responsibility Boundary

An upgrade has two owners:

- The CLI owns deterministic, schema-aware filesystem operations. It reports
  exact status and changes only the files named by an explicit mutating command.
- The calling agent owns repository analysis and judgment. It decides how to
  reconcile local conventions, customized guidance, `AGENTS.md`, wrappers,
  build and CI configuration, and Git checkpoints.

The CLI never treats installing a new executable as authorization to mutate a
repository. It never edits `AGENTS.md`, commits, or pushes.

## Required Order

Run these read-only commands from the target repository before synchronization:

```powershell
dogdouspec --version
dogdouspec skill guide --all
dogdouspec workspace discover --format xml
dogdouspec validate --format xml
dogdouspec skill status --format xml
dogdouspec schema status --format xml
git status --short -- .dogdouspec .agents/skills/dogdouspec AGENTS.md
```

The agent then inspects the reported differences and the repository history.
It must identify intentional local Skill changes before allowing an overwrite.
It must also inspect repository references to DogdouSpec, including wrappers,
automation, CI, build scripts, and contributor documentation.

The default Skill path applies when the current repository owns its own Skill.
In a monorepo or nested workspace, repository instructions may deliberately use
one ancestor Skill for several `.dogdouspec` workspaces. Inspect that shared
installation with `skill status --output-dir <shared-skill-path>` and synchronize
it once at its owning scope. Run schema status and validation separately for
each workspace. Do not install duplicate Skill copies merely to make a nested
workspace's default status report green.

After that analysis, the mechanical synchronization commands are:

```powershell
dogdouspec schema sync --expected-version 1.0 --format xml
dogdouspec skill sync --force --format xml
```

`schema sync` refreshes only the known readable XSD copies under
`.dogdouspec/_schema`. Embedded schemas remain authoritative. It does not
migrate managed XML documents between schema versions. If a workspace version
is unsupported or a document migration is required, stop and follow the
version-specific migration instructions shipped by that binary.

`skill sync --force` deterministically writes the current binary's managed
Skill files. It does not merge local customizations. The caller must preserve
or reapply intentional changes based on the pre-sync status, repository history,
and current Guide.

## Repository-Specific Reconciliation

After mechanical synchronization, the agent decides whether to update:

- `AGENTS.md` bootstrap rules and repository-specific build commands;
- references to renamed commands, Skill paths, or deprecated wrappers;
- CI, release, developer setup, and air-gapped deployment scripts;
- local policy text that intentionally extends the shipped Skill;
- version pins and package-manager configuration.

These edits are ordinary repository work. They are not performed by
`schema sync` or `skill sync`.

## Verification and Recovery

Run the repository's normal build and test commands, then complete the upgrade
with:

```powershell
dogdouspec validate --format xml
dogdouspec skill status --format xml
dogdouspec schema status --format xml
git diff --check
git status --short -- .dogdouspec .agents/skills/dogdouspec AGENTS.md
```

Schema status must report no drift after schema synchronization. Skill status
may continue to report intentional repository customizations that the agent
preserved or reapplied; every remaining difference must be classified and
documented. Review every repository diff before committing. If validation or
project tests fail, restore repository files through the project's version
control or prepared backup, then retain the failure evidence for diagnosis.
Package-manager downgrade or executable replacement is controlled by the
caller and is separate from repository rollback.

## Global and Vendored Installations

For a global installation, upgrading the executable changes the CLI used by
every repository on that machine. Each repository still requires its own
read-only assessment and explicit synchronization pass.

For a vendored installation, replace the repository-local executable only
after staging and verifying the candidate. The same Guide-first assessment and
repository reconciliation apply. Compare an installed Skill with its recorded
previous-version baseline when deciding whether it was customized; comparing
an old standard Skill directly with new standard content cannot distinguish a
normal version change from a local modification.
