# Environment Browser Design (Phase 2)

## Goal and scope

Extend the verified Conda installation selector with an environment browser
that has no explicit mutation actions. The user can list known environments, inspect the selected prefix,
see its Python version, and search its installed packages by name and version.
Environment creation, deletion, and package mutation remain later phases.

## User flow

1. Selecting a verified installation clears the previous installation's
   environments and requests `conda env list --json` through its stored
   launcher. Display each normalized absolute prefix once. The selected
   installation root is marked `base`; if Conda omits it from the list, add it
   from the verified `root_prefix`.
2. The environment list shows `base` or the final path component as a display
   name, plus the full prefix. Display names are not identifiers. If a prefix
   disappears on refresh, clear its details and select the first remaining
   environment; otherwise keep the selected prefix.
3. Selecting an environment clears the previous details and requests
   `conda list --prefix <absolute-prefix> --json`. Show full path, package
   count, and a table with name, version, build, and channel. Derive Python
   version from the package whose name is `python`; show “未安装 Python” when
   absent. A case-insensitive search filters package name and version.
4. Empty environments and empty package lists have explicit states. An error
   in the environment request leaves installation selection usable; an error
   in package details keeps the environment list available.

## Architecture

- `CondaEnvironmentCatalog` implements `ICondaEnvironmentCatalog` and owns the
  two CLI queries. It uses `IProcessRunner`, the verified launcher's absolute
  executable and leading arguments, and the existing `CondaJson` parsers.
  It checks exit status before parsing, uses bounded timeouts, and exposes
  concise errors without dumping raw `info` or package JSON.
- `EnvironmentBrowserViewModel` owns environment and package state. It works
  without WPF types so its search and async behavior can be checked with the
  current console test harness. Installation changes, environment changes,
  refresh, and window close cancel superseded requests. A request generation
  prevents late responses from replacing newer selections, including when a
  CLI adapter ignores cancellation.
- `MainWindow` retains the existing installation selector and adds an
  environment list with a detail and package pane. Code-behind only bridges
  WPF selection events to the browser's async methods and cancellation on
  window close. Collection changes occur on the WPF dispatcher context.

## Safety and verification

- The environment's normalized absolute prefix is the only key. `--name` is
  never used for package queries, so custom-location environments work.
- No per-environment package query runs until that environment is selected.
  Refresh queries the environment list once, then the selected details once.
- Tests use fake process results and controlled delayed tasks to verify
  command arguments, base handling, path deduplication, Python extraction,
  search, empty/error states, refresh selection, cancellation, and stale
  response rejection. A live `conda list` against the user's base environment
  is not part of automated checks because Conda may touch package metadata.
- The existing solution verification and self-contained Windows publish must
  pass after integration. A real `env list --json` smoke test is optional and
  should be avoided in automated checks because Conda may rewrite its
  environment registry when pruning stale entries. Conda's package listing may
  also change `conda-meta` records during pip interoperability scanning in
  certain environments, so the application does not promise strict filesystem
  read-only behavior for these standard CLI queries.
