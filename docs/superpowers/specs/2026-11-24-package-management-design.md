# Conda Package Management Design (Phase 4)

## Scope and decisions

The application will install one Conda package by name and optional version,
update one selected Conda package, and remove one selected Conda package in the
selected non-base environment. The selected verified Conda launcher and the
environment's full prefix determine the target. The base environment remains
read-only. pip packages remain visible but cannot be updated or removed through
these controls. Channel configuration, package search across repositories,
multi-package requests, pip operations, and revision rollback are outside this
phase.

Two implementation paths were considered. Direct execution needs one solver run
but hides dependency changes. A dry run followed by confirmation needs two
solver runs and its result can change between runs, but shows the user which
packages Conda plans to link and unlink. The latter is selected. A repository
browser would add another data source and is deferred.

## User flow

The package area keeps its existing search and list. An `安装软件包` action opens
a small form for a Conda package name and optional version. It shows the target
environment's absolute path and explains that configured Conda channels are
used. Names use ASCII letters, digits, `_`, `-`, and `.`, start with a letter
or digit, and are at most 64 characters. A version, when supplied, is a simple
Conda version prefix such as `1.26` or `3.12.2`; it cannot contain whitespace,
operators, channel syntax, or multiple package specs. The UI previews the exact
single argument `name` or `name=version` before the solve.

For update or removal, the user selects one installed package row. A row is
eligible only when its metadata identifies it as Conda-managed. A pip or
unknown-origin row is visibly read-only. Package selection is cleared if a
search filter hides the row, the environment changes, or package data reloads.
The target environment, package name, version, and action are captured before
the preview and checked again before execution. The base environment is
ineligible in both UI and service.

The application runs `conda install|update|remove --prefix <path> <spec>
--dry-run --json` using the verified launcher. A preview dialog lists the
planned installs, replacements and removals using Conda's `LINK` and `UNLINK`
groups. It names the target path and states that other dependencies may change.
Raw channel URLs, tokens, command JSON, and tracebacks are not shown. A no-op
response is reported as such and is never executed. Confirming a nonempty plan
runs the same action with `--yes --json`; the preview is an estimate because
repository or environment state may change before the second solve.

## Core behavior and safety

`CondaPackageOperator` owns request validation, argument construction, process
calls, and JSON parsing. A request identifies an action, verified installation,
absolute environment prefix, package name, and optional version. Update and
remove also carry the selected package metadata so the service rejects pip or
unknown-origin packages independently of the UI. The environment must have a
`conda-meta/history` marker, cannot be the selected or another known Conda
installation root, and cannot use a Windows device/short-name alias or a
reparse-point path. For existing directories, Win32 directory identity checks
protect known roots across alternate path spellings; inconclusive identity
blocks the mutation. The target is revalidated before both process calls.

Both calls use `IProcessRunner` with separate arguments, bounded timeouts,
`CONDA_REPORT_ERRORS=false` for the child only, and cancellation. The preview
requires a successful JSON object; present `prefix` and `dry_run` fields must
match the request. Action arrays contain package records whose names and
versions can be rendered. A successful no-op JSON response may omit `actions`;
an unrecognized or malformed result blocks execution. The execution result
must report success and, if present, the matching prefix. Errors are summarized
to one short line without echoing raw output.

The main window reuses the phase-3 busy banner and cancellation control during
preview and execution, disabling installation/environment selection and other
operations. After execution, ordinary failure, or cancellation it refreshes
the selected environment's package details, because Conda may have partially
changed the environment. If process cleanup is uncertain, further mutations
stay disabled until restart and the user is told to inspect the process and
environment. Close remains blocked while a process is running.

## Verification and delivery

Tests use fake process results and disposable paths inside the repository.
They cover exact argument boundaries, base/root/alias protection, package
source classification, dry-run action parsing and no-op, malformed/failed JSON,
confirmation gating, cancellation, and post-operation refresh. WPF smoke
checks load the new dialogs and controls without mutating a real Conda
environment. The full check suite, solution build, and self-contained `win-x64`
publish must pass. README will explain the Conda-only scope, base read-only
behavior, preview semantics, and manual acceptance steps.

The command flags and dependency effects follow the official Conda
[install](https://docs.conda.io/projects/conda/en/stable/commands/install.html),
[update](https://docs.conda.io/projects/conda/en/stable/commands/update.html), and
[remove](https://docs.conda.io/projects/conda/en/stable/commands/remove.html)
documentation. Conda's guidance on mixing pip and Conda is described in its
[environment management guide](https://docs.conda.io/projects/conda/en/stable/user-guide/tasks/manage-environments.html).
