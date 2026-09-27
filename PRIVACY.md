# DogdouSpec Privacy Policy

**Effective date:** September 28, 2026

This policy explains what repository information DogdouSpec accesses and
stores, where that information remains, and what choices users have over it.

DogdouSpec is a local-first command-line tool. It does not transmit project
content, collect telemetry, or provide workspace information to the publisher
or other third parties.

The canonical, public version of this policy is available at
[vixasol.com/privacy/dogdouspec](https://vixasol.com/privacy/dogdouspec/).

## 1. Scope and publisher

This policy applies to the DogdouSpec command-line application and the
repository-local files it creates or manages. DogdouSpec is published by
Hangzhou Xueyehu Technology Co., Ltd. under the Vixasol software publisher
namespace.

This policy does not govern third-party applications or services that a user
independently configures to work with a repository.

## 2. Information DogdouSpec accesses

DogdouSpec accesses only information needed to perform commands requested by
the user, including:

- Managed workspace documents under `.dogdouspec`, which may contain project
  configuration, specifications, requirements, tasks, backlog items, findings,
  decisions, summaries, policies, knowledge, evidence metadata, and operation
  history.
- Repository paths and file metadata required for workspace discovery,
  validation, locking, transaction recovery, and scoped reads or writes.
- Local Git status, diff, or revision information when a user invokes a command
  that performs version-control or scope diagnostics. These Git inspections
  are read-only.
- Command-line arguments and local request files explicitly supplied by the
  user.

DogdouSpec does not require personal information. Users should avoid placing
unnecessary personal, confidential, credential, or secret information in
managed documents.

## 3. Storage and retention

Authoritative DogdouSpec state is stored in the `.dogdouspec` directory of the
selected repository. Transaction and recovery data is also staged within that
repository-local state. Generated workflow guidance may be stored under
`.agents/skills/dogdouspec` and in the repository's `AGENTS.md` file.

These files remain until the user edits, archives, or deletes them. DogdouSpec
does not copy this state to a publisher-controlled account or cloud service. If
the repository is committed, backed up, or synchronized, copies are retained
according to the user's selected version-control, backup, or synchronization
service.

## 4. Network communication, telemetry, and diagnostics

- DogdouSpec has no built-in account, cloud synchronization, or background
  service.
- It does not collect telemetry, analytics, advertising identifiers, or usage
  statistics.
- It does not send automatic crash reports or diagnostic logs to the publisher.
- It does not transmit project content or managed workspace state over the
  network.

Command results and diagnostics are written to the local process output.
Verbose diagnostics may include local paths or stack traces and remain visible
only to the user or calling process unless that user or process chooses to
record or transmit them.

## 5. External tools and user-directed sharing

DogdouSpec can be used through user-selected AI coding agents, editors, Git
hosts, continuous integration systems, backup tools, or other services.
DogdouSpec may install local workflow instructions that help an authorized AI
agent query or update managed state, but the DogdouSpec CLI does not send that
state to an AI provider.

An external tool may read or transmit repository content when the user
configures or authorizes it to do so. Those transfers are controlled by the
user and the external tool and are governed by that tool's privacy terms.
Similarly, committing and pushing `.dogdouspec` files to a remote repository is
a user-directed version-control action, not an automatic DogdouSpec
transmission.

Download hosts and package managers, including GitHub and Windows Package
Manager, may process ordinary request information when software is downloaded.
DogdouSpec does not provide repository content to those services during
installation or operation.

## 6. Deleting DogdouSpec data

Users control all persistent DogdouSpec state and can remove it without
contacting the publisher:

1. Stop any DogdouSpec or agent process currently using the repository.
2. Archive or commit any records the user wants to retain.
3. Delete the repository's `.dogdouspec` directory.
4. If removing the integration completely, also remove
   `.agents/skills/dogdouspec` and the DogdouSpec section of `AGENTS.md`.
5. Remove any user-created copies from Git history, backups, or external
   services separately.

Uninstalling the DogdouSpec executable does not automatically delete
repository-local state. There is no DogdouSpec cloud account or publisher-held
workspace copy that users need to close or request for deletion.

## 7. Security responsibilities

DogdouSpec uses repository-local files and the operating system's filesystem
permissions. Users are responsible for controlling access to their
repositories, protecting secrets, reviewing the access granted to external
agents and tools, and configuring remote repositories or backups appropriately.

## 8. Policy changes

Material changes to DogdouSpec's data handling will be reflected in the public
policy by updating the effective date. Historical source changes remain
available through this repository.

## 9. Contact

Privacy questions may be submitted through the
[DogdouSpec issue tracker](https://github.com/XueYeHuLabs/dogdouspec/issues).
Publisher identity and official project links are available on the
[Vixasol publisher identity page](https://vixasol.com/publisher/).
