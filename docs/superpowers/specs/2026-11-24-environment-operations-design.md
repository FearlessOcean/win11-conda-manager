# Environment Creation and Deletion Design (Phase 3)

## Scope

Add creation and deletion to the existing Windows 11 Conda browser. The selected
verified Conda installation supplies the launcher and configuration. Package
installation, import/export, and Conda installation remain outside this phase.

## Creation flow

The main window opens a creation dialog with an environment name, an optional
Python version, an editable parent directory with a folder picker, and a
destination preview that shows the absolute path when inputs are valid. The default parent is the first
writable candidate in the selected installation's `envs_dirs`. Writability is
probed without creating the destination; if no candidate is writable, the user
must choose a parent. A blank version installs the channel's default `python`;
otherwise the accepted version is `major.minor` or `major.minor.patch`.

The dialog accepts a 1–64 character name made of Unicode letters or digits,
underscore, and hyphen, excluding Windows device names. This also excludes
spaces and `#`, which Conda 25.5.1 rejects even with `--prefix`. It validates a
fully qualified parent path. It rejects an existing target file or directory before
starting. The exact previewed destination is passed as one argument to
`conda create --prefix <destination> python[=<version>] --yes --json`; the
application never uses a shell or `--name`. Conda can replace an existing
prefix when `--yes` is set, making the preflight existence check mandatory.
The destination is rechecked immediately before launching.

## Deletion flow

Only a selected, non-base Conda environment can be deleted. The confirmation
shows the full prefix and states that **every file in that directory** will be
removed. The service independently rejects drive/share roots, the selected
base, every known installation root and any ancestor of one, the active
`CONDA_PREFIX`, and paths without `conda-meta/history`. It invokes
`conda env remove --prefix <prefix> --yes --json` through the verified launcher.
Successful deletion must leave the target absent; otherwise the UI reports an
uncertain result for inspection. Process exit codes determine command failure;
successful creation also requires a valid Conda environment marker at the
target. Error JSON from stdout is summarized without displaying command
details or traceback.

## State and persistence

Create/delete operations have a separate busy state and clear progress text
near the action buttons. While busy, installation selection, environment
selection, scans, refresh, and another mutation are disabled. The user can
request cancellation; the UI waits for the Conda child to stop and then
refreshes. Window close is blocked while an operation is still running. An
operation failure or cancellation may leave a partial directory, so the UI
shows the exact path and refreshes the environment list in every outcome.

After successful creation, the application stores the normalized prefix in a
small local JSON file and merges still-existing stored Conda environments into
the browser list. This keeps custom-location environments visible even when
Conda does not register them. Successful creation refreshes and selects the new
prefix; successful deletion removes it from the store, refreshes, and selects
the next available environment. Store failure is reported separately from the
Conda operation result.

## Implementation boundaries and checks

- Core services own validation, path normalization, process arguments, JSON
  result checks, timeouts, root protection, and prefix persistence. WPF owns
  dialogs and confirmation only.
- Create and delete use the same `IProcessRunner` abstraction as discovery.
  User text is never assembled into a command-line string.
- Conda child processes launched by this application receive
  `CONDA_REPORT_ERRORS=false` so an unexpected error does not silently upload
  command and environment details. User Conda configuration is not changed.
- Checks use fake process results and disposable paths inside the repository.
  They cover default choice, preview/argument equality, existing-target
  rejection, base/root/ancestor/active-path protection, non-Conda folders,
  success and failure JSON, cancellation, and post-operation state. They never
  create or delete a real user Conda environment.
- The full solution build, checks, WPF smoke test, and self-contained Windows
  publish must pass.
